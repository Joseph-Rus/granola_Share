"""granola-share command line.

The Mac mini (keeps the library):   setup | run | doctor | update | login | sync | tools
Your laptop (records in Granola):   client open | client setup | client run | client once | client login | doctor | update
Both:         autostart install|uninstall --role server|client
"""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
import threading
from pathlib import Path

from . import __version__
from .config import DEFAULT_HOME, load_client_config, load_config, write_example_config


def _server_client(cfg):
    from .granola import GranolaClient
    from .oauth import GranolaOAuth

    return GranolaClient(cfg, GranolaOAuth(cfg))


def _store(cfg):
    from .store import Store

    return Store(cfg.db_path, cfg.pool_dir)


def _start_auto_update(home: Path, enabled, stop: threading.Event) -> None:
    from .update import start_auto_update

    start_auto_update(home, enabled, stop, log=lambda s: print(s, flush=True))


# --- server commands ------------------------------------------------------------

def cmd_setup(args):
    from .wizard import make_prompter, server_setup

    server_setup(args.home, make_prompter(args))


def cmd_init(args):
    path = write_example_config(args.home)
    print(f"Config: {path}\nEdit it, or run `granola-share setup` for the guided version.")


def cmd_login(args):
    from .oauth import GranolaOAuth

    GranolaOAuth(load_config(args.home)).login(open_browser=not args.no_browser)


def cmd_logout(args):
    from .oauth import GranolaOAuth

    GranolaOAuth(load_config(args.home)).logout()
    print("Logged out.")


def cmd_tools(args):
    cfg = load_config(args.home)
    client = _server_client(cfg)

    async def go():
        async with client.session() as s:
            tools = await client.tools(s)
            print(json.dumps(tools, indent=2))
            if args.probe:
                if "get_account_info" in tools:
                    print("\n== get_account_info ==")
                    print(json.dumps(await client.call(s, "get_account_info", {}), indent=2)[:3000])
                stubs = await client.list_meetings(s)
                print(f"\n== list_meetings: {len(stubs)} meetings ==")
                for m in stubs[:5]:
                    print(f"- {m.id}  {m.date[:10]}  {m.title}  folder={m.folder!r}  notes={len(m.notes_markdown)} chars")
                if stubs:
                    full = await client.get_meetings(s, [stubs[0].id])
                    if full:
                        print(f"\n== get_meetings({stubs[0].id}) keys: {sorted(full[0].raw)} ==")
                        print(full[0].notes_markdown[:800])
                    t = await client.get_transcript(s, stubs[0].id)
                    print(f"\n== transcript: {len(t)} chars ==" + ("" if t else " (none: free Granola plans don't share transcripts)"))

    asyncio.run(go())


def cmd_sync(args):
    from .pipeline import Pipeline
    from .sync import run_loop, sync_once

    cfg = load_config(args.home)
    if args.no_ollama:
        cfg.ollama_enabled = False
    client, store = _server_client(cfg), _store(cfg)
    pipeline = Pipeline(cfg, store)
    if args.once:
        rep = asyncio.run(sync_once(cfg, client, store))
        print(f"listed={rep.listed} new={rep.new} queued={len(rep.queued)} errors={len(rep.errors)}")
        for e in rep.errors:
            print("  error:", e)
        print(f"filed {pipeline.run_pending()} note(s)")
    else:
        stop = threading.Event()
        pipeline.start(stop)
        run_loop(cfg, client, store, on_queued=pipeline.wake)


def _serve(cfg, store, pipeline):
    import uvicorn

    from .web import create_app

    uvicorn.run(create_app(cfg, store, pipeline), host=cfg.web_host, port=cfg.web_port, log_level="info")


def cmd_serve(args):
    from .pipeline import Pipeline

    cfg = load_config(args.home)
    store = _store(cfg)
    stop = threading.Event()
    pipeline = Pipeline(cfg, store, log=lambda s: print(s, flush=True))
    pipeline.start(stop)
    try:
        _serve(cfg, store, pipeline)
    finally:
        stop.set()


