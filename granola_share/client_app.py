"""The Granola Share app on your laptop: a small web page on this computer for setup and status.

The background watcher (`granola-share client run`) serves it on http://127.0.0.1:<port>, so
setting up never needs the terminal: connect to your library, sign in to Granola, choose how to
send, allow transcript copying, done. Afterwards it shows what was sent and where it was filed.

Only this computer can reach it (it listens on 127.0.0.1 and checks the Host header), and it
needs a per-install token that the "Granola Share" launcher passes in, so websites and other
programs can't drive it.
"""

from __future__ import annotations

import getpass
import hmac
import json
import os
import platform
import secrets
import socket
import threading
import time
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse

from . import __version__, autostart, dialogs, ui
from .config import ClientConfig, load_client_config, save_client_config
from .ui import esc

DEFAULT_PORT = 8765
COOKIE = "gs_app"
CSP = ("default-src 'self'; script-src 'nonce-{nonce}'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; "
       "font-src https://fonts.gstatic.com; img-src 'self' data:; form-action 'self'; frame-ancestors 'none'; base-uri 'none'")

APP_CSS = r"""
.solo{max-width:46rem;margin:0 auto;padding:2.4rem 1.1rem 5rem}
.solo>header{margin-bottom:1.4rem}
.solo>header p{margin:.2rem 0 0}
.sheet{background:var(--sheet);border:1px solid var(--rule);border-radius:.8rem;padding:.4rem clamp(1rem,4vw,2rem) 1.4rem}
.step{display:grid;grid-template-columns:2.2rem minmax(0,1fr);gap:.1rem 1rem;padding:1.3rem 0;border-top:1px solid var(--grid)}
.step:first-child{border-top:0}
.step-n{font:700 1.1rem/1 var(--head);width:2.1rem;height:2.1rem;border-radius:50%;display:grid;place-items:center;
  border:2px solid var(--rule);color:var(--ink-2);background:var(--sheet)}
.step.done .step-n{background:var(--pen);border-color:var(--pen);color:var(--pen-ink)}
.step.locked{opacity:.45;pointer-events:none}
.step h2{margin:.15rem 0 .35rem}
.step p{margin:.2rem 0 .7rem;max-width:60ch}
.row{display:flex;flex-wrap:wrap;gap:.6rem;align-items:center;margin:.5rem 0}
.row input[type=text],.row input[type=password],.row input[type=url]{flex:1 1 14rem;min-width:0}
.choice{display:flex;gap:.6rem;align-items:flex-start;margin:.35rem 0}
.choice input{margin-top:.3rem}
.say{font-size:.92rem;margin:.4rem 0 0}
.say.good{color:var(--pen);font-weight:600}
.say.bad{color:var(--bad);font-weight:600}
.say.wait{color:var(--ink-2)}
.status{display:grid;grid-template-columns:repeat(auto-fit,minmax(12rem,1fr));gap:.8rem;margin:0 0 1.6rem}
.status div{background:var(--sheet);border:1px solid var(--rule);border-radius:.6rem;padding:.8rem 1rem}
.status dt{font-size:.8rem;color:var(--ink-2)}
.status dd{margin:.15rem 0 0;font-weight:600}
.status .bad{color:var(--bad)}
.recent{list-style:none;margin:0;padding:0;border-top:1px solid var(--grid)}
.recent li{display:flex;flex-wrap:wrap;justify-content:space-between;gap:.2rem 1rem;padding:.7rem .2rem;border-bottom:1px solid var(--grid)}
.recent .t{font:600 1.05rem var(--head)}
.recent .s{font-size:.88rem;color:var(--ink-2)}
details.more{margin-top:1.8rem}
details.more summary{cursor:pointer;font:600 1.1rem var(--head)}
"""

