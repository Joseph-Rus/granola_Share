"""The pool web UI + ingest API.

Friends browse classes, read notes and download markdown; their laptops push finished notes to
`/api/ingest` (after an optional `/api/preview` to show the guessed class in the control panel);
`/status` answers "connected? as whom? what did it do?" for the pool itself.
"""

from __future__ import annotations

import hashlib
import hmac
import io
import json
import secrets
import threading
import time
import zipfile
from collections import OrderedDict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable
from urllib.parse import quote

import httpx
from fastapi import Body, Depends, FastAPI, Form, HTTPException, Request
from fastapi.responses import FileResponse, HTMLResponse, RedirectResponse, StreamingResponse

from . import __version__, ui
from .classify import classify
from .config import UNSORTED, Config
from .granola import Meeting, meeting_from_dict
from .store import Classification, Store
from .ui import alert, dot, esc, fmt_date, pill, plural, rel_time, stat

NO_NOTES_WARNING = "Connected, but this account has 0 notes."
PREVIEW_CACHE_SIZE = 500
OLLAMA_TTL_SECONDS = 60.0
OLLAMA_TIMEOUT_SECONDS = 3.0
RECENT_LIMIT = 10

NAV = [("Classes", "/"), ("Unsorted", "/unsorted"), ("All notes", "/all"), ("Status", "/status")]


# --- pure helpers (unit tested) -------------------------------------------

def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


def preview_key(m: Meeting) -> tuple[str, str]:
    """Cache key for a note's classification: its id plus a digest of what the sorter looks at."""
    digest = hashlib.sha1("|".join((m.title, m.folder, m.notes_markdown)).encode("utf-8")).hexdigest()
    return (m.id, digest)


class BoundedCache:
    """A tiny thread-safe LRU: only the `max_entries` most recently used keys survive."""

    def __init__(self, max_entries: int = PREVIEW_CACHE_SIZE):
        self.max_entries = max(1, int(max_entries))
        self._data: OrderedDict = OrderedDict()
        self._lock = threading.Lock()

    def get(self, key):
        with self._lock:
            if key not in self._data:
                return None
            self._data.move_to_end(key)
            return self._data[key]

    def put(self, key, value) -> None:
        with self._lock:
            self._data[key] = value
            self._data.move_to_end(key)
            while len(self._data) > self.max_entries:
                self._data.popitem(last=False)

    def __len__(self) -> int:
        return len(self._data)


class Cached:
    """Memoize a zero-argument callable for `ttl` seconds. A result that reports failure is cached
    too, so an unreachable service costs at most one timeout per `ttl`."""

    def __init__(self, fn: Callable[[], Any], ttl: float, clock: Callable[[], float] = time.monotonic):
        self.fn, self.ttl, self.clock = fn, ttl, clock
        self._value: Any = None
        self._at: float | None = None
        self._lock = threading.Lock()

    def __call__(self):
        with self._lock:
            if self._at is None or self.clock() - self._at >= self.ttl:
                self._value = self.fn()
                self._at = self.clock()
            return self._value


def model_installed(model: str, names: list[str]) -> bool:
    """Ollama lists tags like 'qwen3.6:35b-a3b'; a bare name means ':latest'."""
    want = model if ":" in model else f"{model}:latest"
    return any(n in (model, want) for n in names)


def check_ollama(host: str, model: str, get: Callable = httpx.get,
                 timeout: float = OLLAMA_TIMEOUT_SECONDS) -> dict:
    """Is Ollama answering, and is the configured model pulled? Never raises."""
    url = f"{host.rstrip('/')}/api/tags"
    try:
        r = get(url, timeout=timeout)
        r.raise_for_status()
        data = r.json()
    except Exception as e:  # connection refused, timeout, bad JSON: all mean "not reachable"
        return {"enabled": True, "ok": False, "host": host, "model": model, "installed": False,
                "models": [], "error": f"{type(e).__name__}: {e}"[:300]}
    names = [str(m.get("name") or m.get("model") or "") for m in (data.get("models") or []) if isinstance(m, dict)]
    return {"enabled": True, "ok": True, "host": host, "model": model,
            "installed": model_installed(model, names), "models": names, "error": None}