def cmd_run(args):
    import secrets

    from .config import save_config
    from .pipeline import Pipeline
    from .sync import run_loop

    cfg = load_config(args.home)
    if not cfg.config_path.exists():
        sys.exit(f"Not set up yet: run `granola-share setup` (no {cfg.config_path}).")
    if not cfg.admin_password:  # configs from 0.1 have none; Settings needs one
        cfg.admin_password = secrets.token_urlsafe(12)
        save_config(cfg)
        print(f"Created an admin password for the Settings page (see {cfg.config_path}).", flush=True)
    if args.no_ollama:
        cfg.ollama_enabled = False
    print(f"granola-share {__version__}: pool '{cfg.pool_name}' on port {cfg.web_port}", flush=True)
    store = _store(cfg)
    stop = threading.Event()
    pipeline = Pipeline(cfg, store, log=lambda s: print(s, flush=True))
    pipeline.start(stop)
    if cfg.server_sync:
        if not cfg.tokens_path.exists():
            print("server_sync is on but this server is not logged in to Granola; run `granola-share login`. "
                  "Continuing with the web UI only.", flush=True)
        else:
            threading.Thread(target=run_loop, args=(cfg, _server_client(cfg), _store(cfg)),
                             kwargs={"stop": stop, "on_queued": pipeline.wake}, daemon=True).start()
    _start_auto_update(args.home, lambda: load_config(args.home).auto_update, stop)
    try:
        _serve(cfg, store, pipeline)
    finally:
        stop.set()


# --- laptop commands -----------------------------------------------------------------

def _share_client(cc, log=print):
    from .client import ShareClient
    from .granola import GranolaClient
    from .oauth import GranolaOAuth
    from .transcript_grab import TranscriptStore

    store = TranscriptStore(cc.home) if cc.copy_transcripts else None
    return ShareClient(cc, GranolaClient(cc, GranolaOAuth(cc)), log=log, transcripts=store)



def cmd_client_setup(args):
    from . import launcher
    from .wizard import client_setup, make_prompter

    client_setup(args.home, make_prompter(args))
    launcher.install(args.home)  # the "Granola Share" app, to reopen status and settings without a terminal


def cmd_client_login(args):
    from .oauth import GranolaOAuth

    GranolaOAuth(load_client_config(args.home)).login(open_browser=not args.no_browser)


def cmd_client_run(args):
    """The background service: the watcher, plus the Granola Share page for setup and status."""
    from .client_app import ClientRuntime, serve

    log = lambda s: print(s, flush=True)  # noqa: E731
    runtime = ClientRuntime(args.home, log=log)
    cc = runtime.config()
    print(f"granola-share {__version__}: " + (f"watching Granola for '{cc.pool_name}'" if runtime.configured()
                                               else "waiting for setup in the Granola Share page"), flush=True)
    if runtime.configured():
        runtime.start_watching()
    elif args.no_ui:
        sys.exit("Not set up yet: open Granola Share, or run `granola-share client setup`.")
    try:
        if args.no_ui:
            runtime.thread.join()
        else:
            serve(runtime)
    finally:
        runtime.stop.set()


def cmd_client_open(args):
    """What the Granola Share icon and the installer run: start the service if needed, open its page."""
    import os

    from .client_app import open_app, write_prefill

    write_prefill(args.home, args.server or os.environ.get("GRANOLA_SHARE_SERVER"),
                  args.key or os.environ.get("GRANOLA_SHARE_KEY"))
    url = open_app(args.home, install=args.install)
    if not url:
        sys.exit(1)
    print("Granola Share is open in your browser." + (" Finish setting up there." if args.install else ""))
    print(f"If it didn't open, go to: {url}")


def cmd_client_once(args):
    cc = load_client_config(args.home)
    if not cc.server_url:
        sys.exit("Not set up yet: run `granola-share client setup`.")
    if args.auto:
        cc.mode = "auto"
    rep = asyncio.run(_share_client(cc).poll_once())
    print(f"listed={rep.listed} considered={rep.considered} shared={len(rep.shared)} skipped={len(rep.skipped)} "
          f"pending={len(rep.pending)} errors={len(rep.errors)}")
    for e in rep.errors:
        print("  error:", e)