APP_JS = r"""
function post(url, data){
  return fetch(url,{method:'POST',headers:{'Content-Type':'application/json','X-Granola-Share':'1'},
    body:JSON.stringify(data||{})}).then(function(r){return r.json().then(function(j){if(!r.ok)throw new Error(j.detail||r.statusText);return j;});});
}
function say(id, text, kind){var el=document.getElementById(id);if(el){el.textContent=text;el.className='say '+(kind||'');}}
document.querySelectorAll('[data-action]').forEach(function(el){
  el.addEventListener(el.tagName==='FORM'?'submit':'click',function(e){
    e.preventDefault();
    var data={};
    if(el.tagName==='FORM'){new FormData(el).forEach(function(v,k){data[k]=v;});
      el.querySelectorAll('input[type=checkbox]').forEach(function(c){data[c.name]=c.checked;});}
    if(el.dataset.confirm&&!confirm(el.dataset.confirm))return;
    var out=el.dataset.out;
    if(out)say(out, el.dataset.busy||'Working…','wait');
    post(el.dataset.action,data).then(function(j){
      if(out)say(out,j.message||'Done.','good');
      if(j.reload!==false)setTimeout(function(){location.reload();}, j.delay||600);
    }).catch(function(err){if(out)say(out,err.message,'bad');});
  });
});
document.querySelectorAll('[data-copy]').forEach(function(b){b.addEventListener('click',function(){
  navigator.clipboard.writeText(document.getElementById(b.dataset.copy).textContent.trim()).then(function(){
    var t=b.textContent;b.textContent='Copied';setTimeout(function(){b.textContent=t;},1500);});});});
var watch=document.body.dataset.watch;
if(watch){var seen=null;setInterval(function(){fetch('/api/state',{headers:{'X-Granola-Share':'1'}}).then(function(r){return r.json();})
  .then(function(s){var key=JSON.stringify([s.signed_in,s.login.running,s.login.error,s.copy.allowed,s.watching,s.recent_key,s.problem]);
    if(seen!==null&&key!==seen)location.reload();seen=key;});}, parseInt(watch,10)*1000);}
"""


def mac() -> bool:
    return platform.system() == "Darwin"


# --- what runs in the background ---------------------------------------------------------

