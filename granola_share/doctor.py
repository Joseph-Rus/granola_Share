"""`granola-share doctor`: check every piece of a setup and say how to fix what is broken."""

from __future__ import annotations

import asyncio
import platform
import sys
from dataclasses import dataclass
from pathlib import Path

import httpx

from . import __version__, autostart, hostinfo, ollama, ready, update
from .config import ClientConfig, Config
from .ready import mac_sleep_minutes  # noqa: F401  (kept importable from here)

OK, WARN, FAIL = "ok", "warn", "fail"


@dataclass
class Check:
    name: str
    state: str  # ok | warn | fail
    detail: str
    fix: str = ""


def _log_tail(path: Path, n: int = 6) -> str:
    try:
        lines = [ln for ln in path.read_text(errors="replace").splitlines() if ln.strip()]
    except OSError:
        return ""
    return "\n".join(lines[-n:])


def _version_check(latest=update.cached_latest) -> Check:
    rel = latest()
    if update.is_newer(rel):
        return Check("Version", WARN, f"{__version__} installed, {rel.tag} is out", "run `granola-share update`")
    return Check("Version", OK, f"{__version__}" + (" (newest)" if rel else ""))


# --- the library (the Mac mini) -------------------------------------------------------

def server_checks(cfg: Config, *, http_get=httpx.get, list_models=ollama.list_models, service_status=autostart.status,
                  tailscale=hostinfo.tailscale_info, sleep_minutes=None, latest=update.cached_latest,
                  ollama_installed=ollama.installed, firewall=ready.firewall_open,
                  system: str | None = None) -> list[Check]:
    system = system or platform.system()
    out: list[Check] = []
    if not cfg.config_path.exists():
        return [Check("Config", FAIL, f"no {cfg.config_path}", "run `granola-share setup`")]
    out.append(Check("Config", OK if cfg.pool_password else WARN,
                     f"{cfg.config_path}, {len(cfg.classes)} class{'es' if len(cfg.classes) != 1 else ''}",
                     "" if cfg.pool_password else "no pool password: anyone who can reach the server can read and add notes"))
    if not cfg.classes:
        out.append(Check("Classes", WARN, "none yet, so everything lands in Unsorted",
                         "add them in the web UI under Settings, or rerun `granola-share setup`"))

    try:
        cfg.pool_dir.mkdir(parents=True, exist_ok=True)
        probe = cfg.pool_dir / ".write-test"
        probe.write_text("ok")
        probe.unlink()
        out.append(Check("Notes folder", OK, str(cfg.pool_dir)))
    except OSError as e:
        out.append(Check("Notes folder", FAIL, f"can't write to {cfg.pool_dir}: {e}", "pick another folder in config.toml"))

    svc = service_status("server")
    log = cfg.log_dir / "server.log"
    try:
        r = http_get(f"http://127.0.0.1:{cfg.web_port}/api/health",
                     headers={"Authorization": f"Bearer {cfg.pool_password}"}, timeout=5)
        if r.status_code == 200:
            info = r.json()
            ver = info.get("version") or "an old version"
            stale = ver != __version__
            out.append(Check("Web server", WARN if stale else OK, f"answering on port {cfg.web_port} (running {ver})",
                             "restart it to load this version: `granola-share autostart install --role server`" if stale else ""))
        else:
            out.append(Check("Web server", FAIL, f"port {cfg.web_port} answered {r.status_code}",
                             "another app may be using the port; change web_port in config.toml"))
    except Exception:
        tail = _log_tail(log)
        fix = ("it is installed but not answering; recent log:\n" + tail) if svc != "missing" and tail else \
            "start it: `granola-share autostart install --role server` (or `granola-share run` to watch it)"
        out.append(Check("Web server", FAIL, f"nothing answers on port {cfg.web_port}", fix))

    out.append({
        "running": Check("Background service", OK, "starts at login and restarts if it stops"),
        "stopped": Check("Background service", FAIL, "installed but not running",
                         f"`granola-share autostart install --role server`, then check {log}"),
        "missing": Check("Background service", WARN, "not installed, so the library stops when you log out or reboot",
                         "`granola-share autostart install --role server`"),
    }[svc])

    if cfg.ollama_enabled:
        models = list_models(cfg.ollama_host)
        if models is None and ollama_installed():
            out.append(Check("Ollama", FAIL, f"installed, but not answering at {cfg.ollama_host}; study notes and AI sorting "
                             "are paused", {"Darwin": "open the Ollama app (`open -a Ollama`)",
                                            "Windows": "open Ollama from the Start menu"}.get(system, "run `ollama serve`")))
        elif models is None:
            out.append(Check("Ollama", FAIL, "not installed, so study notes and AI sorting are paused",
                             "rerun `granola-share setup`: it installs Ollama for you (or get it from https://ollama.com)"))
        else:
            names = [m["name"] for m in models]
            for label, model in (("Summary model", cfg.effective_summary_model), ("Sorting model", cfg.ollama_model)):
                if label == "Summary model" and not cfg.summary_enabled:
                    continue
                if ollama.has_model(names, model):
                    out.append(Check(label, OK, model))
                else:
                    out.append(Check(label, FAIL, f"{model} is not installed",
                                     f"`ollama pull {model}`, or pick an installed one in Settings"))
    else:
        out.append(Check("Ollama", WARN, "AI is off: only folder and title rules sort lectures, and no summaries are written",
                         "turn it on in Settings"))

    ts = tailscale()
    problem = hostinfo.tailscale_problem(ts)
    if not problem:
        out.append(Check("Tailscale", OK, "your laptop can reach " + hostinfo.server_urls(cfg.web_port, ts)[0]))
    elif not ts.get("installed"):
        out.append(Check("Tailscale", WARN, "not installed; your laptop can only reach this computer on the same Wi-Fi",
                         "rerun `granola-share setup` to install it, or get it from https://tailscale.com/download; "
                         "sign in on both computers with one account"))
    else:
        out.append(Check("Tailscale", WARN, problem + "; your laptop can only reach this computer on the same Wi-Fi",
                         "open Tailscale and sign in (same account as your laptop), or rerun `granola-share setup`"))

    if system == "Windows" and firewall(cfg.web_port) is False:
        out.append(Check("Firewall", WARN, f"Windows Firewall has no rule for port {cfg.web_port}, so it may block your laptop",
                         "rerun `granola-share setup` and let it add the rule (Windows asks for permission)"))

    if system in ready.SLEEP_FIX:
        mins = (sleep_minutes or (lambda: ready.sleep_minutes(system)))()
        if mins:
            out.append(Check("Sleep", WARN, f"this computer sleeps after {mins} min idle, and the library goes offline with it",
                             ready.SLEEP_FIX[system]))

    if cfg.server_sync and not cfg.tokens_path.exists():
        out.append(Check("Server's Granola", FAIL, "server_sync is on but this server isn't signed in",
                         "`granola-share login`"))
    out.append(_version_check(latest))
    return out


