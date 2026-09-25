"""The Study Stash app on your laptop: a small web page on this computer for setup and status.

The background watcher (`granola-share client run`) serves it on http://127.0.0.1:<port>, so
setting up never needs the terminal: connect to your library, sign in to Granola, choose how to
send, allow transcript copying, done. Afterwards it shows what was sent and where it was filed.

Only this computer can reach it (it listens on 127.0.0.1 and checks the Host header), and it
needs a per-install token that the "Study Stash" launcher passes in, so websites and other
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
from urllib.parse import urlsplit

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
.solo{max-width:40rem;margin:0 auto;padding:2.6rem 1.1rem 5rem}
.solo>header{margin-bottom:1.6rem}
.stack{display:grid;gap:.55rem}
.stack input{width:100%}
.say{font-size:.8125rem;margin:.5rem 0 0;color:var(--label-2);min-height:1em}
.say:empty{display:none}
.say.good{color:var(--green)}
.say.bad{color:var(--red)}
.fields>p{margin:0 0 .6rem;max-width:60ch}
.fields>p:last-child{margin-bottom:0}
.step{margin:0 0 1.4rem}
.step-head{display:flex;align-items:center;gap:.65rem;margin:0 0 .55rem .2rem}
.step-head h2{margin:0;font-size:1.0625rem;font-weight:600;letter-spacing:-.01em}
.step-n{width:1.5rem;height:1.5rem;flex:none;border-radius:50%;display:grid;place-items:center;font:600 .8125rem/1 var(--font);
  color:var(--label-2);border:1.5px solid var(--label-3)}
.step.done .step-n{background:var(--green);border-color:var(--green);color:#fff}
.step.locked{opacity:.42;pointer-events:none}
.group>button.row{width:100%;border-radius:0;background:none;justify-content:flex-start;font:400 .9375rem/1.3 var(--font);
  color:var(--accent);text-align:left}
.group>button.row:hover{background:var(--fill-2)}
.group>button.row.danger{color:var(--red)}
details.help{margin-top:.8rem;font-size:.8125rem}
details.help summary{cursor:pointer;color:var(--accent)}
details.help p{margin:.5rem 0}
details.help .code{margin:.3rem 0 .5rem}
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
document.querySelectorAll('form[data-autosave]').forEach(function(f){
  f.addEventListener('change',function(){f.requestSubmit();});});
if(watch){var seen=null;setInterval(function(){fetch('/api/state',{headers:{'X-Granola-Share':'1'}}).then(function(r){return r.json();})
  .then(function(s){var key=JSON.stringify([s.signed_in,s.login.running,s.login.error,s.copy.allowed,s.watching,s.recent_key,s.problem,s.ready]);
    if(seen!==null&&key!==seen)location.reload();seen=key;});}, parseInt(watch,10)*1000);}
"""


def mac() -> bool:
    return platform.system() == "Darwin"


def windows() -> bool:
    return platform.system() == "Windows"


def native_app() -> Path | None:
    """The Study Stash app, when it's installed: the Mac app, or the Windows one."""
    from . import launcher

    return launcher.native_installed() if mac() else launcher.windows_app_exe() if windows() else None


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
        self._checked: tuple[float, dict] | None = None
        self.tailscale_job = {"running": False, "error": None}

    def config(self) -> ClientConfig:
        return load_client_config(self.home)

    # -- what this computer has: Granola (records the lectures) and Tailscale (reaches the library)
    def readiness(self, fresh: bool = False) -> dict:
        """ready.laptop_checks(), at most every few seconds (the setup page asks every two)."""
        if fresh or self._checked is None or time.time() - self._checked[0] > 4:
            from . import ready

            self._checked = (time.time(), ready.laptop_checks())
        return self._checked[1]

    def fix_tailscale(self) -> str:
        """Install Tailscale (its own installer opens) or, when it's installed, open it to sign in."""
        from . import hostinfo, ready

        if hostinfo.tailscale_exe():
            return ("Tailscale is open. Sign in with the same account as your library's computer."
                    if ready.open_tailscale() else "Open Tailscale from your apps and sign in.")
        if self.tailscale_job["running"]:
            return "Tailscale is downloading."
        self.tailscale_job = {"running": True, "error": None}

        def go():
            try:
                ok = ready.install_tailscale(self.log, None, lambda _: None, remote=False)
                self.tailscale_job = {"running": False, "error": None if ok else "it isn't installed yet"}
            except Exception as e:  # noqa: BLE001
                self.tailscale_job = {"running": False, "error": str(e)}
            self._checked = None

        threading.Thread(target=go, name="tailscale-install", daemon=True).start()
        return "Downloading Tailscale. Its installer opens in a moment: click through it."

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
        return dialogs.ask_choice("Study Stash", text, ["Not now", "Save"], default="Save") == "Save"

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