class ClientRuntime:
    """The watcher, the transcript copier, and the Granola sign-in, started and restarted from the page."""

    def __init__(self, home: Path, log=print):
        self.home = Path(home)
        self.log = log
        self.stop = threading.Event()
        self.client = None
        self.grabber = None
        self._grab_stop: threading.Event | None = None
        self.thread: threading.Thread | None = None
        self.login = {"running": False, "error": None, "started": None}
        self._updater = False
        self._lock = threading.Lock()

    def config(self) -> ClientConfig:
        return load_client_config(self.home)

    def signed_in(self) -> bool:
        return self.config().tokens_path.exists()

    def configured(self) -> bool:
        return bool(self.config().server_url and self.signed_in())

    @property
    def watching(self) -> bool:
        return bool(self.thread and self.thread.is_alive())

    # -- transcript copying
    def _grabber_ui(self):
        if self.grabber is not None:
            return self.grabber
        from .transcript_grab import MacUI, TranscriptGrabber, available

        if available():
            return None
        store = self.client.transcripts if self.client else None
        self.grabber = TranscriptGrabber(self.home, MacUI(), store, log=self.log,
                                         on_new=lambda copied: self.client and self.client.wake())
        return self.grabber

    def copy_status(self) -> dict:
        cc = self.config()
        if not mac():
            return {"available": False, "enabled": False, "allowed": False, "why": "only on macOS for now"}
        from .transcript_grab import available

        why = available()
        g = self._grabber_ui() if not why else None
        return {"available": g is not None, "enabled": cc.copy_transcripts, "why": why,
                "allowed": bool(g and g.ui.trusted())}

    def _ask_save(self, title: str) -> bool:
        """The one popup, right after a recording ends while Granola isn't in front."""
        lib = self.config().pool_name or "your library"
        what = f"“{title}” just finished recording." if title else "Your Granola recording just finished."
        text = (f"{what}\n\nSave it to {lib} with its transcript? granola-share opens Granola, copies the "
                "transcript, and brings you back.")
        return dialogs.ask_choice("granola-share", text, ["Not now", "Save"], default="Save") == "Save"

    def request_permission(self) -> None:
        g = self._grabber_ui()
        if g is not None:
            g.request_permission()

    # -- the watcher
    def start_watching(self) -> None:
        with self._lock:
            if self.watching:
                return
            from .cli import _share_client

            cc = self.config()
            self.client = _share_client(cc, log=self.log)
            if self.grabber is not None:
                self.grabber.store = self.client.transcripts or self.grabber.store
            self.apply_copying(cc)
            self.thread = threading.Thread(target=self.client.run_loop, kwargs={"stop": self.stop},
                                           name="watcher", daemon=True)
            self.thread.start()
            if not self._updater:
                from .update import start_auto_update

                start_auto_update(self.home, lambda: load_client_config(self.home).auto_update, self.stop, log=self.log)
                self._updater = True
            self.log(f"[app] watching Granola for '{cc.pool_name}'")

    def apply_copying(self, cc: ClientConfig) -> None:
        """Start or stop the transcript copier to match the settings."""
        want = cc.copy_transcripts and cc.include_transcripts and mac()
        running = self._grab_stop is not None and not self._grab_stop.is_set()
        if want and not running:
            g = self._grabber_ui()
            if g is None:
                return
            from .transcript_grab import TranscriptStore

            if self.client is not None:
                if self.client.transcripts is None:
                    self.client.transcripts = TranscriptStore(self.home)
                g.store = self.client.transcripts
                self.client.copy_state = g.copy_state
                self.client.allow_copying = g.request_permission
                self.client.capture_for = g.capture_for
                self.client.handled_since = g.handled_since
                g.wanted = self.client.missing_transcripts
            g.ask_save = self._ask_save
            self._grab_stop = threading.Event()
            g.start(self._grab_stop)
        elif not want and running:
            self._grab_stop.set()
            if self.client is not None:
                from .client import COPY_OFF

                self.client.copy_state = lambda: COPY_OFF

    def reload(self) -> None:
        """Settings changed on the page: the running watcher picks them up."""
        cc = self.config()
        c = self.client
        if c is not None:
            reconnected = (cc.server_url, cc.pool_key) != (c.cc.server_url, c.cc.pool_key)
            c.cc = cc
            if reconnected and getattr(c, "send_problem_kind", None):
                # The page just checked the new address and password: drop the old warning and
                # send what's waiting now, not at the next check minutes later.
                c.send_problem = c.send_problem_kind = c.last_error = None
                c.wake()
        self.apply_copying(cc)

    def check_now(self) -> None:
        if self.client is not None:
            self.client.wake()

    # -- Granola sign-in
    def start_login(self) -> None:
        if self.login["running"]:
            return
        self.login = {"running": True, "error": None, "started": time.time()}

        def go():
            from .oauth import GranolaOAuth

            try:
                GranolaOAuth(self.config()).login(open_browser=True, log=self.log)
                self.login = {"running": False, "error": None, "started": None}
                if self.configured():
                    self.start_watching()
            except Exception as e:
                self.login = {"running": False, "error": str(e), "started": None}

        threading.Thread(target=go, name="granola-login", daemon=True).start()


# --- the page ----------------------------------------------------------------------------

def _token(home: Path) -> str:
    path = home / "ui_token"
    try:
        if path.exists():
            return path.read_text().strip()
        home.mkdir(parents=True, exist_ok=True)
        tok = secrets.token_urlsafe(24)
        path.write_text(tok)
        os.chmod(path, 0o600)
        return tok
    except OSError:
        return secrets.token_urlsafe(24)


def _recent(cc: ClientConfig, limit: int = 12) -> list[dict]:
    try:
        seen = json.loads(cc.state_path.read_text()).get("seen", {})
    except (OSError, ValueError):
        return []
    rows = sorted(seen.values(), key=lambda e: e.get("at", ""), reverse=True)[:limit]
    return rows


