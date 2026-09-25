"""The library's setup as a page, so nobody needs a terminal: the same six steps as `granola-share setup`,
done with buttons and fields. The Study Stash Library app (Mac and Windows) shows it.

`granola-share setup --page` serves it on 127.0.0.1 (its port in <home>/setup_port, with the same token
as the laptop's page) until setup is finished. Answers are kept in setup_draft.json as you go, and
config.toml is written only when you finish: that's how the app knows the library exists.
"""

from __future__ import annotations

import hmac
import json
import platform
import re
import secrets
import subprocess
import threading
import time
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse

from . import __version__, autostart, dialogs, hostinfo, ollama, ready, ui
from .client_app import APP_CSS, APP_JS, COOKIE, CSP, _token, free_port
from .config import ClassDef, load_config, save_config
from .ui import esc

SETUP_PORT = 8764
RELEASES = "https://github.com/Joseph-Rus/study-stash/releases/latest/download"
SETUP_CSS = r"""
.field{display:grid;gap:.25rem;font-size:.8125rem;color:var(--label-2)}
.field input,.field select{font-size:.9375rem;color:var(--label)}
.two{display:grid;grid-template-columns:1fr 1fr;gap:.55rem}
@media (max-width:520px){.two{grid-template-columns:1fr}}
progress{width:100%;height:.45rem;margin:.6rem 0 0;accent-color:var(--accent)}
progress:not([value]){display:none}
.addr{font:500 .9375rem/1.4 ui-monospace,Menlo,Consolas,monospace;user-select:all}
"""
# Progress bars and notes of running jobs update in place; the page reloads when a step changes.
SETUP_JS = r"""
(function(){var seen=null;function tick(){fetch('/api/state',{headers:{'X-Granola-Share':'1'}}).then(function(r){return r.json();})
.then(function(s){Object.keys(s.jobs).forEach(function(k){var j=s.jobs[k];
  var bar=document.querySelector('progress[data-job="'+k+'"]');
  if(bar){if(j.running&&j.total){bar.max=j.total;bar.value=j.done;}else{bar.removeAttribute('value');}}
  var note=document.querySelector('[data-job-note="'+k+'"]');
  if(note&&j.note){note.textContent=j.note;note.className='say '+(j.error?'bad':j.running?'wait':'good');}});
  if(seen!==null&&s.key!==seen)location.reload();seen=s.key;}).catch(function(){});}
tick();setInterval(tick,1500);})();
"""


class Jobs:
    """Slow things (downloads, installers, the model's first answer) run in the background; the page shows
    their progress and notes."""

    def __init__(self):
        self.state: dict[str, dict] = {}

    def running(self, name: str) -> bool:
        return bool(self.state.get(name, {}).get("running"))

    def start(self, name: str, work, after=None) -> bool:
        if self.running(name):
            return False
        job = {"running": True, "done": 0, "total": 0, "note": "", "error": False}
        self.state[name] = job

        def progress(done: int, total: int) -> None:
            job["done"], job["total"] = done, total

        def note(text: str) -> None:
            job["note"] = str(text).strip()

        def run() -> None:
            try:
                said = work(progress, note)
                if said:
                    job["note"] = said
            except Exception as e:  # noqa: BLE001  shown on the page
                job["note"], job["error"] = f"{e}", True
            finally:
                job["running"] = False
                if after:
                    after()

        threading.Thread(target=run, name=f"setup-{name}", daemon=True).start()
        return True

    def view(self) -> dict:
        return {k: dict(v) for k, v in self.state.items()}


