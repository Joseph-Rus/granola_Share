"""The friend's local control panel (http://127.0.0.1:8790), served next to `client run`.

One page that answers the four questions this week's silent failures hid: connected? as
whom? what is waiting? what did it do? Notes queue here instead of in a native popup that
vanishes after five minutes, so a decision can wait until the human is ready — the poll
loop never waits on a person.

Localhost only, so the whole protection is a per-process CSRF token on every form plus a
Host-header check: a page left open across a restart cannot act, and nothing on the network
can reach the panel at all.
"""

from __future__ import annotations

import json
import secrets
import socket
import threading
from dataclasses import asdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.parse import quote

from fastapi import FastAPI, Form, HTTPException, Request
from fastapi.responses import HTMLResponse, RedirectResponse

from .client import is_weak_guess
from .config import UNSORTED
from .ui import (alert, dot, esc, fmt_date, layout, pill, plural, rel_time, render_note_markdown, stat)

BRAND = "granola-share"
NO_NOTES_WARNING = ("Connected, but this account has 0 notes. "
                    "If your lectures live in a different Google account, switch.")
UNSORTED_LABEL = "Unsorted — pick a class"
LOG_NAME = "client.log"
LOG_TAIL_LINES = 200
HISTORY_LIMIT = 200
HISTORY_IN_STATE = 20
POLL_SECONDS = 15
LOOPBACK = ("127.0.0.1", "localhost", "::1", "[::1]")

# One token per process: a page still open after the panel restarts is stale and its POSTs
# are rejected rather than acting on a queue that has moved on.
TOKEN = secrets.token_urlsafe(24)


# --- pure helpers (unit tested) -------------------------------------------

def allowed_host(host: str | None, port: int) -> bool:
    """True only for loopback (optionally `:port`) — blocks DNS-rebinding style POSTs."""
    if not host:
        return False
    name, _, got_port = host.rpartition(":")
    if not name:  # no colon at all, or "[::1]" style without a port
        name, got_port = host, ""
    if name.lower() not in LOOPBACK:
        return False
    return got_port in ("", str(int(port)))


