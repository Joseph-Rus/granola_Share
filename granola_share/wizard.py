"""Guided setup: `granola-share setup` (the pool server) and `granola-share client setup` (a friend)."""

from __future__ import annotations

import asyncio
import getpass
import json
import secrets
import shutil
import socket
import subprocess
from datetime import date, timedelta
from pathlib import Path

import httpx

from . import autostart
from .config import ClassDef, ClientConfig, Config, load_client_config, load_config, save_client_config, save_config

REPO = "https://github.com/Joseph-Rus/granola_Share"
RAW = "https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main"


class Prompter:
    """Terminal prompts. Tests swap in a scripted one."""

    def say(self, text: str = "") -> None:
        print(text)

    def ask(self, text: str, default: str | None = None) -> str:
        suffix = f" [{default}]" if default not in (None, "") else ""
        ans = input(f"{text}{suffix}: ").strip()
        return ans or (default or "")

    def confirm(self, text: str, default: bool = True) -> bool:
        hint = "Y/n" if default else "y/N"
        ans = input(f"{text} ({hint}): ").strip().lower()
        if not ans:
            return default
        return ans in ("y", "yes")

    def secret(self, text: str) -> str:
        return getpass.getpass(f"{text}: ").strip()


class ScriptedPrompter(Prompter):
    def __init__(self, answers: list):
        self.answers = list(answers)
        self.output: list[str] = []

    def say(self, text: str = "") -> None:
        self.output.append(text)

    def _next(self, text):
        if not self.answers:
            raise AssertionError(f"no scripted answer for: {text}")
        return self.answers.pop(0)

    def ask(self, text, default=None):
        a = self._next(text)
        return a if a != "" else (default or "")

    def confirm(self, text, default=True):
        return bool(self._next(text))

    def secret(self, text):
        return self._next(text)


# --- helpers the wizards call (all injectable for tests) ----------------------

def detect_hostname() -> str:
    for exe in (shutil.which("tailscale"), "/Applications/Tailscale.app/Contents/MacOS/Tailscale"):
        if exe and Path(exe).exists():
            try:
                out = subprocess.run([exe, "status", "--json"], capture_output=True, text=True, timeout=10).stdout
                name = json.loads(out)["Self"]["DNSName"].rstrip(".")
                if name:
                    return name
            except Exception:
                pass
    return socket.gethostname()


# Best first. MoE "a3b" models are fast (3B active) with big-model judgment; 64 GB Macs run the 35B fine.
MODEL_PREFERENCE = ["qwen3.6:35b", "qwen3.6", "qwen3.8", "qwen3:30b", "gemma4", "qwen3", "gemma3", "llama3"]


def pick_default_model(installed: list[str], fallback: str) -> str:
    for pref in MODEL_PREFERENCE:
        for m in installed:
            if m.lower().startswith(pref):
                return m
    return fallback


def ollama_models(host: str) -> list[str] | None:
    try:
        r = httpx.get(host.rstrip("/") + "/api/tags", timeout=5)
        r.raise_for_status()
        return [m["name"] for m in r.json().get("models", [])]
    except Exception:
        return None


def pull_model(model: str) -> bool:
    exe = shutil.which("ollama")
    if not exe:
        return False
    return subprocess.run([exe, "pull", model]).returncode == 0


def server_login(cfg: Config) -> None:
    from .oauth import GranolaOAuth

    GranolaOAuth(cfg).login()


def client_login(cc: ClientConfig) -> None:
    from .oauth import GranolaOAuth

    GranolaOAuth(cc).login()


ACCOUNT_LOOKBACK_DAYS = 30
MAX_LOGIN_ROUNDS = 3


def granola_account_check(cfg: Config | ClientConfig) -> tuple[dict | None, int]:
    """Who is signed in, and how many notes they have in the last 30 days.

    Returns (account info or None, note count). Never raises: a failure counts as
    "not signed in / 0 notes" so the wizard can say so instead of crashing.
    """
    from .granola import GranolaClient
    from .oauth import GranolaOAuth

    client = GranolaClient(cfg, GranolaOAuth(cfg))
    since = date.today() - timedelta(days=ACCOUNT_LOOKBACK_DAYS)

    async def go():
        async with client.session() as s:
            get_info = getattr(client, "get_account_info", None)
            info = await get_info(s) if get_info else None
            return info, len(await client.list_meetings(s, since=since))

    try:
        return asyncio.run(go())
    except Exception:
        return None, 0


