"""Guided setup: `granola-share setup` (the Mac mini that keeps your library) and `granola-share client setup` (your laptop).

Every question has a key, so it can also be answered with a command-line flag
(see `granola-share setup --help`); `--yes` accepts the default for the rest.
"""

from __future__ import annotations

import asyncio
import getpass
import os
import platform
import secrets
import sys
import time
from pathlib import Path

import httpx

from . import __version__, autostart, hostinfo, ollama, ready, update
from .config import ClassDef, ClientConfig, Config, load_client_config, load_config, save_client_config, save_config

_UNSET = object()
NO_TERMINAL = ("Setup needs answers but there is no keyboard attached. Run it in a terminal, "
               "or answer with flags plus --yes (see `granola-share setup --help`).")


class Prompter:
    """Terminal prompts. Answers given as flags skip their question; `assume_defaults` (--yes) takes defaults."""

    def __init__(self, preset: dict | None = None, assume_defaults: bool = False):
        self.preset = {k: v for k, v in (preset or {}).items() if v is not None}
        self.assume_defaults = assume_defaults

    def say(self, text: str = "") -> None:
        print(text, flush=True)

    def take(self, key: str | None, default=None):
        """A flag's answer (used once, so a retry after a wrong answer asks for real)."""
        return self.preset.pop(key) if key in self.preset else default

    def _input(self, prompt: str) -> str:
        try:
            return input(prompt)
        except EOFError:
            raise SystemExit("\n" + NO_TERMINAL) from None

    def ask(self, text: str, default: str | None = None, key: str | None = None) -> str:
        given = self.take(key, _UNSET)
        if given is not _UNSET:
            self.say(f"{text}: {given}")
            return str(given)
        if self.assume_defaults:
            self.say(f"{text}: {default or ''}")
            return default or ""
        suffix = f" [{default}]" if default not in (None, "") else ""
        ans = self._input(f"{text}{suffix}: ").strip()
        return ans or (default or "")

    def confirm(self, text: str, default: bool = True, key: str | None = None) -> bool:
        given = self.take(key, _UNSET)
        if given is not _UNSET or self.assume_defaults:
            value = bool(default if given is _UNSET else given)
            self.say(f"{text} {'yes' if value else 'no'}")
            return value
        hint = "Y/n" if default else "y/N"
        ans = self._input(f"{text} ({hint}): ").strip().lower()
        return default if not ans else ans in ("y", "yes")

    def pause(self, text: str) -> None:
        """Wait for Enter while the person does something outside the terminal (an installer, a sign-in)."""
        if not self.assume_defaults:
            self._input(text)

    def progress(self, label: str) -> "Bar":
        return Bar(label)

    def secret(self, text: str, key: str | None = None) -> str:
        given = self.take(key, _UNSET)
        if given is not _UNSET:
            return str(given)
        if self.assume_defaults:
            return ""
        try:
            return getpass.getpass(f"{text}: ").strip()
        except EOFError:
            raise SystemExit("\n" + NO_TERMINAL) from None


def _size(n: float) -> str:
    return f"{n / 1e9:.1f} GB" if n >= 1e9 else f"{n / 1e6:.0f} MB"


class Bar:
    """A download's progress on one line that updates in place (every quarter when it isn't a terminal)."""

    def __init__(self, label: str, out=None):
        self.label, self.out, self.shown = label, out or sys.stdout, -1
        self.tty = bool(getattr(self.out, "isatty", lambda: False)())

    def __call__(self, done: int, total: int) -> None:
        pct = min(100, int(done * 100 / total)) if total else 0
        if pct == self.shown:
            return
        line = f"    {self.label}  {pct:3d}%  of {_size(total)}"
        if self.tty:
            self.out.write("\r" + line)
            self.out.flush()
        elif pct // 25 > max(self.shown, 0) // 25 or self.shown < 0:
            print(line, file=self.out, flush=True)
        self.shown = pct

    def end(self) -> None:
        if self.tty and self.shown >= 0:
            self.out.write("\n")
            self.out.flush()


