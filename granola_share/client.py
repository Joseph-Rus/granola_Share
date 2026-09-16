"""The friend's laptop client.

Watches the user's own Granola account (official MCP, their own login) and, for each
finished note, either queues it in the local control panel (`ask`, the default), shares it
straight away (`auto`) or asks with the legacy native popup (`dialog`), then POSTs approved
notes to the pool server's /api/ingest.

Deciding is decoupled from polling: the poll loop fetches, asks the server for a class
guess, writes the note body under `queue/<id>.json`, marks it `queued` and moves on. The
panel (panel.py) calls `share()` / `skip()` whenever the human gets round to it.
"""

from __future__ import annotations

import asyncio
import json
import os
import tempfile
import threading
import time
import traceback
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Callable

import httpx

from . import dialogs
from .config import UNSORTED, ClientConfig
from .granola import GranolaClient, Meeting, meeting_from_dict, meeting_to_dict
from .ui import plural

try:  # added by granola.py in v0.2; keep working against an older module
    from .granola import account_label
except ImportError:  # pragma: no cover - only while the two files are out of step
    def account_label(info: dict | None) -> str:
        if not info or not info.get("email"):
            return "not signed in"
        ws = info.get("workspace")
        return f"{info['email']} · {ws}" if ws else str(info["email"])

OVERLAP_DAYS = 2
DONE = ("shared", "skipped")
WAITING = ("queued", "pending")
LOGIN_ERROR_HINTS = ("not logged in", "no refresh token", "token exchange failed", "invalid_grant")


@dataclass
class ClientReport:
    listed: int = 0
    considered: int = 0
    shared: list[tuple[str, str]] = field(default_factory=list)  # (title, class)
    skipped: list[str] = field(default_factory=list)
    pending: list[str] = field(default_factory=list)
    queued: list[str] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)
    account: dict | None = None


@dataclass
class QueuedNote:
    """One row of the control panel's queue."""

    id: str
    title: str
    date: str
    queued_at: str
    guess: dict | None = None
    error: str | None = None
    decision: str = "queued"


def http_post(url: str, payload: dict, headers: dict) -> dict:
    r = httpx.post(url, json=payload, headers=headers, timeout=180)
    r.raise_for_status()
    return r.json()


def check_server(server_url: str, pool_key: str) -> dict:
    """GET /api/health; raises on any failure with a readable message."""
    url = server_url.rstrip("/") + "/api/health"
    try:
        r = httpx.get(url, headers={"Authorization": f"Bearer {pool_key}"}, timeout=15)
    except httpx.HTTPError as e:
        raise RuntimeError(f"could not reach {url}: {e}") from e
    if r.status_code == 401:
        raise RuntimeError("the server rejected the pool password")
    if r.status_code != 200:
        raise RuntimeError(f"unexpected response {r.status_code} from {url}")
    return r.json()


# --- pure helpers (unit tested) -------------------------------------------

def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def atomic_write(path: Path, text: str) -> None:
    """Write via a temp file in the same directory + os.replace, so a crash never leaves half a file."""
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    try:
        with os.fdopen(fd, "w") as fh:
            fh.write(text)
        os.replace(tmp, path)
    except BaseException:
        try:
            os.unlink(tmp)
        except OSError:
            pass
        raise


def default_status() -> dict:
    return {
        "server": {"ok": False, "pool_name": "", "classes": [], "notes": 0, "error": None, "checked_at": None},
        "account": None,
        "listed": 0,
        "since": None,
        "last_checked": None,
        "last_error": None,
        "login": {"state": "idle", "message": ""},
    }


def load_state_text(text: str | None) -> dict:
    """Parse client_state.json; tolerate v0.1 files (no `status`) and garbage."""
    data: dict = {}
    if text:
        try:
            parsed = json.loads(text)
            if isinstance(parsed, dict):
                data = parsed
        except json.JSONDecodeError:
            pass
    seen = data.get("seen") if isinstance(data.get("seen"), dict) else {}
    status = default_status()
    saved = data.get("status") if isinstance(data.get("status"), dict) else {}
    for k, v in saved.items():
        if isinstance(status.get(k), dict) and isinstance(v, dict):
            status[k] = {**status[k], **v}
        else:
            status[k] = v
    status["login"] = {"state": "idle", "message": ""}  # a login never survives a restart
    return {"seen": seen, "last_poll": data.get("last_poll"), "status": status}