def ollama_status(cfg: Config, get: Callable = httpx.get) -> dict:
    if not cfg.ollama_enabled:
        return {"enabled": False, "ok": None, "host": cfg.ollama_host, "model": cfg.ollama_model,
                "installed": None, "models": [], "error": None}
    return check_ollama(cfg.ollama_host, cfg.ollama_model, get=get)


def _int_or_none(value: str | None) -> int | None:
    try:
        return int(value) if value not in (None, "") else None
    except (TypeError, ValueError):
        return None


def sync_summary(cfg: Config, store: Store) -> dict:
    """What the server's own Granola sync last saw, from sync_state."""
    account: dict | None = None
    raw = store.get_state("sync_account")
    if raw:
        try:
            parsed = json.loads(raw)
            account = parsed if isinstance(parsed, dict) and parsed else None
        except ValueError:
            account = None
    return {
        "enabled": bool(cfg.server_sync),
        "account": account,
        "listed": _int_or_none(store.get_state("sync_listed")),
        "checked_at": store.get_state("sync_checked_at") or None,
        "error": store.get_state("sync_error") or None,
        "last_sync": store.get_state("last_sync") or None,
    }


def config_summary(cfg: Config) -> dict:
    """Everything a friend may see about the setup. Never the password."""
    return {
        "pool_name": cfg.pool_name,
        "pool_dir": str(cfg.pool_dir),
        "web_host": cfg.web_host,
        "web_port": cfg.web_port,
        "password_set": bool(cfg.pool_password),
        "server_sync": cfg.server_sync,
        "poll_interval_seconds": cfg.poll_interval_seconds,
        "include_transcripts": cfg.include_transcripts,
        "ollama_enabled": cfg.ollama_enabled,
        "ollama_host": cfg.ollama_host,
        "ollama_model": cfg.ollama_model,
        "min_confidence": cfg.min_confidence,
        "classes": cfg.class_names(),
    }


def class_rows(cfg: Config, store: Store) -> list[dict]:
    """Configured classes in config order (even when empty), then any extra class found in the
    pool; Unsorted is reported separately."""
    counts = store.class_counts()
    names = list(cfg.class_names()) + sorted(n for n in counts if n and n not in cfg.class_names() and n != UNSORTED)
    return [{"name": n, "count": counts.get(n, 0), "latest": store.latest_date(n) if counts.get(n) else None}
            for n in names]


def status_data(cfg: Config, store: Store, ollama: dict, now: datetime | None = None) -> dict:
    now = now or _utcnow()
    classes = class_rows(cfg, store)
    owners = store.owners_summary()
    return {
        "ok": True,
        "version": __version__,
        "server_time": now.isoformat(timespec="seconds"),
        "pool_name": cfg.pool_name,
        "notes": store.count(),
        "classes": classes,
        "unsorted": store.class_counts().get(UNSORTED, 0),
        "contributors": [{"owner": o, "count": n, "latest": d} for o, n, d in owners],
        "last_ingest": store.get_state("last_ingest") or None,
        "last_ingest_owner": store.get_state("last_ingest_owner") or None,
        "ollama": ollama,
        "sync": sync_summary(cfg, store),
        "config": config_summary(cfg),
    }


def known_classes(cfg: Config, store: Store) -> set[str]:
    return set(cfg.class_names()) | {UNSORTED} | {n for n in store.class_counts() if n}


# --- rendering ---------------------------------------------------------------

def class_href(name: str) -> str:
    return "/class/" + quote(name, safe="")


def class_pill(name: str | None) -> str:
    name = name or UNSORTED
    return pill(name, "warn" if name == UNSORTED else "")