def _describe(e: dict) -> str:
    d = e.get("decision")
    if d == "shared":
        where = f" in {e['class_name']}" if e.get("class_name") else ""
        if e.get("filed") is True:
            return f"Filed{where}" + (" with its transcript" if e.get("transcript_chars") else "")
        return "Sent, being summarized" if e.get("filed") is False else f"Shared{where}"
    if d == "pending" and e.get("error"):
        return "Waiting to send" + (" with its transcript" if e.get("transcript_chars") else "")
    return {"skipped": "Skipped", "pending": "Waiting for your answer"}.get(d, d or "")


def create_client_app(runtime: ClientRuntime, *, port: int = DEFAULT_PORT, check_server=None,
                      extra_hosts: tuple[str, ...] = ()) -> FastAPI:
    from .client import check_server as _check_server

    check_server = check_server or _check_server
    home = runtime.home
    token = _token(home)
    app = FastAPI(title="Granola Share", docs_url=None, redoc_url=None, openapi_url=None)
    allowed_hosts = {f"127.0.0.1:{port}", f"localhost:{port}", *extra_hosts}

    @app.middleware("http")
    async def guard(request: Request, call_next):
        # DNS rebinding: a web page can point a hostname at 127.0.0.1, but it can't fake the Host header.
        if request.headers.get("host", "") not in allowed_hosts:
            return JSONResponse({"detail": "not here"}, status_code=403)
        return await call_next(request)

    def authed(request: Request) -> bool:
        return hmac.compare_digest(request.cookies.get(COOKIE, ""), token)

    def require(request: Request) -> None:
        # The custom header can't be sent cross-site without a CORS preflight we never allow.
        if not authed(request) or request.headers.get("x-granola-share") != "1":
            raise HTTPException(403, "Open Granola Share from its app icon.")

    def respond(title: str, body: str, watch: int = 0) -> HTMLResponse:
        nonce = secrets.token_urlsafe(16)
        page = (ui.head(title, nonce).replace("</style>", APP_CSS + "</style>")
                + f'<body data-watch="{watch or ""}"><div class="solo">{body}</div>'
                + f'<script nonce="{nonce}">{APP_JS}</script></body></html>')
        return HTMLResponse(page, headers={"Content-Security-Policy": CSP.format(nonce=nonce)})

    @app.get("/", response_class=HTMLResponse)
    def index(request: Request, t: str = ""):
        if t and hmac.compare_digest(t, token):
            resp = RedirectResponse("/", status_code=303)
            resp.set_cookie(COOKIE, token, httponly=True, samesite="strict", max_age=60 * 60 * 24 * 365)
            return resp
        if not authed(request):
            return respond("Granola Share", "<header><h1>Granola Share</h1></header><p class=lede>Open "
                           "<strong>Granola Share</strong> from your Applications folder to see this page.</p>")
        return status_page() if runtime.configured() and runtime.watching else setup_page()

    # -- setup
    def setup_page() -> HTMLResponse:
        cc = runtime.config()
        prefill = _read_prefill(home)
        server = prefill.get("server") or cc.server_url
        key = prefill.get("key") or cc.pool_key
        connected = bool(cc.server_url and cc.pool_name)
        signed = runtime.signed_in()
        login = runtime.login
        copy = runtime.copy_status()

        def step(n, title, done, locked, inner):
            cls = "step" + (" done" if done else "") + (" locked" if locked else "")
            mark = "✓" if done else str(n)
            return f'<section class="{cls}"><div class="step-n">{mark}</div><div><h2>{esc(title)}</h2>{inner}</div></section>'

        pool_inner = (
            f'<p>The address and password from your Mac mini\'s setup (also under Settings in its web page).</p>'
            f'<form class="row" data-action="/api/pool" data-out="pool-say" data-busy="Connecting…">'
            f'<input type="url" name="server" value="{esc(server)}" placeholder="http://mac-mini:8787" aria-label="Library address" required>'
            f'<input type="password" name="key" value="{esc(key)}" placeholder="Password" aria-label="Password">'
            f'<button class="{"" if connected else "primary"}">{"Reconnect" if connected else "Connect"}</button></form>'
            + (f'<p class="say good" id="pool-say">Connected to {esc(cc.pool_name)}.</p>' if connected
               else '<p class="say" id="pool-say"></p>'))
        if signed:
            granola_inner = '<p class="say good">Signed in to Granola.</p>'
        elif login["running"]:
            granola_inner = ('<p>A browser tab opened for Granola. Sign in there, then come back to this tab.</p>'
                             '<p class="say wait">Waiting for you to finish signing in…</p>')
        else:
            err = f'<p class="say bad">{esc(login["error"])}. Try again.</p>' if login["error"] else ""
            granola_inner = ('<p>Sign in with your Granola account, the one you record lectures with.</p>'
                             f'{err}<button class="primary" data-action="/api/login" data-out="login-say" '
                             'data-busy="Opening Granola…">Sign in to Granola</button><p class="say" id="login-say"></p>')
        ask = cc.mode == "ask"
        prefs_inner = (
            '<p>You can change this later.</p><form data-action="/api/prefs" data-out="prefs-say">'
            f'<label class="choice"><input type="radio" name="mode" value="auto"{"" if ask else " checked"}>'
            '<span>Send every lecture automatically</span></label>'
            f'<label class="choice"><input type="radio" name="mode" value="ask"{" checked" if ask else ""}>'
            '<span>Ask me before sending each one</span></label>'
            + (f'<label class="choice"><input type="checkbox" name="copy_transcripts"{" checked" if cc.copy_transcripts else ""}>'
               '<span>Copy each transcript from the Granola app (free Granola plans only share transcripts this way)</span></label>'
               if mac() else "")
            + '<div class="row"><button>Save</button><span class="say" id="prefs-say"></span></div></form>')
        steps = [step(1, "Connect to your library", connected, False, pool_inner),
                 step(2, "Sign in to Granola", signed, not connected, granola_inner),
                 step(3, "How to send", connected and signed, not signed, prefs_inner)]
        n = 4
        if mac() and cc.copy_transcripts and copy["available"]:
            if copy["allowed"]:
                allow_inner = '<p class="say good">Allowed. Transcripts are copied while Granola is in front.</p>'
            else:
                allow_inner = (
                    "<p>To copy transcripts, granola-share needs to read the Granola window. Click the button, "
                    "then turn on <strong>python3.12</strong> in the list that opens.</p>"
                    '<button class="primary" data-action="/api/allow" data-out="allow-say" data-busy="Opening System Settings…">'
                    'Open Accessibility settings</button><p class="say" id="allow-say"></p>' + _python_fallback())
            steps.append(step(n, "Allow transcript copying", copy["allowed"], not signed, allow_inner))
            n += 1
        ready = connected and signed
        finish_inner = ("<p>granola-share keeps running in the background and starts when you log in. "
                        "When a lecture finishes in Granola, it's sent to your library.</p>"
                        f'<button class="primary" data-action="/api/finish" data-out="finish-say"{"" if ready else " disabled"}>'
                        'Finish setup</button><p class="say" id="finish-say"></p>')
        steps.append(step(n, "Start sending", False, not ready, finish_inner))
        body = (f'<header><h1>Granola Share</h1><p class="lede">Send your Granola lectures to your library on your '
                f'Mac mini, where your own model writes their notes. {n} short steps.</p></header>'
                f'<div class="sheet">{"".join(steps)}</div>')
        return respond("Set up Granola Share", body, watch=2)

    # -- status
    def status_page() -> HTMLResponse:
        cc = runtime.config()
        copy = runtime.copy_status()
        facts = [("Library", cc.pool_name or "not connected", False), ("Granola", "signed in" if runtime.signed_in() else "signed out", not runtime.signed_in()),
                 ("Watching", "every few minutes" if runtime.watching else "stopped", not runtime.watching)]
        if mac() and cc.copy_transcripts:
            facts.append(("Transcripts", "copied from Granola" if copy["allowed"] else "needs permission", not copy["allowed"]))
        problem = runtime.client.last_error if runtime.client is not None else None
        signin_problem = bool(problem and "granola-share login" in problem)  # every sign-in failure says this
        send_kind = getattr(runtime.client, "send_problem_kind", None) if problem else None
        if signin_problem:
            facts[1] = ("Granola", "needs you to sign in again", True)
        elif send_kind == "password":
            facts[0] = ("Library", "needs its new password", True)
        elif send_kind == "unreachable":
            facts[0] = ("Library", "can't be reached", True)
        facts_html = "".join(f'<div><dt>{esc(k)}</dt><dd class="{"bad" if bad else ""}">{esc(v)}</dd></div>' for k, v, bad in facts)
        if problem and send_kind == "password":
            problem_html = (
                f'<div class="callout">{esc(problem)}'
                '<form class="row" data-action="/api/pool" data-out="problem-say" data-busy="Connecting…">'
                f'<input type="hidden" name="server" value="{esc(cc.server_url)}">'
                '<input type="password" name="key" placeholder="The new password" aria-label="Library password" required>'
                '<button class="primary">Reconnect</button></form>'
                '<p class="small muted">It\'s shown on the Mac mini, in your library\'s Settings under Connect your laptop.</p>'
                '<p class="say" id="problem-say"></p></div>')
        elif problem:
            fix = ('<button class="primary" data-action="/api/login" data-out="problem-say">Sign in to Granola again</button>'
                   if signin_problem else "")
            said = ("Granola signed you out, so new lectures can't be checked." if signin_problem else
                    problem if send_kind else f"The last check didn't work: {problem[:240]}")
            problem_html = (f'<div class="callout">{esc(said)}'
                            f'<div class="row">{fix}<span class="say" id="problem-say"></span></div></div>')
        else:
            problem_html = ""
        allow = ('<p><button class="primary" data-action="/api/allow" data-out="allow-say">Allow transcript copying</button> '
                 '<span class="say" id="allow-say">Turn on python3.12 in the list that opens.</span></p>' + _python_fallback()
                 if mac() and cc.copy_transcripts and not copy["allowed"] else "")
        rows = _recent(cc)
        items = "".join(f'<li><span><span class="t">{esc(e.get("title", ""))}</span> <span class="s">{esc(e.get("date", ""))}</span></span>'
                        f'<span class="s">{esc(_describe(e))}</span></li>' for e in rows)
        recent = (f'<ul class="recent">{items}</ul>' if rows else
                  '<div class="empty"><strong>Nothing sent yet</strong>When a lecture finishes in Granola, '
                  "it's sent to your library and shows up here.</div>")
        ask = cc.mode == "ask"
        settings = (
            '<details class="more"><summary>Settings</summary><form data-action="/api/prefs" data-out="prefs-say">'
            f'<label class="choice"><input type="radio" name="mode" value="auto"{"" if ask else " checked"}><span>Send every lecture automatically</span></label>'
            f'<label class="choice"><input type="radio" name="mode" value="ask"{" checked" if ask else ""}><span>Ask me before sending each one</span></label>'
            + (f'<label class="choice"><input type="checkbox" name="copy_transcripts"{" checked" if cc.copy_transcripts else ""}>'
               '<span>Copy each transcript from the Granola app</span></label>' if mac() else "")
            + '<div class="row"><button>Save</button><span class="say" id="prefs-say"></span></div></form>'
            '<div class="row"><button data-action="/api/login" data-out="more-say">Sign in to Granola again</button>'
            '<button data-action="/api/reset-pool" data-out="more-say" data-confirm="Connect to a different library? Your lectures stay where they are.">'
            'Connect to a different library</button></div><p class="say" id="more-say"></p>'
            '<div class="row"><button class="danger" data-action="/api/remove" data-out="more-say" '
            'data-confirm="Stop granola-share and remove it from this computer? Nothing more is sent.">Stop and remove granola-share</button></div>'
            f'<p class="small muted">Version {__version__}. Your settings and copied transcripts are in {esc(str(home))}.</p></details>')
        body = (f'<header><h1>Granola Share</h1><p class="lede">Sending your lectures to {esc(cc.pool_name)}.</p></header>'
                f'<dl class="status">{facts_html}</dl>{problem_html}{allow}'
                '<div class="row"><button class="primary" data-action="/api/check" data-out="check-say">Check for new lectures now</button>'
                f'<a class="btn" href="{esc(cc.server_url)}" target="_blank" rel="noopener">Open your library</a>'
                '<span class="say" id="check-say"></span></div>'
                f'<h2>Recent lectures</h2>{recent}{settings}')
        return respond("Granola Share", body, watch=15)

    # -- actions
    async def body(request: Request) -> dict:
        try:
            data = await request.json()
            return data if isinstance(data, dict) else {}
        except Exception:
            return {}

    @app.post("/api/pool")
    async def set_pool(request: Request):
        require(request)
        from .wizard import _normalize_url

        data = await body(request)
        url, key = _normalize_url(str(data.get("server", ""))), str(data.get("key", "")).strip()
        try:
            info = check_server(url, key)
        except Exception as e:
            hint = (" Check the password from your Mac mini's setup." if "password" in str(e) else
                    " Is Tailscale on on both computers, and is the Mac mini awake?")
            raise HTTPException(400, f"Couldn't connect: {e}.{hint}")
        cc = runtime.config()
        cc.server_url, cc.pool_key, cc.pool_name = url, key, str(info.get("pool_name") or "your library")
        cc.display_name = cc.display_name or _default_name()
        save_client_config(cc)
        _clear_prefill(home)
        runtime.reload()
        return {"message": f"Connected to {cc.pool_name}. Anything waiting is being sent now."}

    @app.post("/api/login")
    def login(request: Request):
        require(request)
        runtime.start_login()
        return {"message": "A browser tab opened for Granola. Sign in there.", "reload": True, "delay": 300}

    @app.post("/api/prefs")
    async def prefs(request: Request):
        require(request)
        data = await body(request)
        cc = runtime.config()
        cc.display_name = str(data.get("display_name") or cc.display_name or _default_name()).strip()[:80]
        cc.mode = "auto" if data.get("mode") == "auto" else "ask"
        if "copy_transcripts" in data:
            cc.copy_transcripts = bool(data.get("copy_transcripts"))
        save_client_config(cc)
        runtime.reload()
        return {"message": "Saved."}

    @app.post("/api/allow")
    def allow(request: Request):
        require(request)
        runtime.request_permission()
        return {"message": "System Settings is open. Turn on python3.12, then come back.", "reload": False}

    @app.post("/api/finish")
    def finish(request: Request):
        require(request)
        if not runtime.configured():
            raise HTTPException(400, "Finish the steps above first.")
        cc = runtime.config()
        if not cc.display_name:
            cc.display_name = _default_name()
            save_client_config(cc)
        runtime.start_watching()
        runtime.check_now()
        return {"message": "All set. Checking Granola now…"}

    @app.post("/api/check")
    def check(request: Request):
        require(request)
        runtime.check_now()
        return {"message": "Checking Granola now.", "reload": True, "delay": 8000}

    @app.post("/api/reset-pool")
    def reset_pool(request: Request):
        require(request)
        cc = runtime.config()
        cc.server_url, cc.pool_key, cc.pool_name = "", "", ""
        save_client_config(cc)
        runtime.stop.set()  # the service restarts into setup
        threading.Timer(1.0, lambda: os._exit(0)).start()
        return {"message": "Restarting into setup…", "delay": 4000}

    @app.post("/api/remove")
    def remove(request: Request):
        require(request)
        from . import launcher

        launcher.uninstall()
        threading.Timer(1.0, lambda: autostart.uninstall("client")).start()  # this stops this very process
        return {"message": "Stopped and removed. You can close this tab.", "reload": False}

    @app.get("/api/state")
    def state(request: Request):
        if not authed(request):
            raise HTTPException(403, "not signed in")
        cc = runtime.config()
        recent = _recent(cc)
        return {"configured": runtime.configured(), "signed_in": runtime.signed_in(), "login": runtime.login,
                "problem": runtime.client.last_error if runtime.client is not None else None,
                "copy": runtime.copy_status(), "watching": runtime.watching, "pool_name": cc.pool_name,
                "recent_key": [(e.get("title"), e.get("decision"), e.get("filed")) for e in recent[:5]],
                "version": __version__}

    @app.get("/healthz")
    def healthz():
        return {"ok": True, "app": "granola-share", "version": __version__}

    return app