def next_check_minutes(st: dict, now_secs: float | None = None) -> int:
    """Whole minutes until the next poll (at least 1), from `next_check_at` or the interval."""
    interval = int(st.get("poll_interval_seconds") or 0)
    secs = interval
    at = st.get("next_check_at")
    if at:
        try:
            d = datetime.fromisoformat(str(at))
        except ValueError:
            d = None
        if d is not None:
            if d.tzinfo is None:
                d = d.replace(tzinfo=timezone.utc)
            now = now_secs if now_secs is not None else datetime.now(timezone.utc).timestamp()
            secs = d.timestamp() - now
    return max(1, int((secs + 59) // 60))


def header_tone(st: dict) -> str:
    """Green = connected, amber = waiting on you (or nothing found), clay = trouble."""
    server = st.get("server") or {}
    if not server.get("ok") or st.get("login_needed") or st.get("last_error"):
        return "bad"
    counts = st.get("counts") or {}
    if counts.get("waiting") or not (st.get("account") or {}).get("email") or st.get("listed") == 0:
        return "warn"
    return "ok"


def hidden(token: str) -> str:
    return f'<input type=hidden name=t value="{esc(token, quote=True)}">'


def form_button(action: str, label: str, token: str, kind: str = "") -> str:
    """A POST button (never a link: a GET must not change anything)."""
    return (f'<form class=inline method=post action="{esc(action, quote=True)}">{hidden(token)}'
            f'<button class="btn sm {kind}">{esc(label)}</button></form>')


def guess_pill(guess: dict | None) -> str:
    """The pool's class guess, or the amber nudge when it could not tell."""
    if is_weak_guess(guess):
        return pill(UNSORTED_LABEL, "warn", title="the pool could not tell — pick a class")
    name = str((guess or {}).get("class_name"))
    conf = (guess or {}).get("confidence")
    try:
        detail = f" ({float(conf):.2f})" if conf is not None else ""
    except (TypeError, ValueError):
        detail = ""
    return pill(name, "ok", title=f"the pool would file this as {name}{detail}")


def class_options(classes: list[str], guess: dict | None) -> str:
    """Options for the per-note class select, pre-selected to the guess (else Unsorted)."""
    names: list[str] = []
    for n in list(classes or []) + [UNSORTED]:
        if n and n not in names:
            names.append(n)
    selected = None if is_weak_guess(guess) else str((guess or {}).get("class_name"))
    if selected and selected not in names:
        names.insert(0, selected)
    chosen = selected or UNSORTED
    return "".join(
        f'<option value="{esc(n, quote=True)}"{" selected" if n == chosen else ""}>{esc(n)}</option>'
        for n in names
    )


def poll_script(waiting: Any, last_checked: str | None, seconds: int = POLL_SECONDS) -> str:
    """Reload when the watcher has done something (a new note, or a finished check)."""
    seed = json.dumps({"waiting": int(waiting or 0), "last_checked": last_checked}).replace("<", "\\u003c")
    return (
        "<script>(function(){var was=" + seed + ";setInterval(function(){"
        "fetch('/api/state',{cache:'no-store'}).then(function(r){return r.ok?r.json():null}).then(function(d){"
        "if(!d)return;var w=(d.counts||{}).waiting;"
        "if(w!==was.waiting||d.last_checked!==was.last_checked){location.reload();}"
        "}).catch(function(){});}," + str(int(seconds) * 1000) + ");})();</script>"
    )


def log_tail(path: Path, lines: int = LOG_TAIL_LINES) -> list[str]:
    """Last `lines` lines of the client log; [] when there is no log yet."""
    try:
        text = path.read_text(errors="replace")
    except OSError:
        return []
    return text.splitlines()[-max(1, lines):]


# --- rendering ---------------------------------------------------------------

def status_header(st: dict) -> str:
    """The signed-in email as the biggest thing on the page, then Found / Shared / Waiting."""
    acct = st.get("account") or {}
    counts = st.get("counts") or {}
    meta = []
    if acct.get("workspace"):
        meta.append(f"workspace {acct['workspace']}")
    pool = (st.get("server") or {}).get("pool_name") or st.get("pool_name") or ""
    if pool:
        meta.append(f"pool {pool}")
    meta.append("last checked " + rel_time(st.get("last_checked")))
    who = (f'<div class=who><div class=id>{dot(header_tone(st))}{esc(acct.get("email") or "not signed in")}</div>'
           f'<div class=meta>{" · ".join(esc(x) for x in meta)}</div></div>')
    waiting = int(counts.get("waiting") or 0)
    stats = (f'<div class=stats>{stat(counts.get("found", 0), "Found")}'
             f'{stat(counts.get("shared", 0), "Shared")}'
             f'{stat(waiting, "Waiting", "warn" if waiting else "")}</div>')
    return f"<div class=status>{who}{stats}</div>"


def callouts(st: dict, token: str) -> str:
    """Say the quiet failures out loud, each with the button that fixes it."""
    out = []
    server = st.get("server") or {}
    if not server.get("ok"):
        err = str(server.get("error") or "not checked yet")
        hint = ("The pool password on this laptop does not match the server's — rerun "
                "<span class=mono>granola-share client setup</span>."
                if "password" in err.lower() else
                f"Is the pool server running at <span class=mono>{esc(str(st.get('server_url') or ''))}</span>?")
        out.append(alert(f"<b>Cannot reach the notes pool.</b> <span class=mono>{esc(err)}</span> {hint}", "bad"))
    login = st.get("login") or {}
    acct = st.get("account") or {}
    if login.get("state") == "waiting":
        out.append(alert(esc(str(login.get("message") or "Finish signing in in the browser window…")), "info", "i"))
    elif st.get("login_needed"):
        out.append(alert("<b>Not signed in to Granola.</b> Nothing can be listed until you sign in. "
                         + form_button("/relogin", "Sign in", token, "primary"), "bad"))
    elif acct.get("email") and st.get("listed") == 0:
        out.append(alert(f"<b>{esc(NO_NOTES_WARNING)}</b> Signed in as {esc(str(acct['email']))}. "
                         + form_button("/relogin", "Sign in as someone else", token), "warn"))
    if login.get("state") == "error":
        out.append(alert(f"Signing in failed: <span class=mono>{esc(str(login.get('message') or ''))}</span> "
                         + form_button("/relogin", "Try again", token), "bad"))
    elif login.get("state") == "ok" and login.get("message"):
        out.append(alert(esc(str(login["message"])), "ok", "✓"))
    if st.get("last_error"):
        out.append(alert(f"The last check failed: <span class=mono>{esc(str(st['last_error']))}</span>", "bad"))
    return "".join(out)


def queue_item(q, classes: list[str], token: str) -> str:
    """One waiting note: title, date, guessed class, the class select and Share / Skip."""
    nid = quote(q.id, safe="")
    meta = [f"<span>{esc(fmt_date(q.date))}</span>", guess_pill(q.guess)]
    if q.decision == "pending" and q.error:
        meta.append(pill("share failed", "bad", title=str(q.error)))
    body = (f'<div class=body><div class=title><a href="/note/{nid}">{esc(q.title)}</a></div>'
            f'<div class=meta>{"".join(meta)}</div>')
    if q.decision == "pending" and q.error:
        body += f'<div class="small mono" style="color:var(--bad)">{esc(str(q.error))}</div>'
    body += "</div>"
    actions = (
        f'<div class=actions><form class=row method=post action="/share/{nid}">{hidden(token)}'
        f'<select class=sm name=class_name aria-label="class">{class_options(classes, q.guess)}</select>'
        f'<button class="btn sm primary">Share</button></form>'
        + form_button(f"/skip/{nid}", "Skip", token, "quiet") + "</div>"
    )
    return f"<div class=item>{body}{actions}</div>"


def queue_section(st: dict, rows: list, token: str) -> str:
    classes = [str(c) for c in (st.get("server") or {}).get("classes") or []]
    if not rows:
        extra = ""
        if st.get("mode") != "ask":
            mode = esc(str(st.get("mode")))
            extra = f'<div class="muted small">Notes are shared without asking (mode: {mode}).</div>'
        return (
            '<div class=section-head><h2>Nothing waiting</h2><div class=actions>'
            + form_button("/check-now", "Check now", token) + "</div></div>"
            f'<div class=list><div class=empty>Nothing waiting. Next check in {next_check_minutes(st)} min.{extra}'
            "</div></div>"
        )
    head = (f'<div class=section-head><h2>{esc(plural(len(rows), "note"))} waiting to share</h2>'
            f'<div class=actions>{form_button("/share-all", "Share all", token, "primary")}'
            f'{form_button("/skip-all", "Skip all", token, "quiet")}'
            f'{form_button("/check-now", "Check now", token)}</div></div>')
    items = "".join(queue_item(q, classes, token) for q in rows)
    return head + f"<div class=list>{items}</div>"


def history_table(rows: list[dict]) -> str:
    if not rows:
        return "<div class=empty>Nothing shared or skipped yet.</div>"
    trs = []
    for r in rows:
        shared = r.get("decision") == "shared"
        name = str(r.get("class_name") or UNSORTED)
        filed = pill(name, "ok" if name != UNSORTED else "warn") if shared else pill("skipped")
        url = str(r.get("url") or "")
        link = (f'<a href="{esc(url, quote=True)}">open in pool ↗</a>' if shared and url
                else '<span class=faint>—</span>')
        trs.append(f"<tr><td>{esc(fmt_date(r.get('date')))}</td>"
                   f"<td>{esc(str(r.get('title') or 'Untitled'))}</td><td>{filed}</td><td>{link}</td>"
                   f"<td class=muted>{esc(rel_time(r.get('at')))}</td></tr>")
    return ("<table><tr><th>Date</th><th>Note</th><th>Filed as</th><th></th><th>When</th></tr>"
            + "".join(trs) + "</table>")


def log_body(lines: list[str], path: Path) -> str:
    if not lines:
        return alert(f"No log yet at <span class=mono>{esc(str(path))}</span>. It fills up while "
                     "<span class=mono>granola-share client run</span> is watching.", "info", "i")
    text = esc("\n".join(lines))
    return (f'<p class=muted>Last {len(lines)} lines of <span class=mono>{esc(str(path))}</span></p>'
            f"<div class=note><pre>{text}</pre></div>")


def note_body(m, q, token: str, classes: list[str]) -> str:
    """The queued note itself, rendered, with the same Share / Skip as the queue row."""
    # Notes can contain HTML (Granola scopes include notes other people shared with you), and this
    # page carries the token that can share the whole queue — so the note is escaped, never live.
    rendered = render_note_markdown(m.notes_markdown or "_This note has no text._")
    guess = q.guess if q is not None else None
    meta = [f"<span>{esc(fmt_date(m.date, with_year=True))}</span>", guess_pill(guess)]
    if m.attendees:
        meta.append(f"<span>{esc(', '.join(str(a) for a in m.attendees))}</span>")
    nid = quote(m.id, safe="")
    actions = (f'<form class=row method=post action="/share/{nid}" style="margin-top:.6rem">{hidden(token)}'
               f'<label class=muted for=class_name>Share as</label>'
               f'<select class=sm id=class_name name=class_name>{class_options(classes, guess)}</select>'
               f'<button class="btn sm primary">Share</button></form>')
    return (f'<div class="row muted small">{"".join(meta)}</div>{actions}'
            f"<div class=note>{rendered}</div>"
            f'<p class=small><a href="/">← back to the queue</a></p>')


# --- the app -----------------------------------------------------------------

def create_panel(client) -> FastAPI:
    """The control panel over one ShareClient. GETs only read; every POST needs the token."""
    app = FastAPI(title="granola-share panel")
    cc = client.cc

    def nav() -> list[tuple[str, str]]:
        items = [("Waiting", "/"), ("History", "/history"), ("Log", "/log")]
        if cc.server_url:
            items.append(("Open the pool ↗", cc.server_url))
        return items

    def page(title: str, body: str, active: str = "", st: dict | None = None) -> HTMLResponse:
        head = poll_script((st.get("counts") or {}).get("waiting"), st.get("last_checked")) if st else ""
        return HTMLResponse(layout(title, body, brand=BRAND, host=f"localhost:{cc.panel_port}",
                                   nav=nav(), active=active, head=head))

    def guard(request: Request, token: str) -> None:
        """Only a form from this very panel may change anything."""
        if not allowed_host(request.headers.get("host"), cc.panel_port):
            raise HTTPException(403, "the control panel only answers on localhost")
        if not secrets.compare_digest(str(token or ""), TOKEN):
            raise HTTPException(403, "this page is out of date — reload the panel and try again")

    def back() -> RedirectResponse:
        return RedirectResponse("/", status_code=303)

    @app.get("/", response_class=HTMLResponse)
    def waiting():
        st = client.status()
        body = status_header(st) + callouts(st, TOKEN) + queue_section(st, client.pending(), TOKEN)
        return page("Waiting", body, active="/", st=st)

    @app.get("/history", response_class=HTMLResponse)
    def history():
        st = client.status()
        body = (status_header(st) + "<h1>History</h1>"
                + history_table(client.history(HISTORY_LIMIT)))
        return page("History", body, active="/history", st=st)

    @app.get("/log", response_class=HTMLResponse)
    def log():
        path = cc.log_dir / LOG_NAME
        return page("Log", "<h1>Log</h1>" + log_body(log_tail(path), path), active="/log")

    @app.get("/note/{note_id}", response_class=HTMLResponse)
    def note(note_id: str):
        m = client.queued_note(note_id)
        if m is None:
            raise HTTPException(404, "that note is not in the queue any more")
        q = next((x for x in client.pending() if x.id == note_id), None)
        classes = [str(c) for c in (client.status().get("server") or {}).get("classes") or []]
        return page(m.title or "Note", f"<h1>{esc(m.title or 'Note')}</h1>" + note_body(m, q, TOKEN, classes))

    @app.get("/api/state")
    def api_state():
        return client.status() | {"pending": [asdict(q) for q in client.pending()],
                                  "history": client.history(HISTORY_IN_STATE)}

    @app.post("/share/{note_id}")
    def share(note_id: str, request: Request, t: str = Form(""), class_name: str = Form("")):
        guard(request, t)
        try:
            client.share(note_id, class_name or None)
        except KeyError:
            client.log(f"[panel] nothing queued with id {note_id!r} any more")
        except Exception as e:  # marked `pending`: the queue row now shows the error
            client.log(f"[panel] share failed: {e}")
        return back()

    @app.post("/skip/{note_id}")
    def skip(note_id: str, request: Request, t: str = Form("")):
        guard(request, t)
        try:
            client.skip(note_id)
        except KeyError:
            client.log(f"[panel] nothing queued with id {note_id!r} any more")
        return back()

    @app.post("/share-all")
    def share_all(request: Request, t: str = Form("")):
        guard(request, t)
        res = client.share_all()
        for err in res.get("errors", []):
            client.log(f"[panel] share all: {err}")
        return back()

    @app.post("/skip-all")
    def skip_all(request: Request, t: str = Form("")):
        guard(request, t)
        client.skip_all()
        return back()

    @app.post("/check-now")
    def check_now(request: Request, t: str = Form("")):
        guard(request, t)
        client.request_poll()
        return back()

    @app.post("/relogin")
    def relogin(request: Request, t: str = Form("")):
        guard(request, t)
        client.relogin(open_browser=True)
        return back()

    return app


def port_in_use(host: str, port: int) -> bool:
    """Can we still bind that port? A taken panel port usually means a panel is already up."""
    try:
        family, socktype, proto, _, addr = socket.getaddrinfo(
            host.strip("[]"), port, type=socket.SOCK_STREAM)[0]
    except (socket.gaierror, IndexError):
        return True
    try:
        with socket.socket(family, socktype, proto) as s:
            s.bind(addr)
        return False
    except OSError:
        return True


def serve_in_thread(client, host: str = "127.0.0.1", port: int | None = None) -> threading.Thread | None:
    """Serve the panel on loopback in a daemon thread. None (never an exception) if it cannot."""
    import uvicorn

    port = int(port if port is not None else client.cc.panel_port)
    if host.lower() not in LOOPBACK:
        client.log(f"[panel] refusing to bind {host}: the control panel is localhost only")
        return None
    if port_in_use(host, port):
        client.log(f"[panel] port {port} is already in use; not starting the control panel")
        return None
    server = uvicorn.Server(uvicorn.Config(create_panel(client), host=host, port=port, log_level="warning"))

    def run() -> None:
        try:
            server.run()
        except BaseException as e:  # a lost race for the port must not kill the watcher
            client.log(f"[panel] stopped: {e}")

    thread = threading.Thread(target=run, name="granola-panel", daemon=True)
    thread.start()
    return thread