def confidence_pill(by: str | None, confidence: float | None, class_name: str | None) -> str:
    tone = "warn" if (class_name or UNSORTED) == UNSORTED else "ok"
    return pill(f"{by or 'none'} · {(confidence or 0):.2f}", tone, title="how this note was sorted")


def note_item(r) -> str:
    nid = quote(str(r["id"]), safe="")
    title = r["lecture_title"] or r["title"] or "Untitled"
    topics = ", ".join(json.loads(r["topics"] or "[]"))
    meta = [esc(fmt_date(r["date"])), class_pill(r["class_name"])]
    if r["owner"]:
        meta.append(f"from {esc(r['owner'])}")
    meta.append(confidence_pill(r["classified_by"], r["confidence"], r["class_name"]))
    topics_html = f'<div class="muted small">{esc(topics)}</div>' if topics else ""
    return (
        f'<div class=item><div class=body><div class=title><a href="/note/{nid}">{esc(title)}</a></div>'
        f'<div class=meta>{"".join(f"<span>{x}</span>" for x in meta)}</div>{topics_html}</div>'
        f'<div class=actions><a class="btn sm" href="/note/{nid}/download">md</a></div></div>'
    )


def note_list(rows, empty: str = "No notes.") -> str:
    items = "".join(note_item(r) for r in rows)
    return f'<div class=list>{items or f"<div class=empty>{esc(empty)}</div>"}</div>'


def class_card(row: dict) -> str:
    href = class_href(row["name"])
    last = f"last {fmt_date(row['latest'])}" if row["latest"] else "no notes yet"
    return (
        f'<div class="card class"><div class=name><a href="{href}">{esc(row["name"])}</a></div>'
        f'<div class=count>{row["count"]}</div>'
        f'<div class=foot><span>{esc(last)}</span><a href="{href}/zip">zip</a></div></div>'
    )


def pool_tone(data: dict) -> str:
    sync = data["sync"]
    if sync["enabled"] and sync["error"]:
        return "bad"
    if sync["enabled"] and sync["listed"] == 0:
        return "warn"
    if data["ollama"]["enabled"] and data["ollama"]["ok"] is False:
        return "warn"
    return "ok"


def status_strip(data: dict) -> str:
    last = data["last_ingest"]
    meta = [f"v{data['version']}", plural(data["notes"], "note"),
            "last ingest " + rel_time(last) + (f" from {data['last_ingest_owner']}" if last and data["last_ingest_owner"] else "")]
    sync = data["sync"]
    if sync["enabled"]:
        acct = sync["account"] or {}
        meta.append("server sync as " + (acct.get("email") or "not signed in"))
    who = (f'<div class=who><div class=id>{dot(pool_tone(data))}{esc(data["pool_name"])}</div>'
           f'<div class=meta>{" · ".join(esc(x) for x in meta)}</div></div>')
    unsorted_tone = "warn" if data["unsorted"] else ""
    stats = (f'<div class=stats>{stat(data["notes"], "Notes")}{stat(len(data["classes"]), "Classes")}'
             f'{stat(len(data["contributors"]), "Contributors")}{stat(data["unsorted"], "Unsorted", unsorted_tone)}</div>')
    return f"<div class=status>{who}{stats}</div>"