# --- your laptop ---------------------------------------------------------------------

def granola_probe(cc: ClientConfig) -> tuple[int, bool | None]:
    """(notes in the last week, transcripts available?) from the user's own Granola account."""
    from datetime import date, timedelta

    from .granola import GranolaClient, build_args
    from .oauth import GranolaOAuth

    g = GranolaClient(cc, GranolaOAuth(cc))

    async def go():
        async with g.session() as s:
            stubs = await g.list_meetings(s, since=date.today() - timedelta(days=7))
            tools = await g.tools(s)
            name = g._find_tool(tools, "get_meeting_transcript", "get_transcript")
            if not stubs or not name:
                return len(stubs), None
            try:
                await g.call(s, name, build_args(tools[name]["schema"], {"id": stubs[0].id}))
                return len(stubs), True
            except Exception as e:
                return len(stubs), False if "paid" in str(e).lower() else None

    return asyncio.run(go())


def copy_check(cc: ClientConfig, transcripts_via_api: bool | None, system: str | None = None) -> Check | None:
    """How copying transcripts from the Granola window is going (the watcher writes transcripts/status.json)."""
    import json

    if (system or platform.system()) != "Darwin":
        return None
    if not cc.copy_transcripts:
        if transcripts_via_api is False:
            return Check("Copy transcripts", OK, "off (the default), so lectures keep Granola's summary",
                         "optional: turn it on in Study Stash, under Sending (read the note there first)")
        return None
    try:
        status = json.loads((cc.home / "transcripts" / "status.json").read_text())
    except (OSError, ValueError):
        return Check("Copy transcripts", WARN, "the background watcher hasn't checked yet",
                     "it starts with the watcher; run `granola-share autostart install --role client` if that isn't running")
    if not status.get("trusted"):
        return Check("Copy transcripts", FAIL, "macOS hasn't allowed Accessibility access yet",
                     "System Settings → Privacy & Security → Accessibility: turn on python3.12, then "
                     "`granola-share autostart install --role client`")
    last = status.get("last_copy")
    if last:
        return Check("Copy transcripts", OK, f"last copied '{last.get('title')}' ({last.get('chars')} chars) at {last.get('at')}")
    return Check("Copy transcripts", OK, "allowed; a transcript is copied when you have it open in Granola")


def laptop_app_checks(checks: dict) -> list[Check]:
    """The Granola app (it records the lectures) and Tailscale, from ready.laptop_checks()."""
    out = []
    if checks["granola_here"]:
        out.append(Check("Granola app", OK, checks["granola"]) if checks["granola"] else
                   Check("Granola app", FAIL, "not installed on this computer, so there's nothing to record lectures with",
                         f"get it from {ready.GRANOLA_DOWNLOAD}"))
    ts = checks["tailscale"]
    problem = hostinfo.tailscale_problem(ts)
    if not problem:
        out.append(Check("Tailscale", OK, "connected" + (f" as {ts['dns']}" if ts.get("dns") else "")))
    else:
        out.append(Check("Tailscale", WARN, problem + "; this computer reaches the library only on the same Wi-Fi",
                         "open Tailscale and sign in with the same account as your library's computer" if ts.get("installed")
                         else f"install it from {ready.TAILSCALE_DOWNLOAD} (or with the button in Study Stash)"))
    return out