def python_path() -> str:
    """The Python binary macOS asks about ("python3.12"): what to add by hand if it isn't in the list."""
    import sys

    return os.path.realpath(sys.executable)


def _python_fallback() -> str:
    path = python_path()
    return ('<details class="small"><summary>Don\'t see python3.12 in the list?</summary>'
            '<p>Click <strong>+</strong> under the list, press <strong>⌘⇧G</strong>, paste this path, then click '
            f'<strong>Open</strong>:</p><pre class="code" id="py-path">{esc(path)}</pre>'
            '<button type="button" data-copy="py-path">Copy the path</button></details>')


def _default_name() -> str:
    try:
        return getpass.getuser()
    except Exception:
        return ""


# --- first run: the installer hands over the library's address -------------------------

def write_prefill(home: Path, server: str | None, key: str | None) -> None:
    """The install line's address and password, for the setup page to fill in (deleted once connected)."""
    if not server and not key:
        return
    path = home / "ui_prefill.json"
    home.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"server": server or "", "key": key or ""}))
    os.chmod(path, 0o600)


def _read_prefill(home: Path) -> dict:
    try:
        data = json.loads((home / "ui_prefill.json").read_text())
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def _clear_prefill(home: Path) -> None:
    try:
        (home / "ui_prefill.json").unlink()
    except OSError:
        pass