def status_alerts(data: dict) -> str:
    out = []
    sync = data["sync"]
    if sync["enabled"]:
        acct = sync["account"] or {}
        if sync["error"]:
            out.append(alert(f"Server sync failed: <span class=mono>{esc(sync['error'])}</span> "
                             f"<span class=muted>({rel_time(sync['checked_at'])})</span>", "bad"))
        elif sync["checked_at"] is None:
            out.append(alert("Server sync is on but has not checked in yet.", "info", "i"))
        elif not acct.get("email"):
            out.append(alert("Server sync is on but this server is not signed in to Granola. "
                             "Run <span class=mono>granola-share login</span> on the server.", "bad"))
        elif sync["listed"] == 0:
            out.append(alert(f"<b>{esc(NO_NOTES_WARNING)}</b> Signed in as {esc(acct['email'])}. If your lectures live "
                             "in a different Google account, run <span class=mono>granola-share login</span> "
                             "on the server and switch.", "warn"))
    oll = data["ollama"]
    if oll["enabled"] and oll["ok"] is False:
        out.append(alert(f"Ollama is not answering at {esc(oll['host'])}; new notes without a folder or title "
                         "match will land in Unsorted.", "warn"))
    elif oll["enabled"] and oll["ok"] and not oll["installed"]:
        out.append(alert(f"Ollama is up but the model <span class=mono>{esc(oll['model'])}</span> is not pulled; "
                         f"run <span class=mono>ollama pull {esc(oll['model'])}</span>.", "warn"))
    return "".join(out)


def kv(rows: list[tuple[str, str]]) -> str:
    """`rows` values are trusted HTML."""
    return "<dl class=kv>" + "".join(f"<dt>{esc(k)}</dt><dd>{v}</dd>" for k, v in rows) + "</dl>"


def status_page_body(data: dict) -> str:
    sync, oll, cfg = data["sync"], data["ollama"], data["config"]
    acct = sync["account"] or {}
    parts = [status_strip(data), status_alerts(data)]

    if sync["enabled"]:
        sync_tone = "bad" if sync["error"] else ("warn" if sync["listed"] == 0 else ("ok" if acct.get("email") else ""))
        sync_rows = [
            ("Signed in as", f"{dot(sync_tone)}{esc(acct.get('email') or 'not signed in')}"),
            ("Workspace", esc(acct.get("workspace") or "—")),
            ("Notes listed", esc(str(sync["listed"]) if sync["listed"] is not None else "—")),
            ("Last checked", esc(rel_time(sync["checked_at"]))),
            ("Last full sync", esc(rel_time(sync["last_sync"]))),
        ]
        if sync["error"]:
            sync_rows.append(("Error", f"<span class=mono>{esc(sync['error'])}</span>"))
    else:
        sync_rows = [("Server sync", "off — notes arrive only from friends' laptops")]
    parts.append("<h2>Server sync</h2><div class=card>" + kv(sync_rows) + "</div>")

    if oll["enabled"]:
        oll_tone = "ok" if oll["ok"] and oll["installed"] else ("warn" if oll["ok"] else "bad")
        state = "reachable" if oll["ok"] else "not reachable"
        oll_rows = [("Ollama", f"{dot(oll_tone)}{esc(state)} at <span class=mono>{esc(oll['host'])}</span>"),
                    ("Model", f"<span class=mono>{esc(oll['model'])}</span> · "
                              + ("installed" if oll["installed"] else "not installed" if oll["ok"] else "unknown"))]
        if oll["error"]:
            oll_rows.append(("Error", f"<span class=mono>{esc(oll['error'])}</span>"))
    else:
        oll_rows = [("Ollama", "off — only folder and title rules sort notes")]
    parts.append("<h2>Sorting</h2><div class=card>" + kv(oll_rows) + "</div>")

    contrib = "".join(
        f"<tr><td>{esc(c['owner'] or '?')}</td><td>{c['count']}</td><td>{esc(fmt_date(c['latest'], with_year=True))}</td></tr>"
        for c in data["contributors"]
    ) or "<tr><td colspan=3 class=muted>Nobody has shared a note yet.</td></tr>"
    parts.append(f"<h2>Contributors</h2><table><tr><th>Who</th><th>Notes</th><th>Latest</th></tr>{contrib}</table>")

    classes = "".join(
        f'<tr><td><a href="{class_href(c["name"])}">{esc(c["name"])}</a></td><td>{c["count"]}</td>'
        f"<td>{esc(fmt_date(c['latest'], with_year=True))}</td></tr>"
        for c in data["classes"]
    ) or "<tr><td colspan=3 class=muted>No classes configured.</td></tr>"
    parts.append(f"<h2>Classes</h2><table><tr><th>Class</th><th>Notes</th><th>Latest</th></tr>{classes}</table>")

    cfg_rows = [
        ("Pool folder", f"<span class=mono>{esc(cfg['pool_dir'])}</span>"),
        ("Listening on", f"<span class=mono>{esc(cfg['web_host'])}:{cfg['web_port']}</span>"),
        ("Password", "set" if cfg["password_set"] else "none (open pool)"),
        ("Model", f"<span class=mono>{esc(cfg['ollama_model'])}</span>" if cfg["ollama_enabled"] else "off"),
        ("Min confidence", esc(f"{cfg['min_confidence']:.2f}")),
        ("Transcripts", "included" if cfg["include_transcripts"] else "not included"),
        ("Version", esc(data["version"])),
    ]
    parts.append("<h2>Config</h2><div class=card>" + kv(cfg_rows) + "</div>")
    return "".join(parts)