def _ready_key(checks: dict, job: dict) -> list:
    from . import hostinfo

    return [bool(checks["granola"]), hostinfo.tailscale_problem(checks["tailscale"]), job.get("running")]


def _this_computer(checks: dict, job: dict) -> str:
    """What this laptop needs besides Study Stash: Granola, which records the lectures, and Tailscale, which
    reaches the library from anywhere. Each with its fix."""
    from . import hostinfo, ready

    rows, fixes = [], []
    if checks["granola_here"]:
        ok = bool(checks["granola"])
        rows.append(("Granola", "Records your lectures", "installed" if ok else "not installed", ok))
        if not ok:
            fixes.append("<p>Granola isn’t on this computer yet. It’s the app you record your lectures in.</p>"
                         f'<div class="toolbar"><a class="btn primary" href="{ready.GRANOLA_DOWNLOAD}" target="_blank" '
                         'rel="noopener">Get Granola</a></div>')
    ts = checks["tailscale"]
    problem = hostinfo.tailscale_problem(ts)
    rows.append(("Tailscale", "Reaches your library from anywhere", problem or "connected", not problem))
    if problem:
        if ts.get("installed"):
            text, label = ("Open Tailscale and sign in with the same account as your library’s computer.", "Open Tailscale")
        else:
            text, label = ("Without Tailscale, this computer reaches your library only on the same Wi-Fi.",
                           "Install Tailscale")
        busy = " disabled" if job.get("running") else ""
        fixes.append(f'<p>{text}</p><div class="toolbar"><button class="primary" data-action="/api/tailscale" '
                     f'data-out="ts-say" data-busy="Working…"{busy}>{label}</button></div>'
                     f'<p class="say{" bad" if job.get("error") else ""}" id="ts-say">'
                     f'{esc("Tailscale didn’t install: " + job["error"]) if job.get("error") else ""}</p>')
    # Without Granola there's nothing to record with (red); without Tailscale, only home Wi-Fi works (orange).
    items = "".join(
        f'<div class="row"><span class="grow">{esc(name)}<span class="subtitle">{esc(what)}</span></span>'
        f'<span class="value{" bad" if not ok and name == "Granola" else ""}">{esc(state)}</span>'
        f'<span class="dot" style="--tag:{"var(--green)" if ok else "var(--red)" if name == "Granola" else "var(--orange)"}">'
        '</span></div>' for name, what, state, ok in rows)
    notice = f'<div class="notice" style="margin-top:.75rem"><div>{"".join(fixes)}</div></div>' if fixes else ""
    return f'<div class="group-head">This computer</div><div class="group">{items}</div>{notice}'


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


def _state_color(e: dict) -> str:
    d = e.get("decision")
    if d == "shared":
        return "var(--green)" if e.get("filed") is not False else "var(--accent)"
    return "var(--orange)" if d == "pending" else "var(--gray)"


