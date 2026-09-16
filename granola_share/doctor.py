"""`granola-share doctor` / `granola-share client doctor`: say the quiet failures out loud.

Every check is a small pure-ish function with its IO injected (HTTP, sockets, filesystem,
Granola), so the tests run with fakes and the real command runs with httpx & friends.
"""

from __future__ import annotations

import os
import socket
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

import httpx

from . import autostart
from .config import ClientConfig, Config
from .wizard import ACCOUNT_LOOKBACK_DAYS, granola_account_check

MARKS = {True: "✓", False: "✗", None: "–"}


@dataclass
class Check:
    name: str
    ok: bool | None  # None = not applicable / skipped
    detail: str
    hint: str = ""


# --- small IO helpers (all replaceable) ----------------------------------------

def http_get(url: str, headers: dict | None = None, timeout: float = 10) -> httpx.Response:
    return httpx.get(url, headers=headers or {}, timeout=timeout)


def port_open(host: str, port: int, timeout: float = 1.0) -> bool:
    try:
        with socket.create_connection((host, port), timeout=timeout):
            return True
    except OSError:
        return False


def dir_writable(path: Path) -> bool:
    try:
        path.mkdir(parents=True, exist_ok=True)
        probe = path / ".granola-share-write-test"
        probe.write_text("ok")
        probe.unlink()
        return True
    except OSError:
        return False


def store_note_count(cfg: Config) -> int | None:
    try:
        from .store import Store

        store = Store(cfg.db_path, cfg.pool_dir)
        count = getattr(store, "count", None)
        return int(count()) if count else sum(n for _, n in store.classes_summary())
    except Exception:
        return None


def _fetch(http_get: Callable, url: str, headers: dict | None = None, timeout: float = 10) -> tuple[int | None, dict, str]:
    """(status, json body or {}, error text). Never raises."""
    try:
        r = http_get(url, headers=headers or {}, timeout=timeout)
    except Exception as e:  # httpx errors, connection refused, ...
        return None, {}, str(e) or e.__class__.__name__
    try:
        body = r.json() if r.status_code == 200 else {}
    except Exception:
        body = {}
    return r.status_code, body if isinstance(body, dict) else {}, ""


def last_log_line(path: Path) -> str | None:
    if not path.exists():
        return None
    try:
        lines = [ln.strip() for ln in path.read_text(errors="replace").splitlines() if ln.strip()]
    except OSError:
        return None
    return lines[-1] if lines else ""


# --- shared checks --------------------------------------------------------------

def check_login(cfg: Config | ClientConfig, account_check, *, cmd: str) -> tuple[Check, Check]:
    """Granola login + "notes in the last 30 days"; the second check is skipped when not signed in."""
    if not cfg.tokens_path.exists():
        return (Check("Granola login", False, "not signed in", f"run `{cmd}`"),
                Check("Notes in Granola", None, "skipped (not signed in)"))
    info, n = account_check(cfg)
    if info is None:
        return (Check("Granola login", False, "tokens exist but Granola did not answer (expired session?)",
                      f"run `{cmd}` to sign in again"),
                Check("Notes in Granola", None, "skipped (could not reach Granola)"))
    who = str(info.get("email") or "unknown account")
    ws = info.get("workspace")
    login = Check("Granola login", True, f"Signed in as {who}" + (f" · {ws}" if ws else ""))
    if n > 0:
        notes = Check("Notes in Granola", True, f"found {n} note{'' if n == 1 else 's'} in the last {ACCOUNT_LOOKBACK_DAYS} days")
    else:
        notes = Check("Notes in Granola", False, f"0 notes in the last {ACCOUNT_LOOKBACK_DAYS} days for {who}",
                      f"if your lectures live in a different Google account, run `{cmd}` and pick that one")
    return login, notes


def check_autostart(role: str, autostart_installed) -> Check:
    path = autostart_installed(role)
    if path:
        return Check("Autostart", True, f"installed: {path}")
    return Check("Autostart", False, "not installed",
                 f"run `granola-share autostart install --role {role}` to keep it running in the background")