def client_checks(cc: ClientConfig, *, check_server=None, probe=granola_probe, service_status=autostart.status,
                  latest=update.cached_latest, system: str | None = None, laptop=None) -> list[Check]:
    from .client import check_server as _check_server

    check_server = check_server or _check_server
    if not cc.config_path.exists() or not cc.server_url:
        return [Check("Config", FAIL, f"no {cc.config_path}", "run `granola-share client setup`")]
    out = [Check("Config", OK, f"{cc.config_path}, mode: {'ask each time' if cc.mode == 'ask' else 'share everything'}"),
           *laptop_app_checks((laptop or ready.laptop_checks)())]
    try:
        info = check_server(cc.server_url, cc.pool_key)
        out.append(Check("Library", OK, f"'{info.get('pool_name')}' at {cc.server_url}"))
    except Exception as e:
        out.append(Check("Library", FAIL, f"{cc.server_url}: {e}",
                         "is Tailscale on on both computers, and is the library's computer awake? "
                         "Wrong address or password: `granola-share client setup`"))
    transcripts = None
    if not cc.tokens_path.exists():
        out.append(Check("Granola", FAIL, "not signed in", "`granola-share client login`"))
    else:
        try:
            n, transcripts = probe(cc)
            out.append(Check("Granola", OK, f"signed in, {n} note{'s' if n != 1 else ''} in the last 7 days"))
            copying = cc.copy_transcripts and (system or platform.system()) == "Darwin"
            if transcripts is False and copying:
                out.append(Check("Transcripts", OK, "your Granola plan doesn't share them through its API, so they're "
                                 "copied from the Granola app instead"))
            elif transcripts is False:
                out.append(Check("Transcripts", WARN, "your Granola plan doesn't share transcripts, so the library "
                                 "keeps Granola's summary for your lectures", "a paid Granola plan unlocks them"))
            elif transcripts:
                out.append(Check("Transcripts", OK, "shared, so the library writes its own summaries"))
        except Exception as e:
            out.append(Check("Granola", FAIL, f"signed in, but Granola refused: {str(e)[:200]}",
                             "sign in again: `granola-share client login`"))
    if (c := copy_check(cc, transcripts, system)) is not None:
        out.append(c)
    svc = service_status("client")
    log = cc.log_dir / "client.log"
    out.append({
        "running": Check("Background watcher", OK, "checks for finished notes every few minutes"),
        "stopped": Check("Background watcher", FAIL, "installed but not running",
                         f"`granola-share autostart install --role client`; recent log:\n{_log_tail(log)}"),
        "missing": Check("Background watcher", WARN, "not installed, so notes are only shared while you run it",
                         "`granola-share autostart install --role client`"),
    }[svc])
    out.append(_version_check(latest))
    return out


# --- output -------------------------------------------------------------------------------

def _marks() -> dict[str, str]:
    enc = (getattr(sys.stdout, "encoding", None) or "ascii").lower()
    try:
        "✓✗!→".encode(enc)
        return {OK: "✓", WARN: "!", FAIL: "✗", "arrow": "→"}
    except (UnicodeEncodeError, LookupError):
        return {OK: "ok", WARN: "!", FAIL: "X", "arrow": "->"}


def format_checks(title: str, checks: list[Check]) -> str:
    mk = _marks()
    width = max((len(c.name) for c in checks), default=0)
    lines = [title, ""]
    for c in checks:
        lines.append(f"  {mk[c.state]:>2} {c.name.ljust(width)}  {c.detail}")
        if c.fix and c.state != OK:
            pad = " " * (width + 7)
            fix = c.fix.replace("\n", "\n" + pad + "  ")
            lines.append(f"{pad}{mk['arrow']} {fix}")
    return "\n".join(lines)


def run(home: Path, role: str | None = None, print_fn=print) -> int:
    """Print the checks for this machine's role(s). Returns 1 if anything failed."""
    from .config import load_client_config, load_config

    roles = [role] if role else [r for r, f in (("server", "config.toml"), ("client", "client.toml"))
                                 if (home / f).exists()]
    if not roles:
        print_fn(f"Nothing is set up in {home} yet.\n"
                 "  The computer that keeps the library:  granola-share setup\n"
                 "  Your laptop (records in Granola):     granola-share client open")
        return 1
    failed = False
    for r in roles:
        checks = server_checks(load_config(home)) if r == "server" else client_checks(load_client_config(home))
        failed |= any(c.state == FAIL for c in checks)
        print_fn(format_checks(f"granola-share {__version__}: {'library' if r == 'server' else 'laptop'} "
                               f"({home})", checks) + "\n")
    print_fn(f"Fix the {_marks()[FAIL]} items above, then run `granola-share doctor` again." if failed else "All good.")
    return 1 if failed else 0
