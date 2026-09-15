# granola-share

Pool your friends' Granola lecture notes on one home server, sorted by class, for free.

- **You (the pool owner)** run one guided setup on a Mac mini or any always-on machine.
- **Each friend** runs one install command. It signs them into their own Granola, and every
  time Granola finishes a note it pops up *"Share this to the pool?"*. Approved notes go to
  your server, which sorts them into class folders and serves a small web UI over Tailscale.

Nothing is paid: it uses Granola's official MCP connector (free plan: AI notes and your
typed notes; transcripts only if the account is on a paid plan) and a local Ollama model.

```
friend's laptop                                  your Mac mini
┌──────────────────────────┐   POST /api/ingest  ┌──────────────────────────────┐
│ Granola app records      │ ──────────────────▶ │ classify (rules → Ollama)    │
│ granola-share client     │   over Tailscale    │ ~/GranolaShare/<Class>/*.md  │
│  ↳ polls own account     │                     │ web UI: browse, move, zip    │
│  ↳ popup: Share / Skip   │ ◀────────────────── │ http://<mini>:8787           │
└──────────────────────────┘   browse/download   └──────────────────────────────┘
```

## Pool owner: set up the server

```bash
curl -fsSL https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main/install.sh | sh -s -- server
```

The wizard walks through six steps: pool name and password, your classes, Ollama model
(it lists what's installed and offers to pull one), whether the server should also sync
its own Granola account, autostart at login, and finally prints the URL and password to
send to friends.

Manual equivalent from a checkout:

```bash
python3.11 -m venv .venv && .venv/bin/pip install -e .
.venv/bin/granola-share setup      # guided
.venv/bin/granola-share run        # if you skipped autostart
```

Requirements: Python 3.10+ (the installer fetches its own), [Ollama](https://ollama.com)
for AI sorting (optional; folder/title rules work without it), Tailscale so friends can reach it.

**Which model?** The wizard lists what Ollama has installed and picks the strongest by default.
Recommended: `qwen3.6:35b-a3b` (mixture-of-experts: 35B-class judgment, only ~3B active per token,
so it's fast; ~24 GB RAM). Smaller machines: `gemma4:e4b` (16 GB) or `qwen3:1.7b` (8 GB).
Sorting sends the summary with a strict JSON schema and `think: false`, so any of these return
clean output; the bigger model mainly gives better class picks on vague notes and better titles/topics.

## Friends: connect your Granola

Mac / Linux:
```bash
curl -fsSL https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main/install.sh | sh
```
Windows (PowerShell):
```powershell
irm https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main/install.ps1 | iex
```

The wizard asks for the server address and pool password (it checks them), your name,
opens the browser to sign in to Granola, asks whether to prompt per note or share
everything automatically, offers to look at the last 7 days right away, and installs the
background watcher. From then on, a native dialog appears when a note finishes:

> Granola finished notes for: "Bio 110 lecture 4"  
> Share it to Fall pool?  [Skip] [Share]

Nothing leaves the laptop unless they click Share (or chose automatic mode). A dialog
nobody answers is asked again on the next check.

## Commands

| Command | What it does |
|---|---|
| `setup` | guided server setup (safe to rerun) |
| `run` | web UI + ingest API, plus the server's own sync if enabled |
| `client setup` | guided friend setup |
| `client run` / `client once [--auto]` | watch forever, or check once |
| `client login` / `login` | sign in to Granola again |
| `tools --probe` | print Granola's MCP tools and your latest notes (debugging) |
| `autostart install|uninstall --role server|client` | background service: launchd, Windows Startup folder, or systemd --user |

All state lives in `~/.granola-share` (override with `--home` or `GRANOLA_SHARE_HOME`):
`config.toml` or `client.toml`, `tokens.json` (0600), `state.db`, `client_state.json`,
`logs/`, `debug/last_meetings.json`.

## How sorting works

1. **Folder / title rules.** If the note's Granola folder or title matches a class name or
   alias, that wins.
2. **Ollama.** Otherwise the summary goes to the local model with a strict JSON schema
   (`class_name` must be one of your classes or `Unsorted`). Below `min_confidence` it goes
   to Unsorted.
3. **Humans.** Any note can be moved from its page in the web UI; the file moves with it.

## Why this design

Granola's official connectors are read-only, so no app can move a note into a Granola
folder for you. Instead each friend's client pushes approved notes straight to the pool.
Granola's public API (used by the Obsidian plugins and granola-exporter) needs the paid
Business plan; the MCP connector is free. Details and alternatives are in
[docs/DESIGN.md](docs/DESIGN.md).

## Development

```
granola_share/
  config.py     server + client TOML config (reader and writer)
  wizard.py     the two guided setups
  oauth.py      discovery, dynamic client registration, PKCE login, refresh
  granola.py    MCP client; discovers tool argument names at runtime
  client.py     friend watcher: poll → popup → push
  dialogs.py    native Share/Skip dialogs and notifications (macOS/Windows/Linux)
  autostart.py  launchd / Startup folder / systemd --user
  classify.py   rules → Ollama structured output → Unsorted
  store.py      SQLite index + Markdown writer
  sync.py       server-side poll loop (optional)
  web.py        FastAPI: web UI, /api/health, /api/ingest, /api/notes
  cli.py        the granola-share command
tests/          36 tests, no network: .venv/bin/python -m pytest
```

## Known unknowns

- The exact field names Granola's MCP tools return are only visible after login. The
  client maps common variants and saves the last raw payload under `debug/`; run
  `granola-share tools --probe` after your first login to check.
- The Windows installer, Startup-folder autostart, and MessageBox dialog are written from
  the Windows APIs but were only tested on macOS.
