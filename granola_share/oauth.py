"""OAuth 2.1 login against Granola's MCP auth server.

Flow: discover the auth server from the MCP resource metadata, register a public
client once (dynamic client registration), then authorization-code + PKCE with a
localhost callback. Tokens are stored in the granola-share home directory and
refreshed automatically.
"""

from __future__ import annotations

import base64
import hashlib
import http.server
import json
import os
import secrets
import time
import urllib.parse
import webbrowser
from pathlib import Path

import httpx

from .config import Config


class OAuthError(Exception):
    pass


def _b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def make_pkce() -> tuple[str, str]:
    """Return (code_verifier, code_challenge) using S256."""
    verifier = _b64url(secrets.token_bytes(48))
    challenge = _b64url(hashlib.sha256(verifier.encode()).digest())
    return verifier, challenge


def _write_private(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2))
    os.chmod(path, 0o600)


class GranolaOAuth:
    def __init__(self, cfg: Config, http: httpx.Client | None = None):
        self.cfg = cfg
        self.http = http or httpx.Client(timeout=30)
        self._meta: dict | None = None

    # -- discovery ---------------------------------------------------------
    def metadata(self) -> dict:
        if self._meta:
            return self._meta
        u = urllib.parse.urlsplit(self.cfg.mcp_url)
        origin = f"{u.scheme}://{u.netloc}"
        r = self.http.get(f"{origin}/.well-known/oauth-protected-resource")
        r.raise_for_status()
        servers = r.json().get("authorization_servers") or []
        if not servers:
            raise OAuthError("MCP resource metadata lists no authorization server")
        issuer = servers[0].rstrip("/")
        r = self.http.get(f"{issuer}/.well-known/oauth-authorization-server")
        r.raise_for_status()
        self._meta = r.json()
        return self._meta

    @property
    def redirect_uri(self) -> str:
        return f"http://localhost:{self.cfg.oauth_callback_port}/callback"

    # -- client registration -----------------------------------------------
    def client_id(self) -> str:
        path = self.cfg.client_path
        if path.exists():
            data = json.loads(path.read_text())
            if data.get("redirect_uri") == self.redirect_uri:
                return data["client_id"]
        meta = self.metadata()
        body = {
            "client_name": "granola-share",
            "redirect_uris": [
                self.redirect_uri,
                f"http://127.0.0.1:{self.cfg.oauth_callback_port}/callback",
            ],
            "grant_types": ["authorization_code", "refresh_token"],
            "response_types": ["code"],
            "token_endpoint_auth_method": "none",
            "scope": "mcp offline_access",
        }
        r = self.http.post(meta["registration_endpoint"], json=body)
        if r.status_code >= 400:
            raise OAuthError(f"client registration failed: {r.status_code} {r.text}")
        cid = r.json()["client_id"]
        _write_private(path, {"client_id": cid, "redirect_uri": self.redirect_uri})
        return cid

    # -- tokens ------------------------------------------------------------
    def load_tokens(self) -> dict | None:
        p = self.cfg.tokens_path
        return json.loads(p.read_text()) if p.exists() else None

    def save_tokens(self, tok: dict) -> None:
        tok = dict(tok)
        if "expires_in" in tok and "expires_at" not in tok:
            tok["expires_at"] = time.time() + float(tok["expires_in"])
        _write_private(self.cfg.tokens_path, tok)

    def logout(self) -> None:
        if self.cfg.tokens_path.exists():
            self.cfg.tokens_path.unlink()

    def is_logged_in(self) -> bool:
        return self.load_tokens() is not None

    def access_token(self) -> str:
        tok = self.load_tokens()
        if not tok:
            raise OAuthError("not logged in: run `granola-share login`")
        if tok.get("expires_at", 0) - 60 < time.time():
            tok = self.refresh(tok)
        return tok["access_token"]

    def refresh(self, tok: dict) -> dict:
        rt = tok.get("refresh_token")
        if not rt:
            raise OAuthError("access token expired and no refresh token: run `granola-share login`")
        meta = self.metadata()
        r = self.http.post(
            meta["token_endpoint"],
            data={
                "grant_type": "refresh_token",
                "refresh_token": rt,
                "client_id": self.client_id(),
                "resource": self.cfg.mcp_url,
            },
        )
        if r.status_code >= 400:
            raise OAuthError(f"token refresh failed ({r.status_code}): {r.text}. Run `granola-share login`.")
        new = r.json()
        new.setdefault("refresh_token", rt)
        self.save_tokens(new)
        return self.load_tokens() or new

    # -- interactive login -------------------------------------------------
    def build_authorize_url(self, state: str, challenge: str) -> str:
        meta = self.metadata()
        params = {
            "response_type": "code",
            "client_id": self.client_id(),
            "redirect_uri": self.redirect_uri,
            "scope": "mcp offline_access",
            "state": state,
            "code_challenge": challenge,
            "code_challenge_method": "S256",
            "resource": self.cfg.mcp_url,
        }
        return meta["authorization_endpoint"] + "?" + urllib.parse.urlencode(params)

    def exchange_code(self, code: str, verifier: str) -> dict:
        meta = self.metadata()
        r = self.http.post(
            meta["token_endpoint"],
            data={
                "grant_type": "authorization_code",
                "code": code,
                "redirect_uri": self.redirect_uri,
                "client_id": self.client_id(),
                "code_verifier": verifier,
                "resource": self.cfg.mcp_url,
            },
        )
        if r.status_code >= 400:
            raise OAuthError(f"token exchange failed ({r.status_code}): {r.text}")
        tok = r.json()
        self.save_tokens(tok)
        return tok

    def login(self, open_browser: bool = True, timeout: int = 300, log=print) -> dict:
        verifier, challenge = make_pkce()
        state = secrets.token_urlsafe(16)
        url = self.build_authorize_url(state, challenge)
        received: dict = {}

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):  # silence
                pass

            def do_GET(self):  # noqa: N802
                parsed = urllib.parse.urlparse(self.path)
                if parsed.path != "/callback":
                    self.send_response(404)
                    self.end_headers()
                    return
                q = urllib.parse.parse_qs(parsed.query)
                received.update({k: v[0] for k, v in q.items()})
                self.send_response(200)
                self.send_header("Content-Type", "text/html")
                self.end_headers()
                self.wfile.write(
                    b"<h2>granola-share is connected.</h2><p>You can close this tab.</p>"
                )

        server = http.server.HTTPServer(("127.0.0.1", self.cfg.oauth_callback_port), Handler)
        server.timeout = 1
        log("Open this URL to sign in to Granola:\n\n  " + url + "\n")
        if open_browser:
            try:
                webbrowser.open(url)
            except Exception:
                pass
        deadline = time.time() + timeout
        try:
            while time.time() < deadline and "code" not in received and "error" not in received:
                server.handle_request()
        finally:
            server.server_close()
        if "error" in received:
            raise OAuthError(f"login denied: {received.get('error')} {received.get('error_description', '')}")
        if "code" not in received:
            raise OAuthError("timed out waiting for the browser callback")
        if received.get("state") != state:
            raise OAuthError("state mismatch in OAuth callback")
        tok = self.exchange_code(received["code"], verifier)
        log("Logged in. Tokens saved to " + str(self.cfg.tokens_path))
        return tok