def create_client_app(runtime: ClientRuntime, *, port: int = DEFAULT_PORT, check_server=None,
                      extra_hosts: tuple[str, ...] = ()) -> FastAPI:
    from .client import check_server as _check_server

    check_server = check_server or _check_server
    home = runtime.home
    token = _token(home)
    app = FastAPI(title="Study Stash", docs_url=None, redoc_url=None, openapi_url=None)
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
            raise HTTPException(403, "Open Study Stash from its app icon.")

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
            return respond("Study Stash", "<header><h1>Study Stash</h1></header><p class=sub>Open "
                           "<strong>Study Stash</strong> from your " + ("Applications folder" if mac() else "Start Menu")
                           + " to see this page.</p>")
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
            return (f'<section class="{cls}"><div class="step-head"><span class="step-n">{mark}</span>'
                    f'<h2>{esc(title)}</h2></div><div class="group"><div class="fields">{inner}</div></div></section>')

        pool_inner = (
            f'<p>The address and password from your library\'s setup (also under Settings in its web page).</p>'
            f'<form class="stack" data-action="/api/pool" data-out="pool-say" data-busy="Connecting…">'
            f'<input type="url" name="server" value="{esc(server)}" placeholder="http://mac-mini:8787" aria-label="Library address" required>'
            f'<input type="password" name="key" value="{esc(key)}" placeholder="Password" aria-label="Password">'
            f'<div class="actions" style="margin:0"><button class="{"" if connected else "primary"}">'
            f'{"Reconnect" if connected else "Connect"}</button></div></form>'
            + (f'<p class="say good" id="pool-say">Connected to {esc(cc.pool_name)}.</p>' if connected
               else '<p class="say" id="pool-say"></p>'))
        if signed:
            granola_inner = '<p class="say good" style="margin:0">Signed in to Granola.</p>'
        elif login["running"]:
            granola_inner = ('<p>A browser tab opened for Granola. Sign in there, then come back to this tab.</p>'
                             '<p class="say"><span class="spin" style="display:inline-block;vertical-align:-2px;'
                             'margin-right:.4rem"></span>Waiting for you to finish signing in…</p>')
        else:
            err = f'<p class="say bad">{esc(login["error"])}. Try again.</p>' if login["error"] else ""
            granola_inner = ('<p>Sign in with your Granola account, the one you record lectures with.</p>'
                             f'{err}<div class="actions" style="margin:0"><button class="primary" data-action="/api/login" '
                             'data-out="login-say" data-busy="Opening Granola…">Sign in to Granola</button></div>'
                             '<p class="say" id="login-say"></p>')
        ask = cc.mode == "ask"
        prefs_inner = (
            '<form data-action="/api/prefs" data-out="prefs-say" data-autosave><div class="group" style="margin:-.8rem -1rem">'
            f'<label class="row pick"><input type="radio" name="mode" value="auto"{"" if ask else " checked"}>'
            '<span class="grow">Send every lecture automatically</span><span class="tick"></span></label>'
            f'<label class="row pick"><input type="radio" name="mode" value="ask"{" checked" if ask else ""}>'
            '<span class="grow">Ask me before sending each one</span><span class="tick"></span></label>'
            + (f'<label class="row"><span class="grow">Copy each transcript from the Granola app<span class="subtitle">Presses Granola’s Copy transcript for you when a lecture ends. This automates the Granola app, which may go against Granola’s terms of service, so it’s off unless you turn it on.</span></span>'
               f'<input class="switch" type="checkbox" name="copy_transcripts"{" checked" if cc.copy_transcripts else ""}></label>'
               if mac() else "")
            + '</div></form><p class="say" id="prefs-say" style="margin-top:1.3rem"></p>')
        steps = [step(1, "Connect to your library", connected, False, pool_inner),
                 step(2, "Sign in to Granola", signed, not connected, granola_inner),
                 step(3, "How to send", connected and signed, not signed, prefs_inner)]
        n = 4
        if mac() and cc.copy_transcripts and copy["available"]:
            if copy["allowed"]:
                allow_inner = '<p class="say good" style="margin:0">Allowed. Transcripts are copied while Granola is in front.</p>'
            else:
                allow_inner = (
                    "<p>To copy transcripts, Study Stash needs to read the Granola window. Click the button, "
                    "then turn on <strong>python3.12</strong> in the list that opens.</p>"
                    '<div class="actions" style="margin:0"><button class="primary" data-action="/api/allow" data-out="allow-say" '
                    'data-busy="Opening System Settings…">Open Accessibility settings</button></div>'
                    '<p class="say" id="allow-say"></p>' + _python_fallback())
            steps.append(step(n, "Allow transcript copying", copy["allowed"], not signed, allow_inner))
            n += 1
        ready = connected and signed
        finish_inner = ("<p>Study Stash keeps running in the background and starts when you log in. "
                        "When a lecture finishes in Granola, it's sent to your library.</p>"
                        f'<div class="actions" style="margin:0"><button class="primary" data-action="/api/finish" '
                        f'data-out="finish-say"{"" if ready else " disabled"}>Finish setup</button></div>'
                        '<p class="say" id="finish-say"></p>')
        steps.append(step(n, "Start sending", False, not ready, finish_inner))
        body = (f'<header><h1>Set up Study Stash</h1><p class="sub">Send your Granola lectures to your library, on your '
                f'own computer, where your own model writes their notes. {n} short steps.</p></header>'
                f'{_this_computer(runtime.readiness(), runtime.tailscale_job)}{"".join(steps)}')
        return respond("Set up Study Stash", body, watch=2)

    # -- status
    def status_page() -> HTMLResponse:
        cc = runtime.config()
        copy = runtime.copy_status()
        facts = [("Library", cc.pool_name or "not connected", False), ("Granola", "signed in" if runtime.signed_in() else "signed out", not runtime.signed_in()),
                 ("Watching", "every few minutes" if runtime.watching else "stopped", not runtime.watching)]
        if mac() and cc.copy_transcripts:
            facts.append(("Transcripts", "copied from Granola" if copy["allowed"] else "needs permission", not copy["allowed"]))
        checks = runtime.readiness()
        if checks["granola_here"] and not checks["granola"]:
            facts.append(("Granola app", "not installed on this computer", True))
        problem = runtime.client.last_error if runtime.client is not None else None
        signin_problem = bool(problem and "granola-share login" in problem)  # every sign-in failure says this
        send_kind = getattr(runtime.client, "send_problem_kind", None) if problem else None
        if signin_problem:
            facts[1] = ("Granola", "needs you to sign in again", True)
        elif send_kind == "password":
            facts[0] = ("Library", "needs its new password", True)
        elif send_kind == "unreachable":
            facts[0] = ("Library", "can't be reached", True)
        facts_html = "".join(
            f'<div class="row"><span class="grow">{esc(k)}</span><span class="value{" bad" if bad else ""}">{esc(v)}</span>'
            f'<span class="dot" style="--tag:{"var(--red)" if bad else "var(--green)"}"></span></div>'
            for k, v, bad in facts)
        if problem and send_kind == "password":
            problem_html = (
                f'<div class="notice bad"><div>{esc(problem)}'
                '<form class="stack" data-action="/api/pool" data-out="problem-say" data-busy="Connecting…">'
                f'<input type="hidden" name="server" value="{esc(cc.server_url)}">'
                '<input type="password" name="key" placeholder="The new password" aria-label="Library password" required>'
                '<div class="actions" style="margin:0"><button class="primary">Reconnect</button></div></form>'
                '<p class="say" id="problem-say"></p>'
                '<p class="small muted" style="margin:.5rem 0 0">It\'s shown on your library\'s computer, in the library\'s '
                'Settings under Connect your laptop.</p></div></div>')
        elif problem:
            fix = ('<div class="toolbar"><button class="primary" data-action="/api/login" data-out="problem-say">'
                   'Sign in to Granola again</button></div>' if signin_problem else "")
            said = ("Granola signed you out, so new lectures can't be checked." if signin_problem else
                    problem if send_kind else f"The last check didn't work: {problem[:240]}")
            problem_html = (f'<div class="notice bad"><div>{esc(said)}{fix}<p class="say" id="problem-say"></p></div></div>')
        else:
            problem_html = ""
        allow = ('<div class="notice"><div>Transcripts can\'t be copied until you allow it. Click the button, then turn on '
                 '<strong>python3.12</strong> in the list that opens.<div class="toolbar"><button class="primary" '
                 'data-action="/api/allow" data-out="allow-say">Allow transcript copying</button></div>'
                 '<p class="say" id="allow-say"></p>' + _python_fallback() + '</div></div>'
                 if mac() and cc.copy_transcripts and not copy["allowed"] else "")
        rows = _recent(cc)
        items = "".join(
            f'<div class="row"><div class="grow"><div class="title">{esc(e.get("title", ""))}</div>'
            f'<div class="subtitle"><span>{esc(ui.short_date(e.get("date")))}</span><span>{esc(_describe(e))}</span></div></div>'
            f'<span class="dot" style="--tag:{_state_color(e)}"></span></div>' for e in rows)
        recent = (f'<div class="group">{items}</div>' if rows else
                  '<div class="empty"><strong>Nothing sent yet</strong>When a lecture finishes in Granola, '
                  "it's sent to your library and shows up here.</div>")
        ask = cc.mode == "ask"
        settings = (
            '<div class="group-head">Sending</div>'
            '<form data-action="/api/prefs" data-out="prefs-say" data-autosave><div class="group">'
            f'<label class="row pick"><input type="radio" name="mode" value="auto"{"" if ask else " checked"}>'
            '<span class="grow">Send every lecture automatically</span><span class="tick"></span></label>'
            f'<label class="row pick"><input type="radio" name="mode" value="ask"{" checked" if ask else ""}>'
            '<span class="grow">Ask me before sending each one</span><span class="tick"></span></label>'
            + (f'<label class="row"><span class="grow">Copy each transcript from the Granola app<span class="subtitle">Presses Granola’s Copy transcript for you when a lecture ends. This automates the Granola app, which may go against Granola’s terms of service, so it’s off unless you turn it on.</span></span>'
               f'<input class="switch" type="checkbox" name="copy_transcripts"{" checked" if cc.copy_transcripts else ""}></label>'
               if mac() else "")
            + '</div></form><p class="say" id="prefs-say"></p>'
            '<div class="group-head">Account</div><div class="group">'
            '<button class="row" data-action="/api/login" data-out="more-say">Sign in to Granola again</button>'
            '<button class="row" data-action="/api/reset-pool" data-out="more-say" '
            'data-confirm="Connect to a different library? Your lectures stay where they are.">Connect to a different library</button>'
            '<button class="row danger" data-action="/api/remove" data-out="more-say" '
            'data-confirm="Stop Study Stash and remove it from this computer? Nothing more is sent.">'
            'Stop and remove Study Stash</button></div><p class="say" id="more-say"></p>'
            f'<p class="group-foot">Version {__version__}. Your settings and copied transcripts are in {esc(str(home))}.</p>'
            '<p class="group-foot">Study Stash is an independent project, not affiliated with or endorsed by Granola. Granola is a trademark of its owner.</p>')
        body = (f'<header><h1>Study Stash</h1><p class="sub">Sending your lectures to {esc(cc.pool_name)}.</p></header>'
                f'{problem_html}{allow}<div class="group">{facts_html}</div>'
                '<div class="toolbar" style="margin-top:1rem"><button class="primary" data-action="/api/check" '
                'data-out="check-say">Check for new lectures now</button>'
                # The Mac app signs in to the library itself; in a browser, /library does it.
                f'<a class="btn" href="{esc(cc.server_url if mac() else "/library")}" target="_blank" rel="noopener">'
                'Open your library</a></div>'
                '<p class="say" id="check-say"></p>'
                f'<h2>Recent lectures</h2>{recent}{settings}')
        return respond("Study Stash", body, watch=15)

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
            hint = (" Check the password from your library's setup." if "password" in str(e) else
                    " Is Tailscale on on both computers, and is the library's computer awake?")
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

    @app.post("/api/tailscale")
    def tailscale(request: Request):
        require(request)
        return {"message": runtime.fix_tailscale(), "reload": False}

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
                "ready": _ready_key(runtime.readiness(), runtime.tailscale_job), "version": __version__}

    @app.get("/library", response_class=HTMLResponse)
    def open_library(request: Request):
        """"Open your library", already signed in: sends the password this laptop has to the library's
        login form, as if you'd typed it. The strict cookie keeps other sites from opening this."""
        cc = runtime.config()
        if not authed(request) or not cc.server_url:
            return RedirectResponse("/", status_code=303)
        target = urlsplit(cc.server_url)
        origin = f"{target.scheme}://{target.netloc}"
        nonce = secrets.token_urlsafe(16)
        body = (f'<form id="go" method="post" action="{esc(origin)}/login"><input type="hidden" name="password" '
                f'value="{esc(cc.pool_key)}"><input type="hidden" name="next" value="/">'
                f'<p class="sub">Opening {esc(cc.pool_name or "your library")}…</p>'
                '<noscript><button class="primary">Open your library</button></noscript></form>'
                f'<script nonce="{nonce}">document.getElementById("go").submit()</script>')
        page = ui.head("Opening your library", nonce) + f'<body><div class="solo">{body}</div></body></html>'
        csp = (f"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; img-src 'self'; "
               f"form-action {origin}; frame-ancestors 'none'; base-uri 'none'")
        return HTMLResponse(page, headers={"Content-Security-Policy": csp, "Cache-Control": "no-store",
                                           "Referrer-Policy": "no-referrer"})

    @app.get("/healthz")
    def healthz():
        return {"ok": True, "app": "granola-share", "version": __version__}

    ui.add_icon_routes(app)
    return app