# --- both ---------------------------------------------------------------------

def cmd_autostart(args):
    from . import autostart

    if args.action == "install":
        print("Installed:", autostart.install(args.role, args.home))
    elif args.action == "status":
        print(autostart.status(args.role))
    else:
        print("Removed." if autostart.uninstall(args.role) else "Nothing to remove.")


def cmd_doctor(args):
    from .doctor import run

    sys.exit(run(args.home, args.role))


def cmd_update(args):
    from . import update

    try:
        rel = update.latest_release()
    except Exception as e:
        sys.exit(f"Could not check for updates: {e}")
    if rel is None:
        sys.exit("No releases are published yet.")
    if not update.is_newer(rel) and not args.force:
        print(f"granola-share {__version__} is the newest version.")
        return
    print(f"granola-share {__version__} → {rel.tag}  ({rel.page})")
    if args.check:
        return
    if not update.apply(rel, args.home):
        sys.exit(1)


# --- parser --------------------------------------------------------------------

def _server_setup_flags(sp: argparse.ArgumentParser) -> None:
    g = sp.add_argument_group("answers (skip the matching question)")
    g.add_argument("--pool-name", dest="a_pool_name")
    g.add_argument("--password", dest="a_password", help="password your laptop and browser use")
    g.add_argument("--pool-dir", dest="a_pool_dir", help="folder the notes are written to")
    g.add_argument("--port", dest="a_port")
    g.add_argument("--class", dest="a_classes", action="append", metavar="NAME[=ALIAS,ALIAS]",
                   help="a class to sort into; repeat for each (replaces the current list)")
    g.add_argument("--summary-model", dest="a_summary_model", help="Ollama model that writes summaries")
    g.add_argument("--sort-model", dest="a_sort_model", help="Ollama model that sorts into classes")
    _bool(g, "pull", "download missing Ollama models")
    _bool(g, "server-sync", "also sync this computer's own Granola account")
    _bool(g, "auto-update", "install new versions automatically")
    _bool(g, "autostart", "start at login and keep running")
    sp.add_argument("--yes", "-y", action="store_true", help="accept the default for every question not answered by a flag")


def _client_setup_flags(sp: argparse.ArgumentParser) -> None:
    g = sp.add_argument_group("answers (skip the matching question)")
    g.add_argument("--server", dest="a_server", help="library address, e.g. http://mini.tailnet.ts.net:8787 "
                   "(default: $GRANOLA_SHARE_SERVER)")
    g.add_argument("--key", dest="a_key", help="library password (default: $GRANOLA_SHARE_KEY)")
    g.add_argument("--name", dest="a_name", help=argparse.SUPPRESS)  # 0.2 flag; the login name is used now
    g.add_argument("--mode", dest="a_ask_each", choices=["ask", "auto"],
                   help="auto = send every lecture (default), ask = Send/Skip popup for each")
    _bool(g, "keep-login", "keep an existing Granola sign-in")
    _bool(g, "share-now", "look at the last week's lectures right away")
    _bool(g, "copy-transcripts", "macOS: copy transcripts from the Granola window (for free Granola plans)")
    _bool(g, "auto-update", "install new versions automatically")
    _bool(g, "autostart", "start at login and keep watching")
    sp.add_argument("--yes", "-y", action="store_true", help="accept the default for every question not answered by a flag")


def _bool(g, name: str, help: str) -> None:
    dest = "a_" + name.replace("-", "_")
    g.add_argument(f"--{name}", dest=dest, action="store_const", const=True, help=help)
    g.add_argument(f"--no-{name}", dest=dest, action="store_const", const=False)


