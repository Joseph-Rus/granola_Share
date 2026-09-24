"""The library's web server: the UI you browse, and the API your laptop pushes lectures to."""

from __future__ import annotations

import hashlib
import hmac
import io
import json
import os
import secrets
import threading
import zipfile
from pathlib import Path
from urllib.parse import quote

from fastapi import Depends, FastAPI, HTTPException, Request
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse, RedirectResponse, StreamingResponse

from . import __version__, hostinfo, ollama, update
from . import ui
from .classify import classify_by_rules
from .config import UNSORTED, ClassDef, Config, save_config
from .granola import meeting_from_dict
from .pipeline import Pipeline
from .store import FAILED, WORKING, Store
from .ui import esc

SORTED_BY = {"folder": "its Granola folder", "rules": "its title", "ollama": "AI", "human": "a person", "none": "nobody yet"}
CSP = ("default-src 'self'; script-src 'nonce-{nonce}' https://cdn.jsdelivr.net; "
       "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net; "
       "font-src https://fonts.gstatic.com https://cdn.jsdelivr.net; img-src 'self' data:; "
       "form-action 'self'; frame-ancestors 'none'; base-uri 'none'")
PROXY_HEADERS = ("x-forwarded-for", "forwarded", "tailscale-user-login")


def safe_next(nxt: str) -> str:
    """Only same-site paths. Browsers read '\\' as '/' and drop tabs and newlines, so '/\\evil.com'
    or '/\t/evil.com' would otherwise leave the site."""
    if not nxt.startswith("/") or nxt.startswith("//") or any(ch == "\\" or ord(ch) <= 32 for ch in nxt):
        return "/"
    return nxt


def _web_secret(cfg: Config) -> bytes:
    """Persisted so logins survive restarts and updates."""
    path = cfg.home / "web_secret"
    try:
        if path.exists():
            return bytes.fromhex(path.read_text().strip())
        cfg.home.mkdir(parents=True, exist_ok=True)
        key = secrets.token_bytes(32)
        path.write_text(key.hex())
        os.chmod(path, 0o600)
        return key
    except (OSError, ValueError):
        return secrets.token_bytes(32)