class LibrarySetup:
    """The answers so far, what this computer has, and the steps that act on it. Every check and install
    is injectable, for tests."""

    def __init__(self, home: Path, *, list_models=ollama.list_models, ollama_installed=ollama.installed,
                 start_ollama=ollama.start, install_ollama=ready.install_ollama, pull_model=ollama.pull,
                 try_model=ollama.try_model, tailscale=hostinfo.tailscale_info,
                 install_tailscale=ready.install_tailscale, open_tailscale=ready.open_tailscale,
                 firewall=ready.firewall_open, open_firewall=ready.open_firewall, sleep_minutes=ready.sleep_minutes,
                 keep_awake=ready.keep_awake, ram_gb=ollama.total_ram_gb, disk_free=ready.disk_free_gb,
                 port_status=hostinfo.port_status, install_autostart=autostart.install, wait_healthy=None,
                 system: str | None = None):
        self.home = Path(home)
        self.system = system or platform.system()
        self.list_models, self.ollama_installed, self.start_ollama = list_models, ollama_installed, start_ollama
        self.install_ollama, self.pull_model, self.try_model = install_ollama, pull_model, try_model
        self.tailscale, self.install_tailscale, self.open_tailscale = tailscale, install_tailscale, open_tailscale
        self.firewall, self.open_firewall = firewall, open_firewall
        self.sleep_minutes, self.keep_awake = sleep_minutes, keep_awake
        self.ram_gb, self.disk_free, self.port_status = ram_gb, disk_free, port_status
        self.install_autostart = install_autostart
        if wait_healthy is None:
            from .wizard import wait_for_server as wait_healthy
        self.wait_healthy = wait_healthy
        self.jobs = Jobs()
        self.cfg = load_config(self.home)
        self.fresh = not self.cfg.config_path.exists()
        self.draft = {"library": not self.fresh, "models": not self.fresh, "autostart": True, "finished": False}
        self._load_draft()
        if not self.cfg.pool_password:
            self.cfg.pool_password = secrets.token_urlsafe(9)
        self._checked: tuple[float, dict] | None = None
        self.last_seen = time.time()

    # -- the draft ------------------------------------------------------------------------
    @property
    def draft_path(self) -> Path:
        return self.home / "setup_draft.json"

    def _load_draft(self) -> None:
        try:
            data = json.loads(self.draft_path.read_text())
        except (OSError, ValueError):
            return
        for key in ("pool_name", "pool_password", "web_port", "summary_model", "ollama_model", "ollama_enabled",
                    "auto_update"):
            if key in data:
                setattr(self.cfg, key, data[key])
        if data.get("pool_dir"):
            self.cfg.pool_dir = Path(data["pool_dir"])
        if "classes" in data:
            self.cfg.classes = [ClassDef(c["name"], c.get("aliases", []), c.get("description", ""))
                                for c in data["classes"]]
        self.draft.update({k: data[k] for k in ("library", "models", "autostart") if k in data})

    def _save_draft(self) -> None:
        c = self.cfg
        data = {"pool_name": c.pool_name, "pool_password": c.pool_password, "pool_dir": str(c.pool_dir),
                "web_port": c.web_port, "summary_model": c.summary_model, "ollama_model": c.ollama_model,
                "ollama_enabled": c.ollama_enabled, "auto_update": c.auto_update,
                "classes": [{"name": k.name, "aliases": k.aliases, "description": k.description} for k in c.classes],
                **{k: self.draft[k] for k in ("library", "models", "autostart")}}
        self.home.mkdir(parents=True, exist_ok=True)
        self.draft_path.write_text(json.dumps(data, indent=1))
        self._checked = None

    # -- what this computer has ---------------------------------------------------------------
    def checks(self, fresh: bool = False) -> dict:
        """Asked every second or two by the page, so at most every few seconds really."""
        if fresh or self._checked is None or time.time() - self._checked[0] > 4:
            models = self.list_models(self.cfg.ollama_host)
            self._checked = (time.time(), {
                "ram": self.ram_gb(), "disk": self.disk_free(), "tailscale": self.tailscale(),
                "models": models, "ollama_installed": models is not None or self.ollama_installed()})
        return self._checked[1]

    def names(self) -> list[str]:
        return [m["name"] for m in self.checks()["models"] or []]

    def model_ready(self, model: str) -> bool:
        return ollama.has_model(self.names(), model)

    # -- step 1: Tailscale and Ollama ---------------------------------------------------------
    def fix_tailscale(self) -> str:
        ts = self.checks(fresh=True)["tailscale"]
        if ts.get("running"):
            return "Tailscale is connected."
        if not ts.get("installed"):
            def install(progress, note):
                note("Downloading Tailscale...")
                ok = self.install_tailscale(note, progress, lambda _: None, system=self.system, remote=False)
                return ("Tailscale's installer is open: click through it, then sign in with the same account as your "
                        "laptop." if ok or self.system in ("Darwin", "Windows") else "Tailscale didn't install.")
            self.jobs.start("tailscale", install, after=lambda: setattr(self, "_checked", None))
            return "Downloading Tailscale."
        if self.open_tailscale(self.system):
            return "Tailscale is open: sign in there, with the same account as your laptop."
        return self._connect_tailscale(ts)

    def _connect_tailscale(self, ts: dict) -> str:
        """Where there's no Tailscale app to open, `tailscale up` prints a sign-in link: open it."""
        exe = ts.get("exe") or hostinfo.tailscale_exe()
        if not exe:
            return "Open Tailscale and sign in."

        def connect(progress, note):
            p = subprocess.Popen([exe, "up"], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            for line in p.stdout or []:
                link = re.search(r"https://login\.tailscale\.com/\S+", line)
                if link:
                    dialogs.open_url(link.group(0))
                    note(f"Sign in to Tailscale in the browser tab that opened: {link.group(0)}")
            p.wait(timeout=600)
            return "Tailscale is connected." if p.returncode == 0 else "Tailscale didn't connect."

        self.jobs.start("tailscale", connect, after=lambda: setattr(self, "_checked", None))
        return "Connecting Tailscale..."

    def fix_ollama(self) -> str:
        c = self.checks(fresh=True)
        if c["models"] is not None:
            return "Ollama is running."

        def work(progress, note):
            if not self.ollama_installed():
                note("Downloading Ollama...")
                if not self.install_ollama(note, progress, system=self.system):
                    return "Ollama didn't install. Get it from https://ollama.com, then come back."
            note("Starting Ollama...")
            return "Ollama is running." if self.start_ollama(self.cfg.ollama_host) else \
                "Ollama is installed but didn't start. Open it from your apps."

        self.jobs.start("ollama", work, after=lambda: setattr(self, "_checked", None))
        return "Working on it."

    # -- step 2: the library ------------------------------------------------------------------
    def set_library(self, data: dict) -> str:
        name = str(data.get("name") or "").strip() or "Lecture notes"
        password = str(data.get("password") or "").strip()
        if len(password) < 4:
            raise ValueError("Use a password of at least 4 characters.")
        folder = Path(str(data.get("folder") or self.cfg.pool_dir)).expanduser()
        try:
            folder.mkdir(parents=True, exist_ok=True)
            (folder / ".write-test").write_text("ok")
            (folder / ".write-test").unlink()
        except OSError as e:
            raise ValueError(f"Can't write to {folder}: {e}") from None
        port = str(data.get("port") or self.cfg.web_port).strip()
        if not port.isdigit() or not 1024 <= int(port) <= 65535:
            raise ValueError("The port is a number from 1024 to 65535.")
        if int(port) != self.cfg.web_port or self.fresh:
            if self.port_status(int(port)) == "busy":
                raise ValueError(f"Another app is using port {port}. Try {int(port) + 1}.")
        self.cfg.pool_name, self.cfg.pool_password, self.cfg.pool_dir, self.cfg.web_port = name, password, folder, int(port)
        self.draft["library"] = True
        self._save_draft()
        return "Saved."

    # -- step 3: classes ----------------------------------------------------------------------
    def change_classes(self, data: dict) -> str:
        if "remove" in data:
            i = int(data["remove"])
            if 0 <= i < len(self.cfg.classes):
                gone = self.cfg.classes.pop(i)
                self._save_draft()
                return f"Removed {gone.name}."
            return "Already removed."
        name = str(data.get("name") or "").strip()
        if not name:
            raise ValueError("Give the class a name, like CS 101.")
        if name.lower() in (c.name.lower() for c in self.cfg.classes):
            raise ValueError(f"{name} is already there.")
        aliases = [a.strip() for a in str(data.get("aliases") or "").split(",") if a.strip()]
        self.cfg.classes.append(ClassDef(name, aliases, str(data.get("description") or "").strip()))
        self._save_draft()
        return f"Added {name}."

    # -- step 4: the models -------------------------------------------------------------------
    def set_models(self, data: dict) -> str:
        summary = str(data.get("summary") or "").strip()
        sort = str(data.get("sort") or "").strip() or summary
        if not summary:
            raise ValueError("Pick a model.")
        self.cfg.summary_model, self.cfg.ollama_model, self.cfg.ollama_enabled = summary, sort, True
        self.draft["models"] = True
        self._save_draft()
        missing = [m for m in dict.fromkeys([summary, sort]) if not self.model_ready(m)]
        if missing:
            self.download(missing[0])
            return f"Downloading {missing[0]}."
        self.try_it()
        return "Saved. Trying it once..."

    def download(self, model: str) -> bool:
        def work(progress, note):
            note(f"Downloading {model}...")
            ok, why = self.pull_model(model, self.cfg.ollama_host, progress)
            if not ok:
                hint = " Update Ollama, then try again." if "newer version" in why.lower() else ""
                raise RuntimeError(f"The download failed: {why}.{hint}")
            return f"Downloaded {model}."

        def after():
            self._checked = None
            missing = [m for m in dict.fromkeys([self.cfg.effective_summary_model, self.cfg.ollama_model])
                       if not self.model_ready(m)]
            if missing and not self.jobs.state["pull"].get("error"):
                self.download(missing[0])
            elif not missing:
                self.try_it()

        return self.jobs.start("pull", work, after=after)

    def try_it(self) -> bool:
        model = self.cfg.effective_summary_model

        def work(progress, note):
            note(f"Trying {model} once (the first load can take a minute)...")
            secs, why = self.try_model(self.cfg.ollama_host, model)
            if secs is None:
                raise RuntimeError(f"{model} didn't answer: {why}. Pick a smaller model if this keeps happening.")
            took = "under a second" if secs < 1 else f"{secs:.0f} s"
            return f"{model} answered in {took}. It's ready to write study notes."

        return self.jobs.start("try", work)

    def without_ai(self) -> str:
        self.cfg.ollama_enabled = False
        self.draft["models"] = True
        self._save_draft()
        return "The library will sort by Granola folder and title only, and keep Granola's notes."

    # -- step 5: keeping it running --------------------------------------------------------------
    def set_prefs(self, data: dict) -> str:
        self.draft["autostart"] = bool(data.get("autostart"))
        self.cfg.auto_update = bool(data.get("auto_update"))
        self._save_draft()
        return "Saved."

    def fix_firewall(self) -> str:
        def work(progress, note):
            note("Windows asks for permission: click Yes.")
            if not self.open_firewall(self.cfg.web_port):
                raise RuntimeError("The firewall didn't change. Click the button again, and Yes when Windows asks.")
            return f"Your laptop can reach port {self.cfg.web_port} over Tailscale and this network."

        self.jobs.start("firewall", work)
        return "Asking Windows..."

    def stay_awake(self) -> str:
        if self.keep_awake(self.system):
            return "Done: this PC stays awake while it's plugged in."
        raise ValueError(f"That didn't take. {ready.SLEEP_FIX.get(self.system, '')}.")

    # -- step 6: finishing -----------------------------------------------------------------------
    def finish(self) -> str:
        if not self.draft["library"]:
            raise ValueError("Save your library's name and password first.")

        def work(progress, note):
            note("Saving your library...")
            save_config(self.cfg)
            if self.draft["autostart"]:
                note("Starting it, and setting it to start when you log in...")
                self.install_autostart("server", self.home)
                if not self.wait_healthy(self.cfg):
                    raise RuntimeError(f"Your library is saved, but it hasn't answered yet. Its log is "
                                       f"{self.cfg.log_dir / 'server.log'}.")
            self.draft["finished"] = True
            self.draft_path.unlink(missing_ok=True)
            return "Your library is ready."

        self.jobs.start("finish", work, after=lambda: setattr(self, "_checked", None))
        return "Finishing..."

    def connect_info(self) -> dict:
        urls = hostinfo.server_urls(self.cfg.web_port, self.checks()["tailscale"])
        return {"urls": urls, "password": self.cfg.pool_password, "name": self.cfg.pool_name,
                "commands": hostinfo.invite_commands(urls[0], self.cfg.pool_password)}

    def key(self) -> list:
        """Changes whenever a step's look changes, so the page knows to reload."""
        c = self.checks()
        ts = hostinfo.tailscale_problem(c["tailscale"])
        return [ts, c["models"] is not None, c["ollama_installed"], sorted(self.names()), dict(self.draft),
                len(self.cfg.classes), {k: v["running"] for k, v in self.jobs.state.items()}]


# --- the page ---------------------------------------------------------------------------

def _step(n: int, title: str, done: bool, locked: bool, inner: str) -> str:
    cls = "step" + (" done" if done else "") + (" locked" if locked else "")
    return (f'<section class="{cls}"><div class="step-head"><span class="step-n">{"✓" if done else n}</span>'
            f'<h2>{esc(title)}</h2></div><div class="group"><div class="fields">{inner}</div></div></section>')


def _job(name: str, jobs: dict) -> str:
    j = jobs.get(name, {})
    kind = "bad" if j.get("error") else "wait" if j.get("running") else "good"
    return (f'<progress data-job="{name}"></progress>'
            f'<p class="say {kind if j.get("note") else ""}" data-job-note="{name}">{esc(j.get("note", ""))}</p>')


def _row(name: str, what: str, state: str, ok: bool, color: str | None = None) -> str:
    dot = "var(--green)" if ok else (color or "var(--orange)")
    return (f'<div class="row"><span class="grow">{esc(name)}<span class="subtitle">{esc(what)}</span></span>'
            f'<span class="value">{esc(state)}</span><span class="dot" style="--tag:{dot}"></span></div>')


def render(s: LibrarySetup) -> str:
    c, cfg, jobs = s.checks(), s.cfg, s.jobs.view()
    here = s.system
    ts = c["tailscale"]
    ts_problem = hostinfo.tailscale_problem(ts)
    ollama_up = c["models"] is not None
    rec = ollama.recommended_model(c["ram"])

    # 1. This computer
    rows = []
    if c["ram"]:
        rows.append(_row("Memory", "How big a model this computer can run", f"{c['ram']:.0f} GB", True))
    if c["disk"] is not None:
        rows.append(_row("Disk", "Models take 2 to 25 GB each", f"{c['disk']:.0f} GB free", c["disk"] >= 30))
    rows.append(_row("Tailscale", "Lets your laptop and phone reach this computer from anywhere",
                     ts_problem or "connected", not ts_problem))
    rows.append(_row("Ollama", "Runs the model that writes your study notes, right here",
                     "running" if ollama_up else "installed, not running" if c["ollama_installed"] else "not installed",
                     ollama_up))
    fixes = []
    if ts_problem:
        label = "Install Tailscale" if not ts.get("installed") else "Open Tailscale"
        fixes.append('<p>Tailscale is free for personal use. Sign in with the same account on your laptop.</p>'
                     f'<div class="toolbar"><button class="primary" data-action="/api/tailscale" data-out="ts-say" '
                     f'data-busy="Working…"{" disabled" if s.jobs.running("tailscale") else ""}>{label}</button></div>'
                     f'<p class="say" id="ts-say"></p>{_job("tailscale", jobs)}')
    if not ollama_up:
        label = "Start Ollama" if c["ollama_installed"] else "Install Ollama"
        fixes.append('<p>Ollama is free, from ollama.com. Without it, lectures are still sorted by their Granola folder '
                     'and title, and keep Granola’s own notes.</p>'
                     f'<div class="toolbar"><button class="primary" data-action="/api/ollama" data-out="ol-say" '
                     f'data-busy="Working…"{" disabled" if s.jobs.running("ollama") else ""}>{label}</button></div>'
                     f'<p class="say" id="ol-say"></p>{_job("ollama", jobs)}')
    computer = (f'<div class="group" style="margin:-.8rem -1rem">{"".join(rows)}</div>'
                + (f'<div style="margin-top:1rem">{"".join(fixes)}</div>' if fixes else ""))

    # 2. The library
    library = (
        '<p>Your laptop and browser use this name and password to reach it.</p>'
        '<form class="stack" data-action="/api/library" data-out="lib-say" data-busy="Saving…">'
        f'<label class="field">Name<input type="text" name="name" value="{esc(cfg.pool_name)}" required></label>'
        f'<label class="field">Password<input type="text" name="password" value="{esc(cfg.pool_password)}" autocomplete="off" '
        'minlength="4" required></label>'
        f'<label class="field">Folder for the notes<input type="text" name="folder" value="{esc(str(cfg.pool_dir))}" required></label>'
        f'<label class="field">Port<input type="text" name="port" value="{cfg.web_port}" inputmode="numeric" required></label>'
        f'<div class="actions" style="margin:0"><button class="{"" if s.draft["library"] else "primary"}">'
        f'{"Save changes" if s.draft["library"] else "Save"}</button></div></form><p class="say" id="lib-say"></p>')

    # 3. Classes
    listed = "".join(
        f'<div class="row"><span class="grow">{esc(k.name)}<span class="subtitle">{esc(", ".join(k.aliases))}'
        f'</span></span><form data-action="/api/classes" data-out="cls-say"><input type="hidden" name="remove" value="{i}">'
        '<button>Remove</button></form></div>'
        for i, k in enumerate(cfg.classes))
    classes = (
        '<p>The folders lectures are sorted into. Other names are what you call it in Granola folder names or '
        'titles, like cs101.</p>'
        + (f'<div class="group" style="margin:0 -1rem .8rem">{listed}</div>' if listed else
           '<p class="muted">None yet: lectures land in Unsorted until you add some. You can add them later too.</p>')
        + '<form class="stack" data-action="/api/classes" data-out="cls-say" data-busy="Adding…">'
        '<div class="two"><input type="text" name="name" placeholder="Class, like CS 101" aria-label="Class name">'
        '<input type="text" name="aliases" placeholder="Other names, comma separated" aria-label="Other names"></div>'
        '<input type="text" name="description" placeholder="One line on what it covers (helps the model)" aria-label="Description">'
        '<div class="actions" style="margin:0"><button>Add class</button></div></form><p class="say" id="cls-say"></p>')

    # 4. The model
    if not ollama_up:
        notes = ('<p>Ollama isn’t running, so there’s no model to pick yet. Install or start it above, or go on '
                 'without: lectures are sorted by folder and title, and keep Granola’s notes.</p>'
                 '<div class="toolbar"><button data-action="/api/no-ai" data-out="ai-say">Go on without a model</button></div>'
                 '<p class="say" id="ai-say"></p>')
    else:
        names = s.names()
        options = list(dict.fromkeys(names + [rec]))
        chosen = cfg.effective_summary_model if s.draft["models"] else ollama.pick_default_model(names, rec)

        def select(field: str, pick: str, same: bool = False) -> str:
            opts = ('<option value="">The same model (fastest)</option>' if same else "") + "".join(
                f'<option value="{esc(n)}"{" selected" if n == pick else ""}>{esc(n)}'
                f'{"" if n in names else " (downloads it)"}</option>' for n in options)
            return f'<select name="{field}">{opts}</select>'

        sort_pick = cfg.ollama_model if s.draft["models"] and cfg.ollama_model != chosen else ""
        notes = (f'<p>For this computer’s memory, {esc(rec)} is a good fit. Notes are written from transcripts, '
                 'which Granola shares on its paid plans; other lectures keep Granola’s own summary.</p>'
                 '<form class="stack" data-action="/api/models" data-out="mod-say" data-busy="Saving…">'
                 f'<label class="field">Writes the study notes{select("summary", chosen)}</label>'
                 f'<label class="field">Sorts lectures into classes{select("sort", sort_pick, same=True)}</label>'
                 '<div class="actions" style="margin:0"><button class="primary">Use these</button></div></form>'
                 f'<p class="say" id="mod-say"></p>{_job("pull", jobs)}{_job("try", jobs)}')

    # 5. Keep it running
    running = (
        '<form data-action="/api/prefs" data-out="pref-say" data-autosave><div class="group" style="margin:-.8rem -1rem">'
        '<label class="row"><span class="grow">Start when this computer starts<span class="subtitle">Keeps your library '
        'running in the background, and restarts it if it stops</span></span>'
        f'<input class="switch" type="checkbox" name="autostart"{" checked" if s.draft["autostart"] else ""}></label>'
        '<label class="row"><span class="grow">Install new versions automatically</span>'
        f'<input class="switch" type="checkbox" name="auto_update"{" checked" if cfg.auto_update else ""}></label>'
        '</div></form><p class="say" id="pref-say"></p>')
    if here == "Windows":
        if s.firewall(cfg.web_port) is False:
            running += ('<p style="margin-top:1.1rem">Windows Firewall blocks your laptop from this computer. Let it through, for Tailscale and '
                        'this network only.</p><div class="toolbar"><button class="primary" data-action="/api/firewall" '
                        f'data-out="fw-say">Let my laptop in</button></div><p class="say" id="fw-say"></p>{_job("firewall", jobs)}')
    mins = s.sleep_minutes(here) if here in ready.SLEEP_FIX else None
    if mins:
        running += (f'<p style="margin-top:1.1rem">This computer sleeps after {mins} minute{"s" if mins != 1 else ""}, '
                    'and your library goes offline with it.</p>')
        running += ('<div class="toolbar"><button data-action="/api/awake" data-out="aw-say">Keep it awake while plugged in'
                    '</button></div><p class="say" id="aw-say"></p>' if here == "Windows" else
                    f'<p class="muted">{esc(ready.SLEEP_FIX[here])}.</p>')

    # 6. Finish
    if s.draft["finished"]:
        info = s.connect_info()
        exe, dmg = f"{RELEASES}/Study-Stash-Laptop-Setup.exe", f"{RELEASES}/Study-Stash-Laptop.dmg"
        finish = (
            f'<p>Your library is running. Open it here, or from your laptop and phone:</p>'
            f'<p class="addr">{esc(info["urls"][0])}</p><p>Password: <span class="addr">{esc(info["password"])}</span></p>'
            f'<div class="toolbar"><a class="btn primary" href="http://127.0.0.1:{cfg.web_port}/">Open your library</a></div>'
            '<h3 style="margin:1.2rem 0 .3rem">Connect your laptop</h3>'
            f'<p>Install Study Stash on the laptop you record lectures on (<a href="{dmg}">Mac</a> or '
            f'<a href="{exe}">Windows</a>), open it, and enter the address and password above.</p>'
            '<details class="help"><summary>Or with one line in a terminal</summary>'
            f'<p>Mac:</p><pre class="code">{esc(info["commands"]["mac"])}</pre>'
            f'<p>Windows (PowerShell):</p><pre class="code">{esc(info["commands"]["windows"])}</pre></details>')
    else:
        finish = ('<p>Saves your library and starts it. You can change any of this later, under Settings on the '
                  'library’s page.</p>'
                  '<div class="actions" style="margin:0"><button class="primary" data-action="/api/finish" '
                  f'data-out="fin-say" data-busy="Finishing…"{"" if s.draft["library"] else " disabled"}>Finish setup</button>'
                  f'</div><p class="say" id="fin-say"></p>{_job("finish", jobs)}')

    steps = [_step(1, "This computer", not ts_problem and ollama_up, False, computer),
             _step(2, "Your library", s.draft["library"], False, library),
             _step(3, "Classes", bool(cfg.classes), not s.draft["library"], classes),
             _step(4, "Study notes", s.draft["models"] and (not cfg.ollama_enabled or s.model_ready(cfg.effective_summary_model)),
                   not s.draft["library"], notes),
             _step(5, "Keep it running", s.draft["finished"], not s.draft["library"], running),
             _step(6, "Finish" if not s.draft["finished"] else "Ready", s.draft["finished"], not s.draft["library"], finish)]
    return ('<header><h1>Set up your library</h1><p class="sub">This computer keeps your lectures: it writes their study '
            'notes with a model that runs here, sorts them by class, and serves them to your laptop and phone. Your '
            'answers are kept as you go.</p></header>' + "".join(steps)
            + '<p class="group-foot">Study Stash is an independent project, not affiliated with or endorsed by Granola.</p>')


def create_setup_app(s: LibrarySetup, *, port: int = SETUP_PORT, extra_hosts: tuple[str, ...] = ()) -> FastAPI:
    app = FastAPI(title="Study Stash setup", docs_url=None, redoc_url=None, openapi_url=None)
    token = _token(s.home)
    allowed_hosts = {f"127.0.0.1:{port}", f"localhost:{port}", *extra_hosts}

    @app.middleware("http")
    async def guard(request: Request, call_next):
        # DNS rebinding: a web page can point a hostname at 127.0.0.1, but it can't fake the Host header.
        if request.headers.get("host", "") not in allowed_hosts:
            return JSONResponse({"detail": "not here"}, status_code=403)
        s.last_seen = time.time()
        return await call_next(request)

    def authed(request: Request) -> bool:
        return hmac.compare_digest(request.cookies.get(COOKIE, ""), token)

    def require(request: Request) -> None:
        if not authed(request) or request.headers.get("x-granola-share") != "1":
            raise HTTPException(403, "Open the setup from the Study Stash Library app.")

    async def body(request: Request) -> dict:
        try:
            data = await request.json()
            return data if isinstance(data, dict) else {}
        except Exception:
            return {}

    def act(fn, *args) -> dict:
        try:
            return {"message": fn(*args), "reload": True, "delay": 400}
        except ValueError as e:
            raise HTTPException(400, str(e)) from None

    @app.get("/", response_class=HTMLResponse)
    def index(request: Request, t: str = ""):
        if t and hmac.compare_digest(t, token):
            resp = RedirectResponse("/", status_code=303)
            resp.set_cookie(COOKIE, token, httponly=True, samesite="strict", max_age=60 * 60 * 24 * 365)
            return resp
        if not authed(request):
            return HTMLResponse("<p>Open the setup from the Study Stash Library app.</p>", status_code=403)
        nonce = secrets.token_urlsafe(16)
        page = (ui.head("Set up your library", nonce).replace("</style>", APP_CSS + SETUP_CSS + "</style>")
                + f'<body><div class="solo">{render(s)}</div>'
                + f'<script nonce="{nonce}">{APP_JS}{SETUP_JS}</script></body></html>')
        return HTMLResponse(page, headers={"Content-Security-Policy": CSP.format(nonce=nonce)})

    @app.get("/api/state")
    def state(request: Request):
        if not authed(request):
            raise HTTPException(403, "not signed in")
        return {"key": json.dumps(s.key(), sort_keys=True, default=str), "jobs": s.jobs.view(),
                "finished": s.draft["finished"], "version": __version__}

    @app.post("/api/tailscale")
    async def tailscale(request: Request):
        require(request)
        return {**act(s.fix_tailscale), "reload": False}

    @app.post("/api/ollama")
    async def ollama_(request: Request):
        require(request)
        return {**act(s.fix_ollama), "reload": False}

    @app.post("/api/library")
    async def library(request: Request):
        require(request)
        return act(s.set_library, await body(request))

    @app.post("/api/classes")
    async def classes(request: Request):
        require(request)
        data = await body(request)
        return act(s.change_classes, data)

    @app.post("/api/models")
    async def models(request: Request):
        require(request)
        return {**act(s.set_models, await body(request)), "reload": False}

    @app.post("/api/no-ai")
    async def no_ai(request: Request):
        require(request)
        return act(s.without_ai)

    @app.post("/api/prefs")
    async def prefs(request: Request):
        require(request)
        return {**act(s.set_prefs, await body(request)), "reload": False}

    @app.post("/api/firewall")
    async def firewall(request: Request):
        require(request)
        return {**act(s.fix_firewall), "reload": False}

    @app.post("/api/awake")
    async def awake(request: Request):
        require(request)
        return act(s.stay_awake)

    @app.post("/api/finish")
    async def finish(request: Request):
        require(request)
        return {**act(s.finish), "reload": False}

    @app.get("/healthz")
    def healthz():
        return {"ok": True, "app": "granola-share-setup", "version": __version__}

    ui.add_icon_routes(app)
    return app


def serve(home: Path, *, browser: bool = True, log=lambda s: print(s, flush=True)) -> None:
    """Blocks until setup is finished (and a little after, for the last page), or nobody has looked at the
    page for an hour."""
    import uvicorn

    s = LibrarySetup(home)
    port = free_port(SETUP_PORT)
    (s.home / "setup_port").write_text(str(port))
    url = f"http://127.0.0.1:{port}/?t={_token(s.home)}"
    server = uvicorn.Server(uvicorn.Config(create_setup_app(s, port=port), host="127.0.0.1", port=port,
                                           log_level="warning"))

    def watch() -> None:
        done_at = None
        while not server.should_exit:
            time.sleep(2)
            if s.draft["finished"] and done_at is None:
                done_at = time.time()
            if (done_at and time.time() - done_at > 600) or time.time() - s.last_seen > 3600:
                server.should_exit = True

    threading.Thread(target=watch, daemon=True).start()
    log(url)
    if browser:
        dialogs.open_url(url)
    try:
        server.run()
    finally:
        (s.home / "setup_port").unlink(missing_ok=True)