# --- running it ------------------------------------------------------------------------

def free_port(start: int = DEFAULT_PORT, tries: int = 10) -> int:
    for port in range(start, start + tries):
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            if os.name != "nt":  # like uvicorn: a restart isn't pushed off the port by its own closed
                # connections. (On Windows this flag would allow sharing a port someone is listening on.)
                s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                s.bind(("127.0.0.1", port))
                return port
            except OSError:
                continue
    return start


def serve(runtime: ClientRuntime, port: int | None = None) -> None:
    """Blocks: the page for setup and status. The watcher runs in background threads."""
    import uvicorn

    port = port or free_port()
    (runtime.home / "ui_port").write_text(str(port))
    runtime.log(f"[app] Granola Share page on http://127.0.0.1:{port}")
    uvicorn.run(create_client_app(runtime, port=port), host="127.0.0.1", port=port, log_level="warning")


def app_url(home: Path) -> str | None:
    try:
        port = int((home / "ui_port").read_text().strip())
    except (OSError, ValueError):
        return None
    return f"http://127.0.0.1:{port}/?t={_token(home)}"


def wait_for_app(home: Path, timeout: float = 20) -> str | None:
    """The page's URL once the background service answers, else None."""
    import httpx

    deadline = time.time() + timeout
    while time.time() < deadline:
        url = app_url(home)
        if url:
            try:
                r = httpx.get(url.split("/?")[0] + "/healthz", timeout=2)
                if r.status_code == 200 and r.json().get("app") == "granola-share":
                    return url
            except Exception:
                pass
        time.sleep(0.5)
    return None


def open_app(home: Path, install: bool = False, log=print) -> str | None:
    """`granola-share client open`: make sure the background service runs, then open its page."""
    from . import launcher

    if install:
        from .update import cleanup_legacy

        autostart.install("client", home)
        launcher.install(home)
        cleanup_legacy(home, log)  # 0.1 lived in <home>/venv and <home>/app
    elif autostart.status("client") == "missing":
        autostart.install("client", home)
    elif autostart.status("client") == "stopped":
        autostart.restart("client")
    url = wait_for_app(home)
    if not url:
        log(f"Granola Share didn't start. See {home / 'logs' / 'client.log'}, or run `granola-share doctor`.")
        return None
    dialogs.open_url(url)
    return url