def account_line(info: dict | None, n: int) -> str:
    """'Signed in as a@b.c (Joey's workspace) · found 12 notes in the last 30 days'."""
    if not info or not info.get("email"):
        who = "Signed in (Granola did not say which account)"
    else:
        ws = info.get("workspace")
        who = f"Signed in as {info['email']}" + (f" ({ws})" if ws else "")
    return f"{who} · found {n} note{'' if n == 1 else 's'} in the last {ACCOUNT_LOOKBACK_DAYS} days"


ZERO_NOTES_MSG = ("That account has 0 notes. If your lectures live in a different Google account, "
                  "sign in again with that one.")


def login_and_check(cfg, io: Prompter, do_login, account_check, *, max_rounds: int = MAX_LOGIN_ROUNDS) -> tuple[dict | None, int]:
    """Sign in, report who that is and how many notes they have, and offer to switch accounts
    when the answer is 0 (the quiet failure this week: wrong account → silent empty pool)."""
    info, n = None, 0
    for round_no in range(1, max_rounds + 1):
        do_login(cfg)
        info, n = account_check(cfg)
        io.say("  " + account_line(info, n))
        if n > 0:
            break
        io.say("  " + ZERO_NOTES_MSG)
        if round_no == max_rounds or not io.confirm("Sign in as someone else?", True):
            break
    return info, n


def client_share_now(cc: ClientConfig, log=print) -> None:
    from .client import ShareClient
    from .granola import GranolaClient
    from .oauth import GranolaOAuth

    client = ShareClient(cc, GranolaClient(cc, GranolaOAuth(cc)), log=log)
    rep = asyncio.run(client.poll_once())
    queued = len(getattr(rep, "queued", []) or [])
    parts = [f"shared {len(rep.shared)}", f"skipped {len(rep.skipped)}"]
    if queued:
        parts.append(f"queued {queued} in the control panel")
    parts += [f"waiting {len(rep.pending)}", f"errors {len(rep.errors)}"]
    log(", ".join(parts))


def _normalize_url(url: str) -> str:
    url = url.strip().rstrip("/")
    if url and "://" not in url:
        url = "http://" + url
    return url


def resolve_pool_dir(answer: str) -> Path:
    """Absolute pool folder: `~` expands, a relative answer lands under the home directory."""
    p = Path(answer.strip() or "~/GranolaShare").expanduser()
    if not p.is_absolute():
        p = Path.home() / p
    return p.resolve()


# --- the server wizard --------------------------------------------------------

def server_setup(home: Path, io: Prompter | None = None, *, ollama_models=ollama_models, pull_model=pull_model,
                 do_login=server_login, install_autostart=autostart.install, hostname=detect_hostname,
                 account_check=granola_account_check) -> Config:
    io = io or Prompter()
    cfg = load_config(home)
    io.say("\n== granola-share: set up your notes pool ==\n")
    io.say("This machine will collect everyone's notes, sort them by class, and serve them to friends.\n")

    io.say("1/6  The pool")
    cfg.pool_name = io.ask("Name for the pool", cfg.pool_name)
    pw = io.ask("Pool password friends will use (blank = generate one)", cfg.pool_password or "")
    cfg.pool_password = pw or secrets.token_urlsafe(9)
    cfg.pool_dir = resolve_pool_dir(io.ask("Folder where notes are stored", str(cfg.pool_dir)))
    io.say(f"  Notes will be stored in {cfg.pool_dir}")
    cfg.web_port = int(io.ask("Web port", str(cfg.web_port)))

    io.say("\n2/6  Classes (the folders notes get sorted into). Blank name to finish.")
    if cfg.classes:
        io.say("Already configured: " + ", ".join(cfg.class_names()))
        if not io.confirm("Keep them?", True):
            cfg.classes = []
    while True:
        name = io.ask("Class name (e.g. CS 101)", "")
        if not name:
            break
        aliases = [a.strip() for a in io.ask("  short names/aliases, comma separated (e.g. cs101, intro programming)", "").split(",") if a.strip()]
        desc = io.ask("  one-line description (helps the AI)", "")
        cfg.classes.append(ClassDef(name, aliases, desc))
    if not cfg.classes:
        io.say("No classes yet: everything will land in Unsorted until you add some to config.toml.")

    io.say("\n3/6  AI sorting (Ollama, runs locally)")
    models = ollama_models(cfg.ollama_host)
    if models is None:
        io.say(f"Ollama is not running at {cfg.ollama_host}. Install it from https://ollama.com (or `brew install ollama`).")
        cfg.ollama_enabled = not io.confirm("Continue without AI sorting for now? (folder/title rules still work)", True)
    else:
        io.say("Installed models: " + (", ".join(models) or "none"))
        default = pick_default_model(models, cfg.ollama_model)
        cfg.ollama_model = io.ask("Model to use for sorting", default)
        cfg.ollama_enabled = True
        if cfg.ollama_model not in models and io.confirm(f"Pull {cfg.ollama_model} now? (a few GB)", True):
            if not pull_model(cfg.ollama_model):
                io.say("Pull failed; you can run `ollama pull` later. Sorting falls back to rules until then.")

    io.say("\n4/6  This server's own Granola account")
    io.say("Friends' laptops push their own notes. Optionally this server can also pull notes from a Granola account it logs into.")
    cfg.server_sync = io.confirm("Also sync a Granola account on this server?", cfg.server_sync)
    save_config(cfg)
    if cfg.server_sync:
        io.say("Sign in to Granola in the browser that opens.")
        login_and_check(cfg, io, do_login, account_check)

    io.say("\n5/6  Keep it running")
    started = False
    if io.confirm("Start at login and keep running in the background?", True):
        path = install_autostart("server", home)
        io.say(f"Installed: {path}")
        started = True

    host = hostname()
    io.say("\n6/6  Done. Share this with your friends:\n")
    io.say(f"  Pool:      {cfg.pool_name}")
    io.say(f"  Web UI:    http://{host}:{cfg.web_port}")
    io.say(f"  Password:  {cfg.pool_password}\n")
    io.say("They install the client with one command and enter that URL + password when asked:")
    io.say(f"  Mac/Linux: curl -fsSL {RAW}/install.sh | sh")
    io.say(f"  Windows:   irm {RAW}/install.ps1 | iex\n")
    io.say(f"Config saved to {cfg.config_path}.")
    if not started:
        io.say("Start the server with:  granola-share run")
    return cfg