def check_log(log_path: Path) -> Check:
    line = last_log_line(log_path)
    if line is None:
        return Check("Log file", None, f"no log yet at {log_path}")
    if not line:
        return Check("Log file", None, f"{log_path} is empty")
    return Check("Log file", True, f"{log_path}\n    last line: {line[:160]}")


# --- the friend's laptop ---------------------------------------------------------

def client_doctor(cc: ClientConfig, *, http_get=http_get, account_check=granola_account_check, port_open=port_open,
                  autostart_installed=lambda role: autostart.installed_path(role)) -> list[Check]:
    checks: list[Check] = []

    # 1. config
    if not cc.config_path.exists():
        checks.append(Check("Config", False, f"{cc.config_path} does not exist", "run `granola-share client setup`"))
    elif not cc.server_url:
        checks.append(Check("Config", False, "no server_url in client.toml", "run `granola-share client setup`"))
    else:
        checks.append(Check("Config", True, f"{cc.config_path} · server {cc.server_url} · mode {cc.mode}"))

    # 2. server reachable + password
    if cc.server_url:
        url = cc.server_url.rstrip("/") + "/api/health"
        status, body, err = _fetch(http_get, url, {"Authorization": f"Bearer {cc.pool_key}"}, timeout=15)
        if status == 200:
            checks.append(Check("Pool server", True,
                                f"{cc.server_url} · pool '{body.get('pool_name', '?')}' · {body.get('notes', '?')} notes in the pool"))
        elif status == 401:
            checks.append(Check("Pool server", False, f"{cc.server_url} rejected the pool password",
                                "ask the pool owner for the password and run `granola-share client setup`"))
        elif status is None:
            checks.append(Check("Pool server", False, f"could not reach {url}: {err}",
                                "is the server running and are you on the same Tailscale network?"))
        else:
            checks.append(Check("Pool server", False, f"unexpected response {status} from {url}"))
    else:
        checks.append(Check("Pool server", None, "skipped (no server_url)"))

    # 3 + 4. Granola login and notes
    checks.extend(check_login(cc, account_check, cmd="granola-share client login"))

    # 5. control panel
    if cc.panel_enabled:
        status, body, err = _fetch(http_get, cc.panel_url + "/api/state", timeout=3)
        if status == 200:
            waiting = (body.get("counts") or {}).get("waiting")
            extra = f" · {waiting} waiting" if isinstance(waiting, int) else ""
            checks.append(Check("Control panel", True, f"answering at {cc.panel_url}{extra}"))
        elif port_open("127.0.0.1", cc.panel_port):
            checks.append(Check("Control panel", False, f"port {cc.panel_port} is taken by something that is not the panel",
                                "change panel_port in client.toml or stop the other program"))
        else:
            checks.append(Check("Control panel", False, f"not running at {cc.panel_url}",
                                "not running — start with `granola-share client run`"))
    else:
        checks.append(Check("Control panel", None, "disabled in client.toml (panel_enabled = false)"))

    # 6. autostart
    checks.append(check_autostart("client", autostart_installed))

    # 7. log
    checks.append(check_log(cc.log_dir / "client.log"))
    return checks


# --- the pool server ---------------------------------------------------------------

def _model_installed(model: str, installed: list[str]) -> bool:
    if model in installed or f"{model}:latest" in installed:
        return True
    return ":" not in model and any(m.split(":")[0] == model for m in installed)