def slim_account(info: dict | None) -> dict | None:
    """The four identity fields the panel shows (drop the raw tool payload)."""
    if not info or not isinstance(info, dict):
        return None
    return {"email": str(info.get("email") or ""), "workspace": str(info.get("workspace") or ""),
            "workspace_id": str(info.get("workspace_id") or ""),
            "scopes": [str(s) for s in (info.get("scopes") or [])]}


def is_weak_guess(guess: dict | None) -> bool:
    return not guess or not guess.get("class_name") or guess.get("class_name") == UNSORTED


def looks_like_login_error(error: str | None) -> bool:
    e = (error or "").lower()
    return any(h in e for h in LOGIN_ERROR_HINTS)


# --- the client ---------------------------------------------------------------

class ShareClient:
    def __init__(self, cc: ClientConfig, granola: GranolaClient, *, ask=None, notify=None, post=None, log=print,
                 health: Callable[[str, str], dict] | None = None):
        self.cc = cc
        self.granola = granola
        self.ask = ask or dialogs.ask_yes_no
        self.notify = notify or dialogs.notify
        self.post = post or http_post
        self.health = health or check_server
        self.log = log
        self.lock = threading.RLock()
        self.wake = threading.Event()
        self._login_thread: threading.Thread | None = None
        self.state = self._load_state()

    # -- state -------------------------------------------------------------
    def _load_state(self) -> dict:
        p = self.cc.state_path
        return load_state_text(p.read_text() if p.exists() else None)

    def _save_state(self) -> None:
        with self.lock:
            atomic_write(self.cc.state_path, json.dumps(self.state, indent=2))

    @property
    def status_dict(self) -> dict:
        return self.state["status"]

    def _set_status(self, **fields: Any) -> None:
        with self.lock:
            self.status_dict.update(fields)
            self._save_state()

    def _mark(self, note_id: str, decision: str, *, title: str | None = None, date_: str | None = None,
              clear: tuple[str, ...] = (), **extra: Any) -> dict:
        """Update the `seen` entry for a note (keeps unrelated fields such as `guess`)."""
        with self.lock:
            entry = dict(self.state["seen"].get(note_id, {}))
            for k in clear:
                entry.pop(k, None)
            if title is not None:
                entry["title"] = title
            if date_ is not None:
                entry["date"] = date_
            entry.update({"decision": decision, "at": now_iso(), **extra})
            self.state["seen"][note_id] = entry
            self._save_state()
            return entry

    def _since(self) -> date:
        last = self.state.get("last_poll")
        if last:
            return datetime.fromisoformat(last).date() - timedelta(days=OVERLAP_DAYS)
        return date.today() - timedelta(days=self.cc.share_lookback_days)

    # -- queue bodies --------------------------------------------------------
    def _body_path(self, note_id: str) -> Path:
        safe = "".join(ch for ch in note_id if ch.isalnum() or ch in "-_.") or "note"
        return self.cc.queue_dir / f"{safe}.json"

    def _has_body(self, note_id: str) -> bool:
        return self._body_path(note_id).exists()

    def _write_body(self, m: Meeting, guess: dict | None) -> None:
        body = meeting_to_dict(m) | {"guess": guess, "queued_at": now_iso()}
        with self.lock:
            atomic_write(self._body_path(m.id), json.dumps(body, indent=2))

    def _read_body(self, note_id: str) -> dict | None:
        p = self._body_path(note_id)
        with self.lock:
            if not p.exists():
                return None
            try:
                data = json.loads(p.read_text())
            except json.JSONDecodeError:
                return None
        return data if isinstance(data, dict) else None

    def _drop_body(self, note_id: str) -> None:
        with self.lock:
            try:
                self._body_path(note_id).unlink()
            except FileNotFoundError:
                pass

    # -- decisions ---------------------------------------------------------
    def decide(self, m: Meeting) -> bool | None:
        if self.cc.mode == "auto":
            return True
        pool = self.cc.pool_name or "the notes pool"
        text = f"Granola finished notes for:\n\n“{m.title}”\n{m.date[:10]}\n\nShare it to {pool}?"
        return self.ask("granola-share", text, timeout=self.cc.dialog_timeout_seconds)

    def _payload(self, m: Meeting) -> dict:
        payload = meeting_to_dict(m)
        payload["owner"] = self.cc.display_name or m.owner
        if not self.cc.include_transcripts:
            payload["transcript"] = ""
        return payload

    def _headers(self) -> dict:
        return {"Authorization": f"Bearer {self.cc.pool_key}"}

    def _url(self, path: str) -> str:
        return self.cc.server_url.rstrip("/") + path

    def push(self, m: Meeting, class_name: str | None = None) -> dict:
        payload = self._payload(m)
        if class_name:
            payload["class_name"] = class_name
        return self.post(self._url("/api/ingest"), payload, self._headers())

    def preview(self, m: Meeting) -> dict | None:
        """Ask the server how it would file this note. Best effort: None on any failure."""
        try:
            res = self.post(self._url("/api/preview"), self._payload(m), self._headers())
        except Exception as e:
            self.log(f"[client] preview failed for '{m.title}': {e}")
            return None
        return res if isinstance(res, dict) else None

    def _note_url(self, note_id: str) -> str:
        return self._url("/note/" + note_id)

    def _share_now(self, m: Meeting, class_name: str | None = None) -> dict:
        """Push one note and record the outcome. Raises on failure (after marking it `pending`)."""
        try:
            res = self.push(m, class_name)
        except Exception as e:
            self._mark(m.id, "pending", title=m.title, date_=m.date, error=str(e))
            self.log(f"[client] could not share '{m.title}': {e}")
            raise
        cls = str((res or {}).get("class_name") or class_name or "?")
        self._mark(m.id, "shared", title=m.title, date_=m.date, clear=("error",),
                   class_name=cls, url=self._note_url(m.id))
        self._drop_body(m.id)
        self.log(f"[client] shared '{m.title}' → {cls}")
        return res

    # -- panel actions (thread-safe) ----------------------------------------
    def queued_note(self, note_id: str) -> Meeting | None:
        body = self._read_body(note_id)
        if body is None:
            return None
        try:
            return meeting_from_dict(body)
        except ValueError:
            return None

    def share(self, note_id: str, class_name: str | None = None) -> dict:
        m = self.queued_note(note_id)
        if m is None:
            raise KeyError(f"nothing queued with id {note_id!r}")
        return self._share_now(m, class_name or None)

    def skip(self, note_id: str) -> dict:
        with self.lock:
            entry = self.state["seen"].get(note_id)
            body = self._read_body(note_id) or {}
            if entry is None and not body:
                raise KeyError(f"nothing queued with id {note_id!r}")
            entry = self._mark(note_id, "skipped", title=body.get("title") or (entry or {}).get("title", ""),
                               date_=body.get("date") or (entry or {}).get("date", ""), clear=("error",))
            self._drop_body(note_id)
        self.log(f"[client] skipped '{entry.get('title', note_id)}'")
        return entry

    def share_all(self) -> dict:
        out: dict = {"shared": [], "errors": []}
        for q in self.pending():
            try:
                self.share(q.id, (q.guess or {}).get("class_name") if not is_weak_guess(q.guess) else None)
                out["shared"].append(q.id)
            except Exception as e:
                out["errors"].append(f"{q.id}: {e}")
        return out

    def skip_all(self) -> int:
        n = 0
        for q in self.pending():
            try:
                self.skip(q.id)
                n += 1
            except KeyError:
                pass
        return n

    def pending(self) -> list[QueuedNote]:
        """Notes waiting in the panel (queued, or a failed share that can be retried), newest first."""
        with self.lock:
            rows = []
            for note_id, e in self.state["seen"].items():
                if e.get("decision") not in WAITING or not self._has_body(note_id):
                    continue
                body = self._read_body(note_id) or {}
                rows.append(QueuedNote(
                    id=note_id, title=str(e.get("title") or body.get("title") or "Untitled"),
                    date=str(e.get("date") or body.get("date") or ""),
                    queued_at=str(body.get("queued_at") or e.get("at") or ""),
                    guess=e.get("guess") if e.get("guess") is not None else body.get("guess"),
                    error=e.get("error"), decision=str(e.get("decision")),
                ))
        rows.sort(key=lambda q: (q.date, q.queued_at), reverse=True)
        return rows

    def history(self, limit: int = 50) -> list[dict]:
        with self.lock:
            rows = [
                {"id": note_id, "title": e.get("title", ""), "date": e.get("date", ""), "decision": e["decision"],
                 "class_name": e.get("class_name"), "url": e.get("url"), "at": e.get("at", "")}
                for note_id, e in self.state["seen"].items() if e.get("decision") in DONE
            ]
        rows.sort(key=lambda r: r["at"], reverse=True)
        return rows[:limit]

    def logged_in(self) -> bool | None:
        """True/False from the OAuth token file; None when the granola client cannot tell us."""
        fn = getattr(getattr(self.granola, "oauth", None), "is_logged_in", None)
        if fn is None:
            return None
        try:
            return bool(fn())
        except Exception:
            return None

    def status(self) -> dict:
        with self.lock:
            st = json.loads(json.dumps(self.status_dict))
            seen = self.state["seen"].values()
            counts = {
                "found": int(st.get("listed") or 0),
                "shared": sum(1 for e in seen if e.get("decision") == "shared"),
                "skipped": sum(1 for e in seen if e.get("decision") == "skipped"),
                "waiting": len(self.pending()),
            }
        next_at = None
        if st.get("last_checked"):
            try:
                d = datetime.fromisoformat(st["last_checked"]) + timedelta(seconds=self.cc.poll_interval_seconds)
                next_at = d.isoformat(timespec="seconds")
            except ValueError:
                pass
        logged_in = self.logged_in()
        st.update({"counts": counts, "mode": self.cc.mode, "next_check_at": next_at, "panel_url": self.cc.panel_url,
                   "server_url": self.cc.server_url, "pool_name": self.cc.pool_name,
                   "poll_interval_seconds": self.cc.poll_interval_seconds, "logged_in": logged_in,
                   "login_needed": logged_in is False or looks_like_login_error(st.get("last_error"))})
        return st

    # -- polling -------------------------------------------------------------
    def _refresh_server(self) -> None:
        try:
            h = self.health(self.cc.server_url, self.cc.pool_key)
            server = {"ok": True, "pool_name": str(h.get("pool_name") or ""), "classes": [str(c) for c in h.get("classes") or []],
                      "notes": int(h.get("notes") or 0), "error": None, "checked_at": now_iso()}
        except Exception as e:
            server = {**self.status_dict["server"], "ok": False, "error": str(e), "checked_at": now_iso()}
            self.log(f"[client] server check failed: {e}")
        self._set_status(server=server)

    async def _account(self, session) -> dict | None:
        fn = getattr(self.granola, "get_account_info", None)
        if fn is None:
            return None
        try:
            return slim_account(await fn(session))
        except Exception as e:
            self.log(f"[client] get_account_info failed: {e}")
            return None

    def _todo(self, stubs: list[Meeting]) -> list[Meeting]:
        seen = self.state["seen"]
        out = []
        for m in stubs:
            decision = seen.get(m.id, {}).get("decision")
            if decision in DONE:
                continue
            if self.cc.mode == "ask" and decision in WAITING and self._has_body(m.id):
                continue  # already sitting in the panel
            out.append(m)
        return out

    async def poll_once(self, since: date | None = None) -> ClientReport:
        rep = ClientReport()
        self._refresh_server()
        since = since or self._since()
        self._set_status(since=since.isoformat())
        try:
            async with self.granola.session() as s:
                rep.account = await self._account(s)
                self._set_status(account=rep.account)
                stubs = await self.granola.list_meetings(s, since=since)
                rep.listed = len(stubs)
                todo = self._todo(stubs)
                rep.considered = len(todo)
                self.log(f"[client] {len(stubs)} notes since {since}, {len(todo)} to consider")
                if not stubs:
                    self.log(f"[client] signed in as {account_label(rep.account)} · 0 notes found since {since}")
                full_by_id: dict[str, Meeting] = {}
                if todo:
                    try:
                        full_by_id = {m.id: m for m in await self.granola.get_meetings(s, [m.id for m in todo])}
                    except Exception as e:
                        rep.errors.append(f"get_meetings: {e}")
                        self.log(f"[client] get_meetings failed, using list data: {e}")
                for stub in todo:
                    m = full_by_id.get(stub.id) or stub
                    if not m.notes_markdown:
                        m.notes_markdown = stub.notes_markdown
                    if self.cc.include_transcripts and not m.transcript:
                        m.transcript = await self.granola.get_transcript(s, m.id)
                    self._handle(m, rep)
            with self.lock:
                self.state["last_poll"] = datetime.now(timezone.utc).isoformat()
                self._set_status(listed=rep.listed, last_checked=now_iso(), last_error=None)
        except Exception as e:
            self._set_status(last_checked=now_iso(), last_error=str(e))
            raise
        if rep.queued and self.cc.notifications:
            self.notify("granola-share", f"{plural(len(rep.queued), 'note')} ready to review · {self.cc.panel_url}")
        return rep

    def _handle(self, m: Meeting, rep: ClientReport) -> None:
        """Decide what to do with one fetched note according to the mode."""
        if self.cc.mode == "ask":
            guess = self.preview(m)
            self._write_body(m, guess)
            self._mark(m.id, "queued", title=m.title, date_=m.date, clear=("error",), guess=guess)
            rep.queued.append(m.title)
            self.log(f"[client] queued '{m.title}' (guess: {(guess or {}).get('class_name') or 'none'})")
            return
        decision = self.decide(m)
        if decision is None:
            rep.pending.append(m.title)
            self._mark(m.id, "pending", title=m.title, date_=m.date)
            return
        if not decision:
            rep.skipped.append(m.title)
            self._mark(m.id, "skipped", title=m.title, date_=m.date)
            self.log(f"[client] skipped '{m.title}'")
            return
        try:
            res = self._share_now(m)
        except Exception as e:
            rep.errors.append(f"{m.id}: {e}")
            rep.pending.append(m.title)
            self._write_body(m, None)  # retryable from the panel
            return
        cls = str(res.get("class_name", "?"))
        rep.shared.append((m.title, cls))
        if self.cc.notifications:
            self.notify("granola-share", f"Shared “{m.title}” → {cls}")

    # -- loop / wake / relogin ------------------------------------------------
    def request_poll(self) -> None:
        self.wake.set()

    def _wait(self, stop) -> None:
        end = time.monotonic() + self.cc.poll_interval_seconds
        while True:
            if stop is not None and stop.is_set():
                return
            remaining = end - time.monotonic()
            if remaining <= 0 or self.wake.wait(timeout=min(1.0, remaining)):
                return

    def run_loop(self, stop=None) -> None:
        while True:
            self.wake.clear()
            try:
                asyncio.run(self.poll_once())
            except Exception as e:
                self.log(f"[client] error: {e}\n{traceback.format_exc()}")
            self._wait(stop)
            if stop is not None and stop.is_set():
                return

    def relogin(self, open_browser: bool = True) -> threading.Thread:
        """Run the OAuth login in the background (the browser opens); poll again when it finishes."""
        with self.lock:
            if self._login_thread is not None and self._login_thread.is_alive():
                return self._login_thread

            def go() -> None:
                oauth = getattr(self.granola, "oauth", None)
                if oauth is None:
                    self._set_status(login={"state": "error", "message": "this client has no OAuth helper"})
                    return
                self._set_status(login={"state": "waiting", "message": "Finish signing in in the browser window…"})
                try:
                    oauth.login(open_browser=open_browser, log=self.log)
                except Exception as e:
                    self._set_status(login={"state": "error", "message": str(e)})
                    self.log(f"[client] login failed: {e}")
                    return
                self._set_status(login={"state": "ok", "message": "Signed in. Checking for notes…"}, last_error=None)
                self.log("[client] signed in; checking for notes")
                self.request_poll()

            self._login_thread = threading.Thread(target=go, name="granola-relogin", daemon=True)
            self._login_thread.start()
            return self._login_thread