class ScriptedPrompter(Prompter):
    """For tests: answers in order, whatever the question."""

    def __init__(self, answers: list):
        super().__init__()
        self.answers = list(answers)
        self.output: list[str] = []

    def say(self, text: str = "") -> None:
        self.output.append(text)

    def _next(self, text):
        if not self.answers:
            raise AssertionError(f"no scripted answer for: {text}")
        return self.answers.pop(0)

    def ask(self, text, default=None, key=None):
        a = self._next(text)
        return a if a != "" else (default or "")

    def confirm(self, text, default=True, key=None):
        return bool(self._next(text))

    def secret(self, text, key=None):
        return self._next(text)

    def pause(self, text):
        self.output.append(text.strip())

    def progress(self, label):
        io = self

        class Recorded(Bar):
            def __init__(self):
                super().__init__(label, out=open(os.devnull, "w"))

            def end(self):
                io.output.append(f"{label} {self.shown}%")
                self.out.close()

        return Recorded()


# --- helpers the wizards call (all injectable for tests) ----------------------

def server_login(cfg: Config) -> None:
    from .oauth import GranolaOAuth

    GranolaOAuth(cfg).login()


def client_login(cc: ClientConfig) -> None:
    from .oauth import GranolaOAuth

    GranolaOAuth(cc).login()


def logged_in(cfg) -> bool:
    """Signed in with a token that still works (refreshing it if needed)."""
    from .oauth import GranolaOAuth

    if not cfg.tokens_path.exists():
        return False
    try:
        GranolaOAuth(cfg).access_token()
        return True
    except Exception:
        return False


def client_share_now(cc: ClientConfig, log=print) -> None:
    from .client import ShareClient
    from .granola import GranolaClient
    from .oauth import GranolaOAuth
    from .transcript_grab import TranscriptStore

    store = TranscriptStore(cc.home) if cc.copy_transcripts else None
    client = ShareClient(cc, GranolaClient(cc, GranolaOAuth(cc)), log=log, transcripts=store)
    rep = asyncio.run(client.poll_once())
    log(f"shared {len(rep.shared)}, skipped {len(rep.skipped)}, waiting {len(rep.pending)}, errors {len(rep.errors)}")


def wait_for_server(cfg: Config, timeout: float = 25) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            r = httpx.get(f"http://127.0.0.1:{cfg.web_port}/api/health",
                          headers={"Authorization": f"Bearer {cfg.pool_password}"}, timeout=3)
            if r.status_code == 200 and r.json().get("version") == __version__:
                return True
        except Exception:
            pass
        time.sleep(1)
    return False


def _wait_running(service_status, role: str, timeout: float = 8) -> bool:
    deadline = time.time() + timeout
    while True:
        if service_status(role) == "running":
            return True
        if time.time() >= deadline:
            return False
        time.sleep(1)


def _normalize_url(url: str) -> str:
    url = url.strip().rstrip("/")
    if url and "://" not in url:
        url = "http://" + url
    return url


def parse_class(spec: str) -> ClassDef:
    """--class "CS 101=cs101,intro programming" → ClassDef("CS 101", ["cs101", "intro programming"])."""
    name, _, aliases = spec.partition("=")
    return ClassDef(name.strip(), [a.strip() for a in aliases.split(",") if a.strip()])


def _try(io: Prompter, what: str, fn, *args, attempts: int = 3, key: str | None = None) -> bool:
    """Run a step that can fail (network, browser). Offers a retry instead of crashing setup."""
    for i in range(attempts):
        try:
            fn(*args)
            return True
        except KeyboardInterrupt:
            raise
        except Exception as e:
            io.say(f"  {what} failed: {e}")
            if i == attempts - 1 or not io.confirm(f"  Try {what.lower()} again?", not io.assume_defaults, key=key):
                return False
    return False


def _ask_port(io: Prompter, current: int, port_status) -> int:
    value = str(current)
    for _ in range(5):
        value = io.ask("Web port", value, key="port")
        if not value.isdigit() or not 1024 <= int(value) <= 65535:
            io.say("  Use a number from 1024 to 65535.")
            value = str(current)
            continue
        state = port_status(int(value))
        if state == "busy":
            io.say(f"  Another app is using port {value}. Pick a different one (8788 is usually free).")
            value = str(int(value) + 1)
            continue
        return int(value)
    raise SystemExit("No usable port. Rerun `granola-share setup` and choose a free one.")