def server_doctor(cfg: Config, *, http_get=http_get, account_check=granola_account_check, writable=dir_writable,
                  note_count=store_note_count,
                  autostart_installed=lambda role: autostart.installed_path(role)) -> list[Check]:
    checks: list[Check] = []

    # 1. config
    if cfg.config_path.exists():
        checks.append(Check("Config", True, f"{cfg.config_path} · pool '{cfg.pool_name}'"))
    else:
        checks.append(Check("Config", False, f"{cfg.config_path} does not exist", "run `granola-share setup`"))

    # 2. pool_dir
    if not cfg.pool_dir.is_absolute():
        checks.append(Check("Pool folder", False, f"{cfg.pool_dir} is not an absolute path",
                            "set pool_dir in config.toml to a full path, e.g. /Users/you/GranolaShare"))
    elif writable(cfg.pool_dir):
        checks.append(Check("Pool folder", True, f"{cfg.pool_dir} (writable)"))
    else:
        checks.append(Check("Pool folder", False, f"{cfg.pool_dir} is not writable", "check the folder's permissions"))

    # 3. web port
    url = f"http://127.0.0.1:{cfg.web_port}/api/health"
    status, body, err = _fetch(http_get, url, {"Authorization": f"Bearer {cfg.pool_password}"}, timeout=5)
    if status == 200:
        checks.append(Check("Web server", True, f"answering on port {cfg.web_port} · pool '{body.get('pool_name', '?')}'"))
    elif status == 401:
        checks.append(Check("Web server", False, f"port {cfg.web_port} answers but rejects this config's password",
                            "another granola-share (or an older config) is running there; restart it with `granola-share run`"))
    elif status is None:
        checks.append(Check("Web server", False, f"nothing answering on port {cfg.web_port}",
                            "start it with `granola-share run` (or `granola-share autostart install --role server`)"))
    else:
        checks.append(Check("Web server", False, f"unexpected response {status} from {url}"))

    # 4. Ollama
    if not cfg.ollama_enabled:
        checks.append(Check("Ollama", None, "AI sorting disabled (folder/title rules only)"))
    else:
        status, body, err = _fetch(http_get, cfg.ollama_host.rstrip("/") + "/api/tags", timeout=5)
        if status != 200:
            checks.append(Check("Ollama", False, f"not reachable at {cfg.ollama_host}",
                                "install it from https://ollama.com and make sure it is running; notes fall back to rules meanwhile"))
        else:
            installed = [str(m.get("name", "")) for m in body.get("models", []) if isinstance(m, dict)]
            if _model_installed(cfg.ollama_model, installed):
                checks.append(Check("Ollama", True, f"reachable · model {cfg.ollama_model} installed"))
            else:
                checks.append(Check("Ollama", False, f"reachable, but model {cfg.ollama_model} is not installed"
                                    + (f" (have: {', '.join(installed)})" if installed else ""),
                                    f"run `ollama pull {cfg.ollama_model}`"))

    # 5. classes
    names = cfg.class_names()
    if names:
        checks.append(Check("Classes", True, ", ".join(names)))
    else:
        checks.append(Check("Classes", False, "none configured; everything lands in Unsorted",
                            "add [[classes]] to config.toml or rerun `granola-share setup`"))

    # 6. server sync
    if not cfg.server_sync:
        checks.append(Check("Server sync", None, "off (friends push their own notes)"))
        checks.append(Check("Notes in Granola", None, "skipped (server sync off)"))
    else:
        login, notes = check_login(cfg, account_check, cmd="granola-share login")
        login.name = "Server sync"
        checks.extend((login, notes))

    # 7. autostart
    checks.append(check_autostart("server", autostart_installed))

    # 8. notes in the pool
    n = note_count(cfg)
    if n is None:
        checks.append(Check("Notes in the pool", False, f"could not open {cfg.db_path}"))
    else:
        checks.append(Check("Notes in the pool", True if n else None, f"{n} note{'' if n == 1 else 's'} filed"
                            + ("" if n else " (nothing shared yet)")))
    return checks


# --- output --------------------------------------------------------------------------

def render(checks: list[Check]) -> str:
    """One line per check: `✓ Name: detail`, with the hint indented under failures."""
    width = max((len(c.name) for c in checks), default=0)
    lines = []
    for c in checks:
        lines.append(f"{MARKS[c.ok]} {c.name.ljust(width)}  {c.detail}")
        if c.hint and c.ok is not True:
            lines.append(f"{' ' * (width + 4)}→ {c.hint}")
    failed = sum(1 for c in checks if c.ok is False)
    lines.append("")
    lines.append("All good." if not failed else f"{failed} problem{'' if failed == 1 else 's'} found.")
    return "\n".join(lines)


def failed(checks: list[Check]) -> bool:
    return any(c.ok is False for c in checks)
