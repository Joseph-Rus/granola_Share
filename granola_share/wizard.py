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
import time
from pathlib import Path

import httpx

from . import __version__, autostart, hostinfo, ollama, update
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

def server_setup(home: Path, io: Prompter | None = None, *, list_models=ollama.list_models,
                 start_ollama=ollama.start, ollama_installed=ollama.installed, pull_model=ollama.pull,
                 ram_gb=ollama.total_ram_gb, do_login=server_login, install_autostart=autostart.install,
                 tailscale=hostinfo.tailscale_info, port_status=hostinfo.port_status, wait_healthy=wait_for_server,
                 cleanup=update.cleanup_legacy) -> Config:
    io = io or Prompter()
    cfg = load_config(home)
    fresh = not cfg.config_path.exists()
    io.say(f"\n== granola-share {__version__}: set up your lecture library ==\n")
    io.say("This computer keeps your lectures: it writes their summaries with your own model, sorts them by class,")
    io.say("and serves them to your browser. Your laptop sends each lecture here when Granola finishes it.")
    io.say("Rerunning this is safe: your answers from last time are the defaults.\n")

    io.say("1/6  Your library")
    cfg.pool_name = io.ask("Name for it", cfg.pool_name, key="pool_name")
    cfg.pool_password = io.ask("Password for your laptop and browser (blank = make one up)", cfg.pool_password,
                               key="password") or secrets.token_urlsafe(9)
    cfg.pool_dir = _ask_folder(io, cfg.pool_dir)
    cfg.web_port = _ask_port(io, cfg.web_port, port_status)

    io.say("\n2/6  Classes (the folders lectures get sorted into)")
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

    io.say("\n3/6  AI summaries and sorting (Ollama, runs on this computer)")
    models = list_models(cfg.ollama_host)
    if models is None and ollama_installed():
        io.say("Ollama is installed but not running. Starting it...")
        if start_ollama(cfg.ollama_host):
            models = list_models(cfg.ollama_host)
    for _ in range(3):
        if models is not None:
            break
        io.say(f"Ollama isn't answering at {cfg.ollama_host}. Install it from https://ollama.com and open it.")
        if not io.confirm("Check again? (no = continue without AI; folder and title rules still sort)",
                          not io.assume_defaults, key="retry_ollama"):
            break
        start_ollama(cfg.ollama_host)
        models = list_models(cfg.ollama_host)
    if models is None:
        cfg.ollama_enabled = False
        io.say("Continuing without AI. Turn it on later in the web UI under Settings.")
    else:
        cfg.ollama_enabled = True
        names = [m["name"] for m in models]
        rec = ollama.recommended_model(ram_gb())
        if models:
            io.say("Installed models:")
            for i, m in enumerate(models, 1):
                io.say(f"  {i}) {m['name']:<28} {ollama.size_label(m)}")
        else:
            io.say(f"No models installed yet. For this computer: {rec}")
        io.say("Summaries are written from transcripts. Granola only shares transcripts from paid plans;")
        io.say("lectures from free accounts keep Granola's own summary.")
        default = cfg.effective_summary_model if not fresh else ollama.pick_default_model(names, rec)
        summary = _choose_model(io, "Model that writes summaries (number or name)", default, names, "summary_model")
        sort_default = summary if fresh or cfg.ollama_model == cfg.effective_summary_model else cfg.ollama_model
        sort = _choose_model(io, "Model that sorts lectures into classes (the same one is fastest)", sort_default,
                             names, "sort_model")
        cfg.summary_model, cfg.ollama_model = summary, sort
        for model in dict.fromkeys([summary, sort]):
            if ollama.has_model(names, model):
                continue
            if io.confirm(f"Download {model} now? (can be several GB)", True, key="pull"):
                if not pull_model(model):
                    io.say(f"  Download failed. Run `ollama pull {model}` later; lectures wait in the queue until then.")
            else:
                io.say(f"  Run `ollama pull {model}` before the first lecture arrives.")

    io.say("\n4/6  Granola on this computer (optional)")
    io.say("Usually your laptop sends lectures here. If Granola is signed in on this computer too, it can pull them itself.")
    cfg.server_sync = io.confirm("Also sync a Granola account on this computer?", cfg.server_sync, key="server_sync")
    save_config(cfg)
    if cfg.server_sync and not logged_in(cfg):
        io.say("Sign in to Granola in the browser window that opens.")
        if not _try(io, "Signing in", do_login, cfg):
            io.say("  Skipped. Run `granola-share login` later.")

    io.say("\n5/6  Keep it running")
    cfg.auto_update = io.confirm("Install new versions automatically?", cfg.auto_update, key="auto_update")
    save_config(cfg)
    started = healthy = False
    if io.confirm("Start at login and keep running in the background?", True, key="autostart"):
        path = install_autostart("server", home)
        io.say(f"Installed: {path}")
        started = True
        io.say("Waiting for it to come up...")
        healthy = wait_healthy(cfg)
        io.say("  It's up." if healthy else
               f"  It hasn't answered yet. Run `granola-share doctor` to see why (log: {cfg.log_dir / 'server.log'}).")
        cleanup(home, io.say)

    ts = tailscale()
    urls = hostinfo.server_urls(cfg.web_port, ts)
    io.say("\n6/6  Connect your laptop\n")
    if not ts.get("running"):
        io.say("  Tailscale isn't running here, so your laptop can only reach this computer on the same Wi-Fi.")
        io.say("  Install it on both (https://tailscale.com/download) and sign in to the same account.\n")
    io.say(f"  Library:   {cfg.pool_name}")
    io.say(f"  Address:   {urls[0]}" + (f"   (or {urls[1]})" if len(urls) > 1 else ""))
    io.say(f"  Password:  {cfg.pool_password}\n")
    cmds = hostinfo.invite_commands(urls[0], cfg.pool_password)
    io.say("On the computer you record lectures on, paste this one line. It installs everything with the")
    io.say("address and password filled in, then finishes setup in the browser:")
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
        return "  Double-check the password from your Mac mini's setup (it's also in that computer's config.toml)."
    return ("  Check that Tailscale is on and signed in on both computers, that the Mac mini is awake, "
            "and that the address is right.")


def client_setup(home: Path, io: Prompter | None = None, *, check_server=None, do_login=client_login,
                 is_logged_in=logged_in, install_autostart=autostart.install, share_now=client_share_now,
                 service_status=autostart.status, cleanup=update.cleanup_legacy, mac: bool | None = None) -> ClientConfig:
    from .client import check_server as _check

    check_server = check_server or _check
    io = io or Prompter()
    cc = load_client_config(home)
    io.say(f"\n== granola-share {__version__}: send your Granola lectures to your library ==\n")

    io.say("1/4  Your library (the address and password from your Mac mini's setup)")
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
        io.say("Granola's API only shares transcripts on paid plans. On a Mac, granola-share can copy each")
        io.say("transcript from the Granola window instead: while Granola is in front with its transcript open, it")
        io.say("clicks Copy transcript for you, then puts your clipboard and cursor back.")
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