def _ask_folder(io: Prompter, current: Path) -> Path:
    value = str(current)
    for _ in range(5):
        path = Path(io.ask("Folder where notes are stored", value, key="pool_dir")).expanduser()
        try:
            path.mkdir(parents=True, exist_ok=True)
            (path / ".write-test").write_text("ok")
            (path / ".write-test").unlink()
            return path
        except OSError as e:
            io.say(f"  Can't write there ({e}). Pick another folder.")
    raise SystemExit("No writable folder. Rerun `granola-share setup` and choose one.")


def _choose_model(io: Prompter, text: str, default: str, names: list[str], key: str) -> str:
    for _ in range(5):
        ans = io.ask(text, default, key=key).strip()
        if ans.isdigit():
            if 1 <= int(ans) <= len(names):
                return names[int(ans) - 1]
            io.say(f"  Pick a number from 1 to {len(names)}, or type a model name.")
            continue
        if ans:
            return ans
    return default


# --- the server wizard --------------------------------------------------------

def _tailscale_line(ts: dict) -> str:
    if ts.get("running"):
        where = ts.get("dns") or (ts.get("ips") or [""])[0]
        return "connected" + (f"; your laptop reaches this computer as {where}" if where else "")
    return hostinfo.tailscale_problem(ts)


def _ready_tailscale(io: Prompter, *, tailscale, install_tailscale, connect_tailscale) -> dict:
    """Tailscale installed, signed in, and connected, or a clear note on what that means if not."""
    ts = tailscale()
    io.say(f"  Tailscale:  {_tailscale_line(ts)}")
    if ts.get("running"):
        return ts
    if not ts.get("installed"):
        io.say("              It lets your laptop and phone reach this computer from anywhere, privately,")
        io.say("              without opening anything to the internet. It's free for personal use.")
        if not io.confirm("  Install Tailscale now?", not io.assume_defaults, key="install_tailscale"):
            io.say(f"    Skipped. Get it from {ready.TAILSCALE_DOWNLOAD} when you're ready, then rerun setup.")
            return ts
        bar = io.progress("Downloading Tailscale")
        installed = install_tailscale(io.say, bar, io.pause)
        bar.end()
        ts = tailscale()
        if not installed or not ts.get("installed"):
            io.say(f"  Tailscale:  {_tailscale_line(ts)}")
            return ts
    elif not io.confirm("  Connect it now?", not io.assume_defaults, key="install_tailscale"):
        return ts
    if not ts.get("running"):
        connect_tailscale(ts, io.say)
        ts = tailscale()
    if not ts.get("running") and not io.assume_defaults:
        io.say("    Open Tailscale and sign in from its icon (menu bar on a Mac, system tray on Windows).")
        io.pause("    Press Enter once you've signed in: ")
        ts = tailscale()
    io.say(f"  Tailscale:  {_tailscale_line(ts)}")
    return ts


def _ready_ollama(io: Prompter, cfg: Config, *, list_models, ollama_installed, start_ollama, install_ollama):
    """Ollama installed and answering. Returns its models, or None to go on without AI."""
    models = list_models(cfg.ollama_host)
    if models is None and ollama_installed():
        io.say("  Ollama:     installed, but not running. Starting it...")
        if start_ollama(cfg.ollama_host):
            models = list_models(cfg.ollama_host)
    elif models is None:
        io.say("  Ollama:     not installed. It runs the model that writes your study notes, right here.")
        if io.confirm("  Install Ollama now? (free, from ollama.com)", not io.assume_defaults, key="install_ollama"):
            bar = io.progress("Downloading Ollama")
            ok = install_ollama(io.say, bar)
            bar.end()
            if ok:
                io.say("    Starting Ollama...")
                start_ollama(cfg.ollama_host)
                models = list_models(cfg.ollama_host)
    for _ in range(3):
        if models is not None:
            break
        io.say(f"  Ollama isn't answering at {cfg.ollama_host}. Get it from https://ollama.com and open it.")
        if not io.confirm("  Check again? (no = continue without AI; folder and title rules still sort)",
                          not io.assume_defaults, key="retry_ollama"):
            break
        start_ollama(cfg.ollama_host)
        models = list_models(cfg.ollama_host)
    if models is not None:
        io.say("  Ollama:     running, " + (f"with {len(models)} model{'s' if len(models) != 1 else ''}"
                                             if models else "no models yet"))
    return models