def create_app(cfg: Config, store: Store, pipeline: Pipeline | None = None, *, list_models=None,
               tailscale=None, latest=None) -> FastAPI:
    app = FastAPI(title="granola-share", docs_url=None, redoc_url=None)
    pipeline = pipeline or Pipeline(cfg, store)
    list_models = list_models or ollama.list_models
    tailscale = tailscale or hostinfo.tailscale_info
    latest = latest or update.cached_latest
    secret = _web_secret(cfg)

    @app.middleware("http")
    async def tag_responses(request: Request, call_next):
        resp = await call_next(request)
        resp.headers["X-Granola-Share"] = __version__  # lets setup tell our server from another app on the port
        return resp

    # -- who is asking ----------------------------------------------------------------
    def sign(what: str) -> str:
        return hmac.new(secret, what.encode(), hashlib.sha256).hexdigest()

    def is_local(request: Request) -> bool:
        host = request.client.host if request.client else ""
        return host in ("127.0.0.1", "::1") and not any(h in request.headers for h in PROXY_HEADERS)

    def role_of(request: Request) -> str | None:
        """'admin' (everything) for you: on this computer, with no password set, or logged in."""
        cookie = request.cookies.get("pool", "")
        if is_local(request) or not cfg.pool_password:
            return "admin"
        if cookie and any(hmac.compare_digest(cookie, sign(w)) for w in ("admin", "member")):  # 0.2 cookies too
            return "admin"
        return None

    def member(request: Request) -> str:
        role = role_of(request)
        if role is None:
            nxt = quote(request.url.path + (f"?{request.url.query}" if request.url.query else ""), safe="")
            raise HTTPException(status_code=303, headers={"Location": f"/login?next={nxt}"})
        return role

    admin = member  # one person, one password

    def require_key(request: Request):
        """API auth for your laptop: Authorization: Bearer <password>."""
        if not cfg.pool_password:
            return
        h = request.headers.get("authorization", "")
        key = h[7:].strip() if h.lower().startswith("bearer ") else request.headers.get("x-pool-key", "")
        if not hmac.compare_digest(key, cfg.pool_password):
            raise HTTPException(401, "wrong password")

    # -- rendering ----------------------------------------------------------------------
    def context(role: str | None, current: str | None = None) -> dict:
        counts = dict(store.classes_summary())
        names = cfg.class_names()
        classes = [(n, counts.get(n, 0)) for n in names]
        classes += [(n, c) for n, c in counts.items() if n not in names and n != UNSORTED]
        classes.append((UNSORTED, counts.get(UNSORTED, 0)))
        return {"pool_name": cfg.pool_name, "classes": classes, "total": sum(counts.values()),
                "processing": len(store.processing()), "admin": role == "admin", "current": current,
                "password": bool(cfg.pool_password), "version": __version__,
                "nonce": secrets.token_urlsafe(16)}

    def respond(body: str, ctx: dict | None = None, nonce: str | None = None, status: int = 200) -> HTMLResponse:
        n = nonce or (ctx or {}).get("nonce", "")
        return HTMLResponse(body, status_code=status, headers={"Content-Security-Policy": CSP.format(nonce=n)})

    def show(title: str, body: str, ctx: dict, math: bool = False) -> HTMLResponse:
        return respond(ui.page(title, body, ctx, math=math), ctx)

    def queue_panel(admin_view: bool) -> str:
        rows = store.processing()
        if not rows:
            return ""
        model = cfg.effective_summary_model
        items = []
        for r in rows:
            if r["status"] == WORKING:
                state = f'<span class="state now">Writing the summary now with {esc(model)}</span>'
            elif r["status"] == FAILED:
                retry = (f'<form class="inline" method="post" action="/note/{quote(r["id"], safe="")}/resummarize">'
                         f'<button>Try again</button></form>') if admin_view else ""
                state = f'<span class="state failed">Failed: {esc((r["error"] or "")[:160])}</span>{retry}'
            else:
                state = '<span class="state">Waiting its turn</span>'
            title = esc(r["lecture_title"] or r["title"] or "Untitled")
            if r["md_path"]:
                title = f'<a href="/note/{quote(r["id"], safe="")}">{title}</a>'
            items.append(f"<li><span>{title}</span>{state}</li>")
        waiting = any(r["status"] != FAILED for r in rows)
        refresh = ' data-refresh="20"' if waiting else ""
        return (f'<section id="queue"{refresh}><h2>Being written</h2>'
                f'<p class="lede small">Summaries are written on this computer with {esc(model)}. '
                f'A lecture takes a minute or two. This page refreshes on its own.</p>'
                f'<div class="queue"><ul>{"".join(items)}</ul></div></section>')

    def not_found(role: str, what: str = "note") -> HTMLResponse:
        ctx = context(role)
        body = (f'<h1>No such {what}</h1><p class="lede">It may have been deleted or moved. '
                f'<a href="/">Back to recent lectures</a></p>')
        return respond(ui.page("Not found", body, ctx), ctx, status=404)

    # -- login ----------------------------------------------------------------------------
    @app.get("/login", response_class=HTMLResponse)
    def login_form(next: str = "/", bad: int = 0):
        nonce = secrets.token_urlsafe(16)
        err = '<p class="bad">That password is not right.</p>' if bad else ""
        body = (f'<div class="login"><form method="post" action="/login"><h1>{esc(cfg.pool_name)}</h1>'
                f'<p class="muted">Enter your password to continue.</p>{err}'
                f'<input type="password" name="password" autocomplete="current-password" aria-label="Password" autofocus required>'
                f'<input type="hidden" name="next" value="{esc(next)}">'
                f'<button class="primary">Open</button></form></div>')
        return respond(ui.bare_page(f"Log in · {cfg.pool_name}", body, nonce), nonce=nonce)

    @app.post("/login")
    async def login(request: Request):
        form = await request.form()
        password, nxt = str(form.get("password", "")), safe_next(str(form.get("next", "/")))
        # The 0.2 admin password still works, so an old bookmark or password manager entry isn't a dead end.
        if any(p and hmac.compare_digest(password, p) for p in (cfg.pool_password, cfg.admin_password)):
            who = "admin"
        else:
            return RedirectResponse(f"/login?bad=1&next={quote(nxt, safe='')}", status_code=303)
        resp = RedirectResponse(nxt, status_code=303)
        resp.set_cookie("pool", sign(who), httponly=True, samesite="lax", max_age=60 * 60 * 24 * 180)
        return resp

    @app.get("/logout")
    def logout():
        resp = RedirectResponse("/login", status_code=303)
        resp.delete_cookie("pool")
        return resp

    # -- browsing -------------------------------------------------------------------------
    @app.get("/", response_class=HTMLResponse)
    def home(role: str = Depends(member)):
        ctx = context(role, "home")
        rows = store.list_notes(limit=40)
        n_classes = len([c for c, n in ctx["classes"] if n and c != UNSORTED])
        lede = (f"{ctx['total']} lecture{'s' if ctx['total'] != 1 else ''} across "
                f"{n_classes} class{'es' if n_classes != 1 else ''}." if ctx["total"] else "")
        empty = ("" if ctx["processing"] else
                 '<div class="empty"><strong>No lectures yet</strong>Lectures you record in Granola show up here '
                 'once your laptop sends them. To connect it, see <a href="/settings">Settings</a>.</div>')
        body = (ui.search_box() + f"<h1>{esc(cfg.pool_name)}</h1>" + (f'<p class="lede">{lede}</p>' if lede else "")
                + queue_panel(role == "admin")
                + (f"<h2>Recent lectures</h2>{ui.note_list(rows, '', '')}" if rows else empty))
        return show(cfg.pool_name, body, ctx)

    def class_page(name: str, role: str, current: str, lede: str) -> HTMLResponse:
        ctx = context(role, current)
        rows = store.list_notes(name)
        zip_btn = (f'<a class="btn" href="{ui.class_url(name)}/zip">Download all as .zip</a>' if rows else "")
        body = (f'<header class="class-head" style="{ui.hue_style(name)}"><h1>{esc(name)}</h1></header>'
                f'<p class="lede">{lede}</p><div class="row-actions" style="margin-bottom:1.4rem">{zip_btn}</div>'
                + ui.note_list(rows, "Nothing here yet",
                               "Lectures sorted into this class show up here." if name != UNSORTED else
                               "Every lecture found its class."))
        return show(name, body, ctx)

    @app.get("/class/{name}", response_class=HTMLResponse)
    def by_class(name: str, role: str = Depends(member)):
        if name == UNSORTED:
            return RedirectResponse("/unsorted", status_code=303)
        desc = next((c.description for c in cfg.classes if c.name == name), "")
        n = len(store.list_notes(name))
        lede = esc(desc + (". " if desc and not desc.endswith(".") else " " if desc else "")) + \
            f"{n} lecture{'s' if n != 1 else ''}."
        return class_page(name, role, f"class:{name}", lede)

    @app.get("/unsorted", response_class=HTMLResponse)
    def unsorted(role: str = Depends(member)):
        return class_page(UNSORTED, role, "unsorted",
                          "The sorter wasn't sure where these go. Open one and pick its class.")

    @app.get("/search", response_class=HTMLResponse)
    def search(q: str = "", role: str = Depends(member)):
        ctx = context(role)
        q = q.strip()
        rows = store.search(q) if q else []
        snippets = {}
        for r in rows:
            m = store.meeting(r)
            text = " ".join(filter(None, [r["summary_md"], m.notes_markdown, m.transcript]))
            snippets[r["id"]] = ui.snippet(text, q)
        found = f"{len(rows)} lecture{'s' if len(rows) != 1 else ''} mention “{esc(q)}”." if q else ""
        body = (ui.search_box(q) + "<h1>Search</h1>" + (f'<p class="lede">{found}</p>' if q else "")
                + (ui.note_list(rows, "No matches", "Try a shorter word, a topic, or a name.", snippets) if q else ""))
        return show(f"Search: {q}" if q else "Search", body, ctx)

    @app.get("/note/{note_id}", response_class=HTMLResponse)
    def note(note_id: str, role: str = Depends(member)):
        r = store.get(note_id)
        if not r:
            return not_found(role)
        ctx = context(role, f"class:{r['class_name']}" if r["class_name"] != UNSORTED else "unsorted")
        m = store.meeting(r)
        nid = quote(note_id, safe="")
        cls = r["class_name"] or ""
        facts = [("Date", ui.long_date(r["date"]))]
        if r["summary_md"]:
            facts.append(("Summary", f"{r['summary_model']}, from the transcript"))
        else:
            facts.append(("Summary", "Granola's" + ("" if r["has_transcript"] else " (no transcript shared)")))
        if r["classified_by"]:
            sure = f", {round((r['confidence'] or 0) * 100)}% sure" if r["classified_by"] == "ollama" else ""
            facts.append(("Sorted by", SORTED_BY.get(r["classified_by"], r["classified_by"]) + sure))
        facts_html = "".join(f"<div><dt>{esc(k)}</dt><dd>{esc(v)}</dd></div>" for k, v in facts)

        options = "".join(f'<option value="{esc(c)}"{" selected" if c == cls else ""}>{esc(c)}</option>'
                          for c in cfg.class_names() + [UNSORTED])
        actions = [f'<form class="inline row-actions" method="post" action="/note/{nid}/class">'
                   f'<select name="class_name" aria-label="Class">{options}</select><button>Move</button></form>',
                   f'<a class="btn" href="/note/{nid}/download">Download .md</a>']
        if role == "admin":
            if m.transcript.strip():
                actions.append(f'<form class="inline" method="post" action="/note/{nid}/resummarize">'
                               f'<button>Rewrite summary</button></form>')
            actions.append(f'<form class="inline" method="post" action="/note/{nid}/delete" '
                           f'data-confirm="Delete this lecture? Its file is removed too.">'
                           f'<button class="danger">Delete</button></form>')

        notice = ""
        if r["status"] in ("queued", WORKING):
            notice = (f'<div class="callout" data-refresh="15">A new summary is being written with '
                      f'{esc(cfg.effective_summary_model)}. This page refreshes on its own.</div>')
        elif r["error"]:
            retry = (f'<form method="post" action="/note/{nid}/resummarize"><button>Try again</button></form>'
                     if role == "admin" else "")
            notice = (f'<div class="callout">{esc(r["error"][:300])}. Showing Granola\'s summary instead.{retry}</div>')

        docs = []  # (id, label, html)
        if r["summary_md"]:
            docs.append(("summary", "Summary", f'<div class="prose">{ui.render_md(r["summary_md"])}</div>'
                         f'<p class="byline">Written by {esc(r["summary_model"])} from the transcript.</p>'))
        if m.notes_markdown.strip() and (not r["summary_md"] or cfg.keep_granola_notes):
            docs.append(("granola", "Granola's summary" if r["summary_md"] else "Summary",
                         f'<div class="prose">{ui.render_md(m.notes_markdown)}</div>'))
        if m.private_notes.strip():
            docs.append(("typed", "Typed notes", f'<div class="prose">{ui.render_md(m.private_notes)}</div>'))
        if m.transcript.strip():
            docs.append(("transcript", "Transcript", f'<div class="transcript">{esc(m.transcript.strip())}</div>'))
        if not docs:
            docs.append(("summary", "Summary", '<p class="muted">Granola returned no notes for this lecture.</p>'))
        tabs = "".join(f'<button type="button" role="tab" data-for="{d}" aria-controls="{d}">{esc(label)}</button>'
                       for d, label, _ in docs)
        panes = "".join(f'<section class="doc" id="{d}" role="tabpanel">{content}</section>' for d, _, content in docs)
        tabbar = f'<div class="doc-tabs" role="tablist" data-tabs>{tabs}</div>' if len(docs) > 1 else ""

        title = r["lecture_title"] or r["title"] or "Untitled"
        crumb_href = ui.class_url(cls) if cls != UNSORTED else "/unsorted"
        body = (f'<div style="{ui.hue_style(cls)}"><a class="crumb" href="{crumb_href}">{esc(cls)}</a>'
                f"<h1>{esc(title)}</h1><dl class=facts>{facts_html}</dl>"
                f'<div class="row-actions">{"".join(actions)}</div>'
                f'<div style="height:1.2rem"></div>{notice}{tabbar}{panes}</div>')
        return show(title, body, ctx, math=True)

    @app.post("/note/{note_id}/class")
    async def move(note_id: str, request: Request, role: str = Depends(member)):
        class_name = str((await request.form()).get("class_name", ""))
        if class_name not in cfg.class_names() + [UNSORTED]:
            raise HTTPException(400, "unknown class")
        store.set_class(note_id, class_name, keep_granola=cfg.keep_granola_notes)
        return RedirectResponse(f"/note/{quote(note_id, safe='')}", status_code=303)

    @app.post("/note/{note_id}/resummarize")
    def resummarize(note_id: str, role: str = Depends(admin)):
        if store.requeue(note_id):
            pipeline.wake()
        return RedirectResponse(f"/note/{quote(note_id, safe='')}", status_code=303)

    @app.post("/note/{note_id}/delete")
    def delete(note_id: str, role: str = Depends(admin)):
        store.delete(note_id)
        return RedirectResponse("/", status_code=303)

    @app.get("/note/{note_id}/download")
    def download(note_id: str, role: str = Depends(member)):
        r = store.get(note_id)
        if not r or not r["md_path"] or not Path(r["md_path"]).exists():
            raise HTTPException(404)
        return FileResponse(r["md_path"], media_type="text/markdown", filename=Path(r["md_path"]).name)

    @app.get("/class/{name}/zip")
    def class_zip(name: str, role: str = Depends(member)):
        rows = store.list_notes(name)
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
            for r in rows:
                p = Path(r["md_path"]) if r["md_path"] else None
                if p and p.exists():
                    z.write(p, arcname=f"{name}/{p.name}")
        buf.seek(0)
        return StreamingResponse(buf, media_type="application/zip",
                                 headers={"Content-Disposition": f"attachment; filename*=UTF-8''{quote(name)}.zip"})

    # -- settings --------------------------------------------------------------------------
    def model_select(field: str, current: str, models: list[dict] | None, blank: str | None = None) -> str:
        names = [m["name"] for m in models or []]
        opts = [f'<option value="">{esc(blank)}</option>'] if blank is not None else []
        if current and current not in names:
            opts.append(f'<option value="{esc(current)}" selected>{esc(current)} (not installed)</option>')
        for m in models or []:
            sel = " selected" if m["name"] == current else ""
            opts.append(f'<option value="{esc(m["name"])}"{sel}>{esc(m["name"])} ({esc(ollama.size_label(m))})</option>')
        return f'<select id="{field}" name="{field}">{"".join(opts)}</select>'

    @app.get("/settings", response_class=HTMLResponse)
    def settings(request: Request, saved: int = 0, queued: int = -1, role: str = Depends(admin)):
        ctx = context(role, "settings")
        models = list_models(cfg.ollama_host)
        flash = ""
        if saved:
            flash = '<div class="flash">Settings saved. New lectures use them right away.</div>'
        if queued >= 0:
            flash = f'<div class="flash">{queued} lecture{"s" if queued != 1 else ""} queued for a new summary.</div>'

        if models is None:
            ai_state = (f'<div class="callout">Ollama isn\'t answering at {esc(cfg.ollama_host)}, so summaries and AI '
                        f'sorting are paused. Open the Ollama app on this computer, then reload.</div>')
        elif not models:
            ai_state = ('<div class="callout">Ollama has no models yet. On this computer run '
                        f'<code>ollama pull {esc(ollama.recommended_model(ollama.total_ram_gb()))}</code>, then reload.</div>')
        else:
            ai_state = ""
        checked = lambda b: " checked" if b else ""  # noqa: E731
        ai = (f'<section class="panel"><h2>AI models</h2>{ai_state}'
              f'<div class="field"><label for="summary_model">Writes summaries</label>'
              f'{model_select("summary_model", cfg.summary_model, models, blank="Same as the sorting model")}'
              f'<span class="hint">Reads each transcript and writes the lecture notes. Bigger models write better notes '
              f'but take longer. Granola only shares transcripts from paid plans; other lectures keep Granola\'s summary.</span></div>'
              f'<div class="field"><label for="ollama_model">Sorts lectures into classes</label>'
              f'{model_select("ollama_model", cfg.ollama_model, models)}'
              f'<span class="hint">Using the same model for both avoids reloading it between the two steps.</span></div>'
              f'<label class="check"><input type="checkbox" name="summary_enabled" value="1"{checked(cfg.summary_enabled)}>'
              f'<span>Write our own summary when a transcript is shared</span></label>'
              f'<label class="check"><input type="checkbox" name="keep_granola_notes" value="1"{checked(cfg.keep_granola_notes)}>'
              f'<span>Also keep Granola\'s summary next to ours</span></label>'
              f'<label class="check"><input type="checkbox" name="ollama_enabled" value="1"{checked(cfg.ollama_enabled)}>'
              f'<span>Use AI at all (off: only folder and title rules sort lectures)</span></label>'
              f'<div class="field"><label for="min_confidence">Minimum confidence to file a lecture</label>'
              f'<input id="min_confidence" name="min_confidence" type="number" min="0" max="1" step="0.05" value="{cfg.min_confidence}">'
              f'<span class="hint">Below this, the lecture goes to Unsorted for a person to file.</span></div></section>')

        rows = []
        for i, c in enumerate(cfg.classes + [ClassDef("")] * 2):
            rows.append(
                f'<div class="c"><input name="class_name_{i}" value="{esc(c.name)}" placeholder="Class name, e.g. CS 101" aria-label="Class name">'
                f'<input name="class_aliases_{i}" value="{esc(", ".join(c.aliases))}" placeholder="Other names, comma separated" aria-label="Other names">'
                f'<input name="class_desc_{i}" value="{esc(c.description)}" placeholder="What it covers (helps the AI)" aria-label="Description">'
                + (f'<label class="check" style="margin:0"><input type="checkbox" name="class_remove_{i}" value="1"><span>Remove</span></label>'
                   if c.name else "<span></span>") + "</div>")
        classes = (f'<section class="panel"><h2>Classes</h2><p class="lede small">Lectures are sorted into these. A Granola '
                   f'folder or a title that matches a name here files the lecture without asking the AI. Removing a '
                   f'class keeps its lectures; move them from their pages.</p>'
                   f'<div class="classes-edit">{"".join(rows)}</div></section>')

        form = (f'<form method="post" action="/settings">{ai}{classes}'
                f'<button class="primary">Save settings</button></form>')

        ts = tailscale()
        urls = hostinfo.server_urls(cfg.web_port, ts)
        url = urls[0]
        cmds = hostinfo.invite_commands(url, cfg.pool_password)
        ts_note = ("" if ts.get("running") else
                   '<p class="callout">Tailscale isn\'t running on this computer, so your laptop can only reach it on '
                   'the same Wi-Fi. Install Tailscale on both and sign in to the same account.</p>')
        invite = (f'<section class="panel"><h2>Connect your laptop</h2>{ts_note}'
                  f'<p class="lede small">On the computer you record lectures on, paste this one line into Terminal '
                  f'(Mac) or PowerShell (Windows). It installs everything with this address and password filled in, '
                  f'then finishes setup in the browser.</p>'
                  f'<div class="field"><label>Mac or Linux</label><pre class="code" id="inv-mac">{esc(cmds["mac"])}</pre>'
                  f'<div><button type="button" data-copy="inv-mac">Copy</button></div></div>'
                  f'<div class="field"><label>Windows</label><pre class="code" id="inv-win">{esc(cmds["windows"])}</pre>'
                  f'<div><button type="button" data-copy="inv-win">Copy</button></div></div>'
                  f'<p class="small muted">Address: {esc(url)}. Password: {esc(cfg.pool_password or "none")}.</p></section>')

        rel = latest()
        if update.is_newer(rel):
            upd = (f'<p>Version {esc(rel.tag)} is out (<a href="{esc(rel.page)}">what changed</a>). You have {__version__}.</p>'
                   f'<form method="post" action="/settings/update"><button class="primary">Update now</button></form>')
        else:
            upd = f'<p class="muted">You have version {__version__}, the newest.</p>'
        auto = "on" if cfg.auto_update else "off"
        n_done = ctx["total"]
        maintenance = (f'<section class="panel"><h2>Updates</h2>{upd}'
                       f'<p class="small muted">Automatic updates are {auto} (auto_update in config.toml).</p></section>'
                       f'<section class="panel"><h2>Rewrite every summary</h2><p class="lede small">Useful after switching '
                       f'models. Lectures stay readable while they are rewritten, one at a time.</p>'
                       f'<form method="post" action="/settings/resummarize-all" data-confirm="Rewrite all {n_done} '
                       f'summaries with {esc(cfg.effective_summary_model)}? This can take a while.">'
                       f'<button>Rewrite all summaries</button></form></section>')
        body = f"<h1>Settings</h1>{flash}{form}{invite}{maintenance}"
        return show("Settings", body, ctx)

    @app.post("/settings")
    async def save_settings(request: Request, role: str = Depends(admin)):
        f = await request.form()
        cfg.summary_model = str(f.get("summary_model", cfg.summary_model)).strip()
        cfg.ollama_model = str(f.get("ollama_model", cfg.ollama_model)).strip() or cfg.ollama_model
        cfg.summary_enabled = f.get("summary_enabled") == "1"
        cfg.keep_granola_notes = f.get("keep_granola_notes") == "1"
        cfg.ollama_enabled = f.get("ollama_enabled") == "1"
        try:
            cfg.min_confidence = max(0.0, min(1.0, float(f.get("min_confidence", cfg.min_confidence))))
        except ValueError:
            pass
        classes, seen, i = [], set(), 0
        while f"class_name_{i}" in f:
            name = str(f.get(f"class_name_{i}", "")).strip()
            if name and name != UNSORTED and name not in seen and f.get(f"class_remove_{i}") != "1":
                aliases = [a.strip() for a in str(f.get(f"class_aliases_{i}", "")).split(",") if a.strip()]
                classes.append(ClassDef(name, aliases, str(f.get(f"class_desc_{i}", "")).strip()))
                seen.add(name)
            i += 1
        cfg.classes = classes
        save_config(cfg)
        return RedirectResponse("/settings?saved=1", status_code=303)

    @app.post("/settings/resummarize-all")
    def resummarize_all(role: str = Depends(admin)):
        n = store.requeue_all()
        pipeline.wake()
        return RedirectResponse(f"/settings?queued={n}", status_code=303)

    @app.post("/settings/update")
    def update_now(role: str = Depends(admin)):
        rel = latest(0)
        if update.is_newer(rel):
            threading.Thread(target=update.apply, args=(rel, cfg.home), daemon=True).start()
        nonce = secrets.token_urlsafe(16)
        body = ('<div class="login"><form><h1>Updating</h1><p class="muted">This restarts on the new version in about '
                'a minute.</p><a class="btn primary" href="/settings">Back to settings</a></form></div>')
        return respond(ui.bare_page("Updating", body, nonce), nonce=nonce)

    # -- API -------------------------------------------------------------------------------
    @app.get("/api/health")
    def health(request: Request):
        require_key(request)
        return {"ok": True, "pool_name": cfg.pool_name, "classes": cfg.class_names(), "version": __version__,
                "notes": sum(n for _, n in store.classes_summary())}

    @app.post("/api/ingest")
    def ingest(request: Request, payload: dict):
        """Your laptop pushes one finished note. It is queued; the pipeline summarizes and files it."""
        require_key(request)
        try:
            m = meeting_from_dict(payload)
        except ValueError as e:
            raise HTTPException(400, str(e))
        store.enqueue(m)
        pipeline.wake()
        c = classify_by_rules(m, cfg.classes)
        return {"id": m.id, "status": "queued", "class_name": c.class_name if c else None,
                "has_transcript": bool(m.transcript.strip())}

    @app.get("/api/notes/{note_id}/status")
    def api_note_status(note_id: str, request: Request):
        """For your laptop: has a lecture been filed yet, and where?"""
        require_key(request)
        r = store.get(note_id)
        if not r:
            raise HTTPException(404, "no such note")
        return {"id": note_id, "status": r["status"] or "done", "class_name": r["class_name"],
                "summary_model": r["summary_model"], "has_transcript": bool(r["has_transcript"]),
                "path": f"/note/{quote(note_id, safe='')}"}

    @app.get("/api/notes")
    def api_notes(class_name: str | None = None, role: str = Depends(member)):
        return [
            {k: r[k] for k in ("id", "title", "lecture_title", "date", "owner", "class_name", "confidence",
                               "classified_by", "has_transcript", "summary_model")}
            | {"topics": json.loads(r["topics"] or "[]")}
            for r in store.list_notes(class_name)
        ]

    @app.get("/api/status")
    def api_status(role: str = Depends(member)):
        return JSONResponse({"counts": store.status_counts(), "working_on": pipeline.current,
                             "summary_model": cfg.effective_summary_model, "version": __version__})

    return app
