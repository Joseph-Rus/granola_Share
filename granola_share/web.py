"""Tiny web UI for friends: browse classes, read notes, download markdown."""

from __future__ import annotations

import hashlib
import hmac
import html
import io
import json
import secrets
import zipfile
from pathlib import Path

import markdown as md
from fastapi import Depends, FastAPI, Form, HTTPException, Request
from fastapi.responses import FileResponse, HTMLResponse, RedirectResponse, StreamingResponse

from .classify import classify
from .config import UNSORTED, Config
from .granola import meeting_from_dict
from .store import Store

CSS = """
body{font-family:-apple-system,system-ui,sans-serif;max-width:900px;margin:2rem auto;padding:0 1rem;line-height:1.5}
a{color:#0b57d0}nav a{margin-right:1rem}.muted{color:#666;font-size:.9em}
table{border-collapse:collapse;width:100%}td,th{padding:.4rem .5rem;border-bottom:1px solid #ddd;text-align:left}
.note{border:1px solid #ddd;border-radius:8px;padding:1rem 1.5rem;margin-top:1rem}
form.inline{display:inline}input,select,button{font:inherit;padding:.3rem .5rem}
.pill{background:#eef;border-radius:999px;padding:.1rem .6rem;font-size:.85em}
"""


def page(title: str, body: str, cfg: Config) -> HTMLResponse:
    nav = '<nav><a href="/">Classes</a><a href="/unsorted">Unsorted</a><a href="/all">All notes</a>'
    if cfg.pool_password:
        nav += '<a href="/logout">Log out</a>'
    nav += "</nav>"
    return HTMLResponse(
        f"<!doctype html><title>{html.escape(title)} · granola-share</title><style>{CSS}</style>"
        f"{nav}<h1>{html.escape(title)}</h1>{body}"
    )


def create_app(cfg: Config, store: Store) -> FastAPI:
    app = FastAPI(title="granola-share")
    secret = secrets.token_bytes(32)

    def cookie_value() -> str:
        return hmac.new(secret, b"pool-ok", hashlib.sha256).hexdigest()

    def require_login(request: Request):
        if not cfg.pool_password:
            return
        if request.cookies.get("pool") != cookie_value():
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

    @app.get("/api/health")
    def health(request: Request):
        require_key(request)
        return {"ok": True, "pool_name": cfg.pool_name, "classes": cfg.class_names(),
                "notes": sum(n for _, n in store.classes_summary())}

    @app.post("/api/ingest")
    def ingest(request: Request, payload: dict):
        """A friend's client pushes one finished note. We classify and file it."""
        require_key(request)
        try:
            m = meeting_from_dict(payload)
        except ValueError as e:
            raise HTTPException(400, str(e))
        c = classify(m, cfg)
        path = store.save(m, c)
        return {"id": m.id, "class_name": c.class_name, "confidence": c.confidence,
                "classified_by": c.by, "lecture_title": c.lecture_title, "file": path.name}

    @app.get("/login", response_class=HTMLResponse)
    def login_form():
        return page("Log in", '<form method="post"><input type="password" name="password" placeholder="pool password" autofocus> <button>Enter</button></form>', cfg)

    @app.post("/login")
    def login(password: str = Form(...)):
        if not hmac.compare_digest(password, cfg.pool_password):
            return page("Log in", "<p>Wrong password.</p><p><a href='/login'>Try again</a></p>", cfg)
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
        rows = store.classes_summary()
        items = "".join(
            f'<tr><td><a href="/class/{html.escape(name, quote=True)}">{html.escape(name)}</a></td>'
            f'<td>{n}</td><td><a href="/class/{html.escape(name, quote=True)}/zip">zip</a></td></tr>'
            for name, n in rows
        ) or "<tr><td colspan=3 class=muted>Nothing synced yet.</td></tr>"
        return page(cfg.pool_name, f"<table><tr><th>Class</th><th>Notes</th><th>Download</th></tr>{items}</table>", cfg)

    def note_rows(rows) -> str:
        out = []
        for r in rows:
            topics = ", ".join(json.loads(r["topics"] or "[]"))
            out.append(
                f'<tr><td>{html.escape(r["date"][:10] if r["date"] else "")}</td>'
                f'<td><a href="/note/{r["id"]}">{html.escape(r["lecture_title"] or r["title"] or "")}</a>'
                f'<div class=muted>{html.escape(topics)}</div></td>'
                f'<td><span class=pill>{html.escape(r["class_name"] or "")}</span></td>'
                f'<td class=muted>{html.escape(r["owner"] or "")}</td>'
                f'<td><a href="/note/{r["id"]}/download">md</a></td></tr>'
            )
        return "".join(out) or "<tr><td colspan=5 class=muted>No notes.</td></tr>"

    def notes_table(rows) -> str:
        return f"<table><tr><th>Date</th><th>Lecture</th><th>Class</th><th>From</th><th></th></tr>{note_rows(rows)}</table>"

    @app.get("/class/{name}", response_class=HTMLResponse, dependencies=[auth])
    def by_class(name: str):
        return page(name, notes_table(store.list_notes(name)), cfg)

    @app.get("/unsorted", response_class=HTMLResponse, dependencies=[auth])
    def unsorted():
        return page(UNSORTED, "<p class=muted>Notes the sorter was not sure about. Open one and file it.</p>" + notes_table(store.list_notes(UNSORTED)), cfg)

    @app.get("/all", response_class=HTMLResponse, dependencies=[auth])
    def all_notes():
        return page("All notes", notes_table(store.list_notes()), cfg)

    @app.get("/note/{note_id}", response_class=HTMLResponse, dependencies=[auth])
    def note(note_id: str):
        r = store.get(note_id)
        if not r:
            raise HTTPException(404)
        text = Path(r["md_path"]).read_text() if r["md_path"] and Path(r["md_path"]).exists() else ""
        body_md = text.split("---", 2)[2] if text.startswith("---") and text.count("---") >= 2 else text
        rendered = md.markdown(body_md, extensions=["extra", "sane_lists"])
        options = "".join(
            f'<option value="{html.escape(c, quote=True)}"{" selected" if c == r["class_name"] else ""}>{html.escape(c)}</option>'
            for c in cfg.class_names() + [UNSORTED]
        )
        meta = (
            f'<p class=muted>{html.escape(r["date"] or "")} · from {html.escape(r["owner"] or "?")} · '
            f'sorted by {html.escape(r["classified_by"] or "")} ({(r["confidence"] or 0):.2f}) · '
            f'<a href="/note/{note_id}/download">download .md</a></p>'
            f'<form class=inline method="post" action="/note/{note_id}/class">Class: <select name="class_name">{options}</select> '
            f'<button>Move</button></form>'
        )
        return page(r["lecture_title"] or r["title"] or "Note", meta + f'<div class=note>{rendered}</div>', cfg)

    @app.post("/note/{note_id}/class", dependencies=[auth])
    def move(note_id: str, class_name: str = Form(...)):
        if class_name not in cfg.class_names() + [UNSORTED]:
            raise HTTPException(400, "unknown class")
        store.set_class(note_id, class_name)
        return RedirectResponse(f"/note/{note_id}", status_code=303)

    @app.get("/note/{note_id}/download", dependencies=[auth])
    def download(note_id: str):
        r = store.get(note_id)
        if not r or not r["md_path"] or not Path(r["md_path"]).exists():
            raise HTTPException(404)
        return FileResponse(r["md_path"], media_type="text/markdown", filename=Path(r["md_path"]).name)

    @app.get("/class/{name}/zip", dependencies=[auth])
    def class_zip(name: str):
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