def server_setup(home: Path, io: Prompter | None = None, *, list_models=ollama.list_models,
                 start_ollama=ollama.start, ollama_installed=ollama.installed, pull_model=ollama.pull,
                 try_model=ollama.try_model, install_ollama=ready.install_ollama, ram_gb=ollama.total_ram_gb,
                 disk_free=ready.disk_free_gb, do_login=server_login, install_autostart=autostart.install,
                 tailscale=hostinfo.tailscale_info, install_tailscale=ready.install_tailscale,
                 connect_tailscale=ready.connect_tailscale, firewall=ready.firewall_open,
                 open_firewall=ready.open_firewall, sleep_minutes=ready.sleep_minutes, keep_awake=ready.keep_awake,
                 port_status=hostinfo.port_status, wait_healthy=wait_for_server, cleanup=update.cleanup_legacy,
                 system: str | None = None) -> Config:
    io = io or Prompter()
    system = system or platform.system()
    cfg = load_config(home)
    fresh = not cfg.config_path.exists()
    io.say(f"\n== Study Stash {__version__}: set up your library ==\n")
    io.say("This computer keeps your lectures. It writes study notes with a model that runs right here,")
    io.say("sorts each lecture into its class, and serves your library to your laptop and phone.")
    io.say("Rerunning this is safe: your answers from last time are the defaults.\n")

    io.say("1/6  Get this computer ready")
    ram, free = ram_gb(), disk_free()
    rec = ollama.recommended_model(ram)
    if ram:
        io.say(f"  Memory:     {ram:.0f} GB, enough for {rec}")
    if free is not None:
        io.say(f"  Disk:       {free:.0f} GB free" + ("  (models take 2 to 25 GB each: make some room)" if free < 30 else ""))
    ts = _ready_tailscale(io, tailscale=tailscale, install_tailscale=install_tailscale,
                          connect_tailscale=connect_tailscale)
    models = _ready_ollama(io, cfg, list_models=list_models, ollama_installed=ollama_installed,
                           start_ollama=start_ollama, install_ollama=install_ollama)

    io.say("\n2/6  Your library")
    cfg.pool_name = io.ask("Name for it", cfg.pool_name, key="pool_name")
    cfg.pool_password = io.ask("Password for your laptop and browser (blank = make one up)", cfg.pool_password,
                               key="password") or secrets.token_urlsafe(9)
    cfg.pool_dir = _ask_folder(io, cfg.pool_dir)
    cfg.web_port = _ask_port(io, cfg.web_port, port_status)

    io.say("\n3/6  Classes (the folders lectures get sorted into)")
    given = io.take("classes")
    if given is not None:
        cfg.classes = list(given)
        io.say("Classes: " + (", ".join(cfg.class_names()) or "none"))
    else:
        if cfg.classes:
            io.say("Already set up: " + ", ".join(cfg.class_names()))
            if not io.confirm("Keep them?", True, key="keep_classes"):
                cfg.classes = []
        if not io.assume_defaults:
            io.say("Add classes one at a time. Leave the name blank when you're done.")
            while True:
                name = io.ask("Class name (e.g. CS 101)", "")
                if not name:
                    break
                aliases = [a.strip() for a in io.ask("  other names, comma separated (e.g. cs101, intro programming)",
                                                     "").split(",") if a.strip()]
                desc = io.ask("  one line on what it covers (helps the AI)", "")
                cfg.classes.append(ClassDef(name, aliases, desc))
    if not cfg.classes:
        io.say("No classes yet: lectures land in Unsorted until you add some (web UI → Settings).")

    io.say("\n4/6  Study notes (the model that writes them)")
    if models is None:
        cfg.ollama_enabled = False
        io.say("Continuing without AI: folder and title rules still sort lectures. Rerun setup once Ollama is")
        io.say("installed, or turn AI on later in the web UI under Settings.")
    else:
        cfg.ollama_enabled = True
        names = [m["name"] for m in models]
        if models:
            io.say("Installed models:")
            for i, m in enumerate(models, 1):
                io.say(f"  {i}) {m['name']:<28} {ollama.size_label(m)}")
        else:
            io.say(f"No models yet. For this computer: {rec}")
        io.say("Notes are written from transcripts. Granola only shares transcripts from paid plans;")
        io.say("lectures from free accounts keep Granola's own summary.")
        default = cfg.effective_summary_model if not fresh else ollama.pick_default_model(names, rec)
        summary = _choose_model(io, "Model that writes study notes (number or name)", default, names, "summary_model")
        sort_default = summary if fresh or cfg.ollama_model == cfg.effective_summary_model else cfg.ollama_model
        sort = _choose_model(io, "Model that sorts lectures into classes (the same one is fastest)", sort_default,
                             names, "sort_model")
        cfg.summary_model, cfg.ollama_model = summary, sort
        ready_models, pull = [], io.take("pull")  # --pull / --no-pull answers for every missing model
        for model in dict.fromkeys([summary, sort]):
            if ollama.has_model(names, model):
                ready_models.append(model)
            elif (io.confirm(f"Download {model} now? (can be several GB)", True) if pull is None else
                  io.say(f"Download {model} now? {'yes' if pull else 'no'}") or pull):
                bar = io.progress(f"Downloading {model}")
                ok, why = pull_model(model, cfg.ollama_host, bar)
                bar.end()
                if ok:
                    ready_models.append(model)
                else:
                    io.say(f"  Download failed: {why}")
                    if "newer version" in why.lower():
                        io.say("  Update Ollama (open it and choose Restart to Update, or get it again from ollama.com).")
                    io.say(f"  Run `ollama pull {model}` later; lectures wait in the queue until then.")
            else:
                io.say(f"  Run `ollama pull {model}` before the first lecture arrives.")
        for model in ready_models:
            io.say(f"  Trying {model} once (the first load can take a minute)...")
            secs, why = try_model(cfg.ollama_host, model)
            took = "under a second" if secs is not None and secs < 1 else f"{secs or 0:.0f} s"
            io.say(f"  It answered in {took}. Ready to write study notes." if secs is not None else
                   f"  It didn't answer: {why}. Lectures wait in the queue; pick another model in Settings if it keeps failing.")

    io.say("\n5/6  Keep it running")
    if cfg.server_sync or "server_sync" in io.preset:
        # Rare: your laptop usually sends the lectures. Only asked when it's on or given as a flag.
        cfg.server_sync = io.confirm("Also pull lectures from a Granola account signed in on this computer?",
                                     cfg.server_sync, key="server_sync")
    cfg.auto_update = io.confirm("Install new versions automatically?", cfg.auto_update, key="auto_update")
    save_config(cfg)
    if cfg.server_sync and not logged_in(cfg):
        io.say("Sign in to Granola in the browser window that opens.")
        if not _try(io, "Signing in", do_login, cfg):
            io.say("  Skipped. Run `granola-share login` later.")
    started = False
    if io.confirm("Start at login and keep running in the background?", True, key="autostart"):
        path = install_autostart("server", home)
        io.say(f"Installed: {path}")
        started = True
        io.say("Waiting for it to come up...")
        healthy = wait_healthy(cfg)
        io.say("  It's up." if healthy else
               f"  It hasn't answered yet. Run `granola-share doctor` to see why (log: {cfg.log_dir / 'server.log'}).")
        cleanup(home, io.say)
    if system == "Windows" and firewall(cfg.web_port) is False:
        io.say(f"Windows Firewall blocks other computers from port {cfg.web_port}, so your laptop can't reach the library.")
        if io.confirm("Let Tailscale and this network through to it? (Windows asks for permission)",
                      not io.assume_defaults, key="firewall"):
            io.say("  Opened." if open_firewall(cfg.web_port) else
                   "  Not changed. Rerun setup to try again, or allow python in Windows Security → Firewall.")
    mins = sleep_minutes(system) if system in ready.SLEEP_FIX else None
    if mins:
        io.say(f"This computer sleeps after {mins} minute{'s' if mins != 1 else ''} idle, and your library goes "
               "offline while it sleeps.")
        if system == "Windows" and io.confirm("Keep it awake while it's plugged in? (the screen still turns off)",
                                              not io.assume_defaults, key="keep_awake") and keep_awake(system):
            io.say("  Done: it stays awake while plugged in.")
        else:
            io.say(f"  To change it: {ready.SLEEP_FIX[system]}.")

    urls = hostinfo.server_urls(cfg.web_port, ts)
    io.say("\n6/6  Connect your laptop\n")
    if not ts.get("running"):
        io.say("  Tailscale isn't connected here, so your laptop can only reach this computer on the same Wi-Fi.")
        io.say(f"  Install it on both ({ready.TAILSCALE_DOWNLOAD}) and sign in to the same account.\n")
    else:
        io.say("  On the laptop, sign in to Tailscale with the same account as this computer.\n")
    io.say(f"  Library:   {cfg.pool_name}")
    io.say(f"  Address:   {urls[0]}" + (f"   (or {urls[1]})" if len(urls) > 1 else ""))
    io.say(f"  Password:  {cfg.pool_password}\n")
    cmds = hostinfo.invite_commands(urls[0], cfg.pool_password)
    io.say("On the computer you record lectures on, install Study Stash and enter this address and password:")
    io.say("  Mac:      https://github.com/Joseph-Rus/study-stash/releases/latest/download/Study-Stash-Laptop.dmg")
    io.say("  Windows:  https://github.com/Joseph-Rus/study-stash/releases/latest/download/Study-Stash-Laptop-Setup.exe")
    io.say("Or paste this one line there, which installs everything with the address and password filled in:")
    io.say(f"  Mac/Linux:  {cmds['mac']}")
    io.say(f"  Windows:    {cmds['windows']}\n")
    io.say(f"Your library and its settings (models, classes): {urls[0]}  (log in with the password above)")
    io.say(f"Config: {cfg.config_path}")
    if not started:
        io.say("Start it with:  granola-share run")
    io.say("Check everything any time with:  granola-share doctor")
    return cfg