# --- the friend wizard --------------------------------------------------------

def client_setup(home: Path, io: Prompter | None = None, *, check_server=None, do_login=client_login,
                 install_autostart=autostart.install, share_now=client_share_now,
                 account_check=granola_account_check) -> ClientConfig:
    from .client import check_server as _check

    check_server = check_server or _check
    io = io or Prompter()
    cc = load_client_config(home)
    io.say("\n== granola-share: connect your Granola notes to the pool ==\n")

    io.say("1/5  The pool server (ask the pool owner for these)")
    info = None
    for attempt in range(3):
        cc.server_url = _normalize_url(io.ask("Server address (e.g. http://your-mac-mini:8787)", cc.server_url))
        cc.pool_key = io.ask("Pool password", cc.pool_key)
        try:
            info = check_server(cc.server_url, cc.pool_key)
            break
        except Exception as e:
            io.say(f"  Could not connect: {e}")
            if attempt == 2:
                raise SystemExit("Fix the address/password and run `granola-share client setup` again.")
    cc.pool_name = str(info.get("pool_name") or "the notes pool")
    classes = info.get("classes") or []
    io.say(f"  Connected to '{cc.pool_name}'" + (f" (classes: {', '.join(classes)})" if classes else ""))

    io.say("\n2/5  You")
    cc.display_name = io.ask("Your name (shown as the source of your notes)", cc.display_name or getpass.getuser())

    io.say("\n3/5  Granola")
    io.say("A browser window will open; sign in to your Granola account. Nothing is shared until you say so.")
    login_and_check(cc, io, do_login, account_check)

    io.say("\n4/5  Sharing")
    ask_each = io.confirm("Review each finished note in the control panel before it is shared? (No = share everything automatically)",
                          cc.mode != "auto")
    cc.mode = "ask" if ask_each else "auto"
    save_client_config(cc)

    if io.confirm(f"Check for notes from the last {cc.share_lookback_days} days now?", True):
        share_now(cc, log=io.say)

    io.say("\n5/5  Keep it running")
    if io.confirm("Start at login and keep watching for new notes in the background?", True):
        path = install_autostart("client", home)
        io.say(f"Installed: {path}")
    else:
        io.say("Run `granola-share client run` whenever you want it watching.")
    io.say(f"\nDone. Config saved to {cc.config_path}.")
    io.say(f"Control panel: {cc.panel_url}  (open it while `granola-share client run` is watching;")
    io.say("it shows who is signed in, what is waiting for your Share/Skip, and what was shared)")
    return cc
