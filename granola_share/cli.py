"""granola-share command line.

Server (the pool):   setup | run | serve | sync | login | logout | tools | init
Friend (a laptop):   client setup | client run | client once | client login
Both:                autostart install|uninstall --role server|client
"""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
import threading
from pathlib import Path

from .config import DEFAULT_HOME, load_client_config, load_config, write_example_config


def _server_client(cfg):
    from .granola import GranolaClient
    from .oauth import GranolaOAuth

    return GranolaClient(cfg, GranolaOAuth(cfg))


def _store(cfg):
    from .store import Store

    return Store(cfg.db_path, cfg.pool_dir)


# --- server commands ------------------------------------------------------------

def cmd_setup(args):
    from .wizard import server_setup

    server_setup(args.home)


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

    asyncio.run(go())


def cmd_sync(args):
    from .sync import run_loop, sync_once

    cfg = load_config(args.home)
    if args.no_ollama:
        cfg.ollama_enabled = False
    client, store = _server_client(cfg), _store(cfg)
    if args.once:
        rep = asyncio.run(sync_once(cfg, client, store))
        print(f"listed={rep.listed} new={rep.new} saved={len(rep.saved)} errors={len(rep.errors)}")
        for e in rep.errors:
            print("  error:", e)
    else:
        run_loop(cfg, client, store)


def _serve(cfg, store):
    import uvicorn

    from .web import create_app

    uvicorn.run(create_app(cfg, store), host=cfg.web_host, port=cfg.web_port, log_level="info")


def cmd_serve(args):
    cfg = load_config(args.home)
    _serve(cfg, _store(cfg))


def cmd_run(args):
    from .sync import run_loop

    cfg = load_config(args.home)
    if args.no_ollama:
        cfg.ollama_enabled = False
    stop = threading.Event()
    if cfg.server_sync:
        if not cfg.tokens_path.exists():
            print("server_sync is on but this server is not logged in to Granola; run `granola-share login`. Continuing with the web UI only.")
        else:
            t = threading.Thread(target=run_loop, args=(cfg, _server_client(cfg), _store(cfg)),
                                 kwargs={"stop": stop}, daemon=True)
            t.start()
    try:
        _serve(cfg, _store(cfg))
    finally:
        stop.set()


# --- friend client commands --------------------------------------------------------

def _share_client(cc, log=print):
    from .client import ShareClient
    from .granola import GranolaClient
    from .oauth import GranolaOAuth

    return ShareClient(cc, GranolaClient(cc, GranolaOAuth(cc)), log=log)


def cmd_client_setup(args):
    from .wizard import client_setup

    client_setup(args.home)


def cmd_client_login(args):
    from .oauth import GranolaOAuth

    GranolaOAuth(load_client_config(args.home)).login(open_browser=not args.no_browser)


def cmd_client_run(args):
    cc = load_client_config(args.home)
    if not cc.server_url:
        sys.exit("Not set up yet: run `granola-share client setup`.")
    _share_client(cc).run_loop()


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


# --- autostart -----------------------------------------------------------------

def cmd_autostart(args):
    from . import autostart

    if args.action == "install":
        print("Installed:", autostart.install(args.role, args.home))
    else:
        print("Removed." if autostart.uninstall(args.role) else "Nothing to remove.")


# --- parser --------------------------------------------------------------------

def main(argv=None):
    p = argparse.ArgumentParser(prog="granola-share", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--home", type=Path, default=DEFAULT_HOME, help=f"data directory (default {DEFAULT_HOME})")
    sub = p.add_subparsers(dest="cmd", required=True)

    sub.add_parser("setup", help="guided setup for the pool server").set_defaults(fn=cmd_setup)
    sub.add_parser("init", help="write a starter config.toml without the wizard").set_defaults(fn=cmd_init)
    lp = sub.add_parser("login", help="sign the server in to a Granola account (for server_sync)")
    lp.add_argument("--no-browser", action="store_true")
    lp.set_defaults(fn=cmd_login)
    sub.add_parser("logout").set_defaults(fn=cmd_logout)
    tp = sub.add_parser("tools", help="print the MCP tools Granola exposes (debugging)")
    tp.add_argument("--probe", action="store_true", help="also list meetings and fetch one")
    tp.set_defaults(fn=cmd_tools)
    sp = sub.add_parser("sync", help="server-side pull from its own Granola account")
    sp.add_argument("--once", action="store_true")
    sp.add_argument("--no-ollama", action="store_true")
    sp.set_defaults(fn=cmd_sync)
    sub.add_parser("serve", help="web UI + ingest API only").set_defaults(fn=cmd_serve)
    rp = sub.add_parser("run", help="what the server runs: web UI + ingest API (+ own sync if enabled)")
    rp.add_argument("--no-ollama", action="store_true")
    rp.set_defaults(fn=cmd_run)

    cp = sub.add_parser("client", help="friend's laptop: watch my Granola and push approved notes")
    csub = cp.add_subparsers(dest="client_cmd", required=True)
    csub.add_parser("setup", help="guided setup").set_defaults(fn=cmd_client_setup)
    clp = csub.add_parser("login", help="sign in to Granola again")
    clp.add_argument("--no-browser", action="store_true")
    clp.set_defaults(fn=cmd_client_login)
    csub.add_parser("run", help="watch forever").set_defaults(fn=cmd_client_run)
    cop = csub.add_parser("once", help="check once and exit")
    cop.add_argument("--auto", action="store_true", help="share without asking this time")
    cop.set_defaults(fn=cmd_client_once)

    ap = sub.add_parser("autostart", help="install/uninstall the background service")
    ap.add_argument("action", choices=["install", "uninstall"])
    ap.add_argument("--role", choices=["server", "client"], required=True)
    ap.set_defaults(fn=cmd_autostart)

    args = p.parse_args(argv)
    args.home = Path(args.home).expanduser()
    args.home.mkdir(parents=True, exist_ok=True)
    try:
        args.fn(args)
    except KeyboardInterrupt:
        sys.exit(130)


if __name__ == "__main__":
    main()