def main(argv=None):
    p = argparse.ArgumentParser(prog="granola-share", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--home", type=Path, default=DEFAULT_HOME, help=f"data directory (default {DEFAULT_HOME})")
    p.add_argument("--version", action="version", version=f"granola-share {__version__}")
    sub = p.add_subparsers(dest="cmd", required=True)

    sp = sub.add_parser("setup", help="guided setup for the computer that keeps your library (the Mac mini)")
    _server_setup_flags(sp)
    sp.set_defaults(fn=cmd_setup)
    sub.add_parser("init", help="write a starter config.toml without the wizard").set_defaults(fn=cmd_init)
    lp = sub.add_parser("login", help="sign the server in to a Granola account (for server_sync)")
    lp.add_argument("--no-browser", action="store_true")
    lp.set_defaults(fn=cmd_login)
    sub.add_parser("logout").set_defaults(fn=cmd_logout)
    tp = sub.add_parser("tools", help="print the MCP tools Granola exposes (debugging)")
    tp.add_argument("--probe", action="store_true", help="also list meetings and fetch one")
    tp.set_defaults(fn=cmd_tools)
    syp = sub.add_parser("sync", help="server-side pull from its own Granola account")
    syp.add_argument("--once", action="store_true")
    syp.add_argument("--no-ollama", action="store_true")
    syp.set_defaults(fn=cmd_sync)
    sub.add_parser("serve", help="web UI + ingest API + summaries, without the server's own sync").set_defaults(fn=cmd_serve)
    rp = sub.add_parser("run", help="what the server runs: web UI, ingest API, summaries (+ own sync if enabled)")
    rp.add_argument("--no-ollama", action="store_true")
    rp.set_defaults(fn=cmd_run)

    cp = sub.add_parser("client", help="your laptop: watch Granola and send finished lectures to the library")
    csub = cp.add_subparsers(dest="client_cmd", required=True)
    csp = csub.add_parser("setup", help="guided setup")
    _client_setup_flags(csp)
    csp.set_defaults(fn=cmd_client_setup)
    clp = csub.add_parser("login", help="sign in to Granola again")
    clp.add_argument("--no-browser", action="store_true")
    clp.set_defaults(fn=cmd_client_login)
    crp = csub.add_parser("run", help="the background service: watch Granola and serve the Granola Share page")
    crp.add_argument("--no-ui", action="store_true", help="only watch; no local page")
    crp.set_defaults(fn=cmd_client_run)
    cap = csub.add_parser("open", help="open the Granola Share page (setup and status), starting the service if needed")
    cap.add_argument("--install", action="store_true", help="also install the background service and the app icon")
    cap.add_argument("--server", help="library address to fill in (default: $GRANOLA_SHARE_SERVER)")
    cap.add_argument("--key", help="library password to fill in (default: $GRANOLA_SHARE_KEY)")
    cap.set_defaults(fn=cmd_client_open)
    cop = csub.add_parser("once", help="check once and exit")
    cop.add_argument("--auto", action="store_true", help="share without asking this time")
    cop.set_defaults(fn=cmd_client_once)

    dp = sub.add_parser("doctor", help="check this machine's setup and say how to fix what's broken")
    dp.add_argument("--role", choices=["server", "client"], help="default: whatever is set up here")
    dp.set_defaults(fn=cmd_doctor)
    up = sub.add_parser("update", help="install the newest release and restart the background service")
    up.add_argument("--check", action="store_true", help="only say whether there is a newer version")
    up.add_argument("--force", action="store_true", help="reinstall even if already up to date")
    up.set_defaults(fn=cmd_update)

    ap = sub.add_parser("autostart", help="install/uninstall the background service")
    ap.add_argument("action", choices=["install", "uninstall", "status"])
    ap.add_argument("--role", choices=["server", "client"], required=True)
    ap.set_defaults(fn=cmd_autostart)

    args = p.parse_args(argv)
    args.home = Path(args.home).expanduser()
    args.home.mkdir(parents=True, exist_ok=True)
    if getattr(args, "a_ask_each", None) is not None:
        args.a_ask_each = args.a_ask_each == "ask"
    try:
        args.fn(args)
    except KeyboardInterrupt:
        print("\nStopped.")
        sys.exit(130)


if __name__ == "__main__":
    main()