# --- the laptop wizard --------------------------------------------------------

def _connect_hint(err: str) -> str:
    if "password" in err:
        return "  Double-check the password from your library's setup (it's also in that computer's config.toml)."
    return ("  Check that Tailscale is on and signed in on both computers, that the library's computer is awake, "
            "and that the address is right.")


def client_setup(home: Path, io: Prompter | None = None, *, check_server=None, do_login=client_login,
                 is_logged_in=logged_in, install_autostart=autostart.install, share_now=client_share_now,
                 service_status=autostart.status, cleanup=update.cleanup_legacy, mac: bool | None = None,
                 laptop=None) -> ClientConfig:
    from .client import check_server as _check

    check_server = check_server or _check
    io = io or Prompter()
    cc = load_client_config(home)
    io.say(f"\n== Study Stash {__version__}: send your Granola lectures to your library ==\n")
    checks = (laptop or ready.laptop_checks)()
    problem = hostinfo.tailscale_problem(checks["tailscale"])
    io.say("This computer")
    if checks["granola_here"]:
        io.say("  Granola:    " + ("installed" if checks["granola"] else
                                   f"not installed. It records your lectures: get it from {ready.GRANOLA_DOWNLOAD}"))
    io.say("  Tailscale:  " + (f"{problem}. Without it, the library is reachable only on the same Wi-Fi: "
                               + ("open Tailscale and sign in" if checks["tailscale"].get("installed")
                                  else f"get it from {ready.TAILSCALE_DOWNLOAD}") if problem else "connected"))
    io.say("")

    io.say("1/4  Your library (the address and password from its setup)")
    info = None
    for attempt in range(3):
        cc.server_url = _normalize_url(io.ask("Address (e.g. http://mac-mini:8787)", cc.server_url,
                                              key="server"))
        cc.pool_key = io.ask("Password", cc.pool_key, key="key")
        try:
            info = check_server(cc.server_url, cc.pool_key)
            break
        except Exception as e:
            io.say(f"  Could not connect: {e}")
            io.say(_connect_hint(str(e)))
            if attempt == 2 or io.assume_defaults:
                raise SystemExit("Fix the address or password and run `granola-share client setup` again.")
    cc.pool_name = str(info.get("pool_name") or "your library")
    classes = info.get("classes") or []
    io.say(f"  Connected to '{cc.pool_name}'" + (f" (classes: {', '.join(classes)})" if classes else ""))

    cc.display_name = io.take("name") or cc.display_name or getpass.getuser()

    io.say("\n2/4  Granola")
    if is_logged_in(cc) and io.confirm("Already signed in to Granola. Keep that sign-in?", True, key="keep_login"):
        pass
    else:
        io.say("A browser window opens; sign in to your Granola account. Nothing is shared until you say so.")
        if not _try(io, "Signing in", do_login, cc):
            io.say("  Skipped. Run `granola-share client login` to sign in later; nothing is shared until then.")

    io.say("\n3/4  Sending")
    ask_each = io.confirm("Ask before sending each finished lecture? (no = send every lecture automatically)",
                          cc.mode == "ask", key="ask_each")
    cc.mode = "ask" if ask_each else "auto"
    on_mac = platform.system() == "Darwin" if mac is None else mac
    if on_mac:
        io.say("Optional: on a Mac, Study Stash can press Granola's own Copy transcript for you when a lecture")
        io.say("ends, then put your clipboard and cursor back, so your notes are written from the transcript.")
        io.say("This automates the Granola app, which may go against Granola's terms of service. It's off unless")
        io.say("you turn it on, and you decide whether that's OK for your account.")
        cc.copy_transcripts = io.confirm("Copy transcripts from the Granola app?", cc.copy_transcripts,
                                         key="copy_transcripts")
        if cc.copy_transcripts:
            io.say("  When the watcher starts, macOS asks to let python3.12 use Accessibility. Allow it in")
            io.say("  System Settings → Privacy & Security → Accessibility (python3.12 is what granola-share runs on).")
    save_client_config(cc)
    if io.confirm(f"Look at lectures from the last {cc.share_lookback_days} days now?", True, key="share_now"):
        if not _try(io, "Checking Granola", share_now, cc, io.say, attempts=1):
            io.say("  The background watcher will try again in a few minutes.")

    io.say("\n4/4  Keep it running")
    cc.auto_update = io.confirm("Install new versions automatically?", cc.auto_update, key="auto_update")
    save_client_config(cc)
    if io.confirm("Start at login and keep watching for new lectures?", True, key="autostart"):
        path = install_autostart("client", home)
        io.say(f"Installed: {path}")
        running = _wait_running(service_status, "client")
        io.say("  The watcher is running." if running else
               "  The watcher isn't running yet. Run `granola-share doctor` to see why.")
        cleanup(home, io.say)
    else:
        io.say("Run `granola-share client run` whenever you want it watching.")
    io.say(f"\nDone. Config: {cc.config_path}")
    io.say("Check everything any time with:  granola-share doctor")
    return cc


def make_prompter(args) -> Prompter:
    """Build the Prompter from `setup` / `client setup` flags."""
    preset = {k: v for k, v in vars(args).items() if k.startswith("a_")}
    preset = {k[2:]: v for k, v in preset.items()}
    if preset.get("classes") is not None:
        preset["classes"] = [parse_class(s) for s in preset["classes"]]
    if os.environ.get("GRANOLA_SHARE_SERVER") and preset.get("server") is None:
        preset["server"] = os.environ["GRANOLA_SHARE_SERVER"]
    if os.environ.get("GRANOLA_SHARE_KEY") and preset.get("key") is None:
        preset["key"] = os.environ["GRANOLA_SHARE_KEY"]
    return Prompter(preset, assume_defaults=getattr(args, "yes", False))