def index_body(data: dict, recent_rows) -> str:
    parts = [status_strip(data), status_alerts(data)]
    if data["unsorted"]:
        parts.append(alert(f'<a href="/unsorted">{esc(plural(data["unsorted"], "note"))} waiting in Unsorted</a> — '
                           "open one and file it.", "warn"))
    cards = "".join(class_card(c) for c in data["classes"])
    parts.append("<h2>Classes</h2>" + (f"<div class=grid>{cards}</div>" if cards
                                       else "<div class=empty>No classes configured yet.</div>"))
    parts.append("<h2>Recent</h2>" + note_list(recent_rows, "Nothing synced yet."))
    return "".join(parts)


# --- the app -----------------------------------------------------------------

def create_app(cfg: Config, store: Store, *, chat: Callable | None = None,
               ollama_check: Callable[[], dict] | None = None,
               now: Callable[[], datetime] = _utcnow) -> FastAPI:
    """`chat` and `ollama_check` are injection points for tests (fake Ollama)."""
    app = FastAPI(title="granola-share")
    secret = secrets.token_bytes(32)
    previews = BoundedCache(PREVIEW_CACHE_SIZE)
    ollama = Cached(ollama_check or (lambda: ollama_status(cfg)), OLLAMA_TTL_SECONDS)
    allowed_classes = lambda: cfg.class_names() + [UNSORTED]  # noqa: E731 (config may be edited in place)

    def page(title: str, body: str, active: str = "", heading: str | None = None) -> HTMLResponse:
        nav = list(NAV) + ([("Log out", "/logout")] if cfg.pool_password else [])
        h1 = f"<h1>{esc(heading if heading is not None else title)}</h1>" if (heading or heading is None) else ""
        return HTMLResponse(ui.layout(title, h1 + body, brand=cfg.pool_name or "granola-share",
                                      nav=nav, active=active))

    def cookie_value() -> str:
        return hmac.new(secret, b"pool-ok", hashlib.sha256).hexdigest()

    def logged_in(request: Request) -> bool:
        return not cfg.pool_password or request.cookies.get("pool") == cookie_value()

    def require_login(request: Request):
        if not logged_in(request):
            raise HTTPException(status_code=307, headers={"Location": "/login"})

    auth = Depends(require_login)

    def require_key(request: Request):
        """API auth for friend clients: Authorization: Bearer <pool password>."""
        if not cfg.pool_password:
            return
        h = request.headers.get("authorization", "")
        key = h[7:].strip() if h.lower().startswith("bearer ") else request.headers.get("x-pool-key", "")
        if not hmac.compare_digest(key, cfg.pool_password):
            raise HTTPException(401, "bad pool password")

    def require_login_or_key(request: Request):
        if not logged_in(request):
            require_key(request)

    def status() -> dict:
        return status_data(cfg, store, ollama(), now())

    def parse_note(payload: Any) -> Meeting:
        if not isinstance(payload, dict):
            raise HTTPException(400, "payload must be a JSON object")
        try:
            return meeting_from_dict(payload)
        except ValueError as e:
            raise HTTPException(400, str(e))

    def classification_of(m: Meeting) -> Classification:
        """Classify once per (id, content); a preview's answer is reused by the ingest that follows."""
        key = preview_key(m)
        c = previews.get(key)
        if c is None:
            c = classify(m, cfg, chat)
            previews.put(key, c)
        return c

    def as_json(m: Meeting, c: Classification) -> dict:
        return {"id": m.id, "class_name": c.class_name, "confidence": c.confidence,
                "classified_by": c.by, "lecture_title": c.lecture_title or m.title, "topics": list(c.topics or [])}

    @app.get("/api/health")
    def health(request: Request):
        require_key(request)
        return {"ok": True, "pool_name": cfg.pool_name, "classes": cfg.class_names(), "notes": store.count(),
                "version": __version__, "server_time": now().isoformat(timespec="seconds"),
                "last_ingest": store.get_state("last_ingest") or None}

    @app.post("/api/preview")
    def preview(request: Request, payload: Any = Body(default=None)):
        """Guess the class of a note without saving it (the control panel shows this guess)."""
        require_key(request)
        m = parse_note(payload)
        return as_json(m, classification_of(m))

    @app.post("/api/ingest")
    def ingest(request: Request, payload: Any = Body(default=None)):
        """A friend's client pushes one finished note. We classify (or honour their pick) and file it."""
        require_key(request)
        override = str(payload.get("class_name") or "").strip() if isinstance(payload, dict) else ""
        m = parse_note(payload)
        if override:
            if override not in allowed_classes():
                raise HTTPException(400, f"unknown class: {override}")
            guess = previews.get(preview_key(m))
            c = Classification(override, 1.0, "human", lecture_title=(guess.lecture_title if guess else "") or m.title,
                               topics=list(guess.topics or []) if guess else [])
        else:
            c = classification_of(m)
        path = store.save(m, c)
        store.set_state("last_ingest", now().isoformat(timespec="seconds"))
        store.set_state("last_ingest_owner", m.owner)
        return as_json(m, c) | {"file": path.name, "url": f"/note/{quote(m.id, safe='')}"}

    @app.get("/api/status")
    def api_status(request: Request):
        require_login_or_key(request)
        return status()

    @app.get("/login", response_class=HTMLResponse)
    def login_form():
        return page("Log in", '<form method="post" class=row><input type="password" name="password" '
                    'placeholder="pool password" autofocus> <button class="btn primary">Enter</button></form>')

    @app.post("/login")
    def login(password: str = Form(...)):
        if not hmac.compare_digest(password, cfg.pool_password):
            return page("Log in", alert("Wrong password. <a href='/login'>Try again</a>", "bad"))
        resp = RedirectResponse("/", status_code=303)
        resp.set_cookie("pool", cookie_value(), httponly=True, samesite="lax", max_age=60 * 60 * 24 * 90)
        return resp

    @app.get("/logout")
    def logout():
        resp = RedirectResponse("/login", status_code=303)
        resp.delete_cookie("pool")
        return resp

    @app.get("/", response_class=HTMLResponse, dependencies=[auth])
    def index():
        return page("Classes", index_body(status(), store.recent(RECENT_LIMIT)), active="/", heading="")

    @app.get("/status", response_class=HTMLResponse, dependencies=[auth])
    def status_page():
        return page("Status", status_page_body(status()), active="/status", heading="")

    def require_class(name: str) -> None:
        if name not in known_classes(cfg, store):
            raise HTTPException(404, "unknown class")

    @app.get("/class/{name}", response_class=HTMLResponse, dependencies=[auth])
    def by_class(name: str):
        require_class(name)
        rows = store.list_notes(name)
        head = (f'<div class="row spread"><span class=muted>{esc(plural(len(rows), "note"))}</span>'
                f'<a class="btn sm" href="{class_href(name)}/zip">Download zip</a></div>')
        return page(name, head + note_list(rows), active="/" if name != UNSORTED else "/unsorted")

    @app.get("/unsorted", response_class=HTMLResponse, dependencies=[auth])
    def unsorted():
        body = "<p class=muted>Notes the sorter was not sure about. Open one and file it.</p>" + note_list(store.list_notes(UNSORTED))
        return page(UNSORTED, body, active="/unsorted")

    @app.get("/all", response_class=HTMLResponse, dependencies=[auth])
    def all_notes():
        rows = store.list_notes()
        return page("All notes", f'<p class=muted>{esc(plural(len(rows), "note"))}</p>' + note_list(rows), active="/all")

    @app.get("/note/{note_id}", response_class=HTMLResponse, dependencies=[auth])
    def note(note_id: str):
        r = store.get(note_id)
        if not r:
            raise HTTPException(404)
        text = Path(r["md_path"]).read_text() if r["md_path"] and Path(r["md_path"]).exists() else ""
        body_md = text.split("---", 2)[2] if text.startswith("---") and text.count("---") >= 2 else text
        rendered = ui.render_note_markdown(body_md)
        nid = quote(note_id, safe="")
        options = "".join(
            f'<option value="{esc(c, quote=True)}"{" selected" if c == r["class_name"] else ""}>{esc(c)}</option>'
            for c in allowed_classes()
        )
        topics = ", ".join(json.loads(r["topics"] or "[]"))
        meta = [esc(fmt_date(r["date"], with_year=True)), f"from {esc(r['owner'] or '?')}", class_pill(r["class_name"]),
                confidence_pill(r["classified_by"], r["confidence"], r["class_name"]),
                f'<a href="/note/{nid}/download">download .md</a>']
        if topics:
            meta.append(esc(topics))
        head = (
            f'<div class="row muted small">{"".join(f"<span>{x}</span>" for x in meta)}</div>'
            f'<form class="row" method="post" action="/note/{nid}/class" style="margin-top:.6rem">'
            f'<label class=muted for=class_name>Class</label><select class=sm id=class_name name="class_name">{options}</select> '
            f'<button class="btn sm">Move</button></form>'
        )
        return page(r["lecture_title"] or r["title"] or "Note", head + f'<div class=note>{rendered}</div>')

    @app.post("/note/{note_id}/class", dependencies=[auth])
    def move(note_id: str, class_name: str = Form(...)):
        if class_name not in allowed_classes():
            raise HTTPException(400, "unknown class")
        if store.set_class(note_id, class_name) is None:
            raise HTTPException(404)
        return RedirectResponse(f"/note/{quote(note_id, safe='')}", status_code=303)

    @app.get("/note/{note_id}/download", dependencies=[auth])
    def download(note_id: str):
        r = store.get(note_id)
        if not r or not r["md_path"] or not Path(r["md_path"]).exists():
            raise HTTPException(404)
        return FileResponse(r["md_path"], media_type="text/markdown", filename=Path(r["md_path"]).name)

    @app.get("/class/{name}/zip", dependencies=[auth])
    def class_zip(name: str):
        require_class(name)
        rows = store.list_notes(name)
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
            for r in rows:
                p = Path(r["md_path"]) if r["md_path"] else None
                if p and p.exists():
                    z.write(p, arcname=f"{name}/{p.name}")
        buf.seek(0)
        return StreamingResponse(buf, media_type="application/zip",
                                 headers={"Content-Disposition": f'attachment; filename="{name}.zip"'})

    @app.get("/api/notes", dependencies=[auth])
    def api_notes(class_name: str | None = None):
        return [
            {k: r[k] for k in ("id", "title", "lecture_title", "date", "owner", "class_name", "confidence", "classified_by", "has_transcript")}
            | {"topics": json.loads(r["topics"] or "[]")}
            for r in store.list_notes(class_name)
        ]

    return app