def python_path() -> str:
    """The Python binary macOS asks about ("python3.12"): what to add by hand if it isn't in the list."""
    import sys

    return os.path.realpath(sys.executable)


def _python_fallback() -> str:
    path = python_path()
    return ('<details class="help"><summary>Don\'t see python3.12 in the list?</summary>'
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
    runtime.log(f"[app] Study Stash page on http://127.0.0.1:{port}")
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


def open_app(home: Path, install: bool = False, log=print, browser: bool = True) -> str | None:
    """`granola-share client open`: make sure the background service runs, then show its page, in the
    Study Stash app when it's installed (macOS), else in the browser. `browser=False` only starts it:
    that's what the app itself runs."""
    from . import launcher

    if install:
        from .update import cleanup_legacy

        if (mac() or windows()) and native_app() is None:
            _install_native_app(log)
        autostart.install("client", home)
        launcher.install(home)
        cleanup_legacy(home, log)  # 0.1 lived in <home>/venv and <home>/app
    elif autostart.status("client") == "missing":
        autostart.install("client", home)
    elif autostart.status("client") == "stopped":
        autostart.restart("client")
    url = wait_for_app(home)
    if not url:
        log(f"Study Stash didn't start. See {home / 'logs' / 'client.log'}, or run `granola-share doctor`.")
        return None
    if browser:
        native = native_app()
        if native is None or not dialogs.open_app(str(native)):
            dialogs.open_window(url)  # no app yet (or Linux): its own browser window, with the Study Stash icon
    return url


def _install_native_app(log=print) -> None:
    """The first install on a Mac or PC also gets the Study Stash app from the newest release, if it has one."""
    from . import launcher, update

    try:
        rel = update.latest_release()
    except Exception:
        return
    if rel and mac() and rel.mac_app:
        launcher.install_native(rel.mac_app, log=log)
    elif rel and windows() and rel.windows_app:
        launcher.install_windows_app(rel.windows_app, log=log)