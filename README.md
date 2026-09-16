# granola-share

Pool your friends' Granola lecture notes on one home server, sorted by class, for free.

There are two roles:

- **The pool owner** runs one guided setup on a Mac mini or any always-on machine. That
  machine keeps the notes, sorts them into class folders, and serves a small web UI.
- **Each friend** runs one install command on their laptop. It signs them into their own
  Granola account, watches for finished notes, and holds each one in a **local control
  panel** until they press Share. Nothing leaves the laptop before that.

Nothing is paid: it uses Granola's official MCP connector (free plan: AI notes and your
typed notes; transcripts only if the account is on a paid plan) and a local Ollama model.

## How it fits together

```
     friend's laptop                                    your Mac mini
┌──────────────────────────────┐                ┌──────────────────────────────┐
│ Granola app                  │                │ POST /api/preview → a guess  │
│   └ finishes a note          │                │ POST /api/ingest  → filed    │
│                              │   over         │                              │
│ granola-share client run     │   Tailscale    │ classify: rules → Ollama     │
│   └ polls your own account   │ ◀────────────▶ │ ~/GranolaShare/<Class>/*.md  │
│   └ queues it on disk        │                │                              │
│                              │                │ web UI + zip download        │
│ control panel (localhost)    │                │ http://<mini>:8787           │
│   └ you press Share / Skip   │                │ Classes · Unsorted · All ·   │
│     http://127.0.0.1:8790    │                │ Status                       │
└──────────────────────────────┘                └──────────────────────────────┘
```

One note's journey:

```
Granola  →  poll  →  queue on your own disk  →  you press Share  →  the pool
                     (waits as long as it takes)   POST /api/ingest   sorted into a class
```

The polling loop never waits for a person. It fetches a note, asks the pool for a class
guess, writes the note into `~/.granola-share/queue/`, and moves on. Your Share or Skip
can come five minutes later or tomorrow morning; the note is still there.

## Pool owner: set up the server

```bash
curl -fsSL https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main/install.sh | sh -s -- server
```

The wizard walks through six steps: pool name, password and folder; your classes; the
Ollama model (it lists what's installed and offers to pull one); whether the server should
also sync its own Granola account; autostart at login; and finally it prints the URL and
password to send to friends.

Manual equivalent from a checkout:

```bash
python3 -m venv .venv && .venv/bin/pip install -e .
.venv/bin/granola-share setup      # guided
.venv/bin/granola-share run        # if you skipped autostart
.venv/bin/granola-share doctor     # check it afterwards
```

Requirements: Python 3.11+ (the installer fetches its own), [Ollama](https://ollama.com)
for AI sorting (optional; folder and title rules work without it), Tailscale so friends
can reach the machine.

**Which model?** The wizard lists what Ollama has installed and picks the strongest by
default. Recommended: `qwen3.6:35b-a3b` (mixture-of-experts: 35B-class judgment, only ~3B
active per token, so it's fast; ~24 GB RAM). Smaller machines: `gemma4:e4b` (16 GB) or
`qwen3:1.7b` (8 GB). Sorting sends the summary with a strict JSON schema and
`think: false`, so any of these return clean output; the bigger model mainly gives better
class picks on vague notes and better titles and topics.

## Friend: connect your Granola

Mac / Linux:

```bash
curl -fsSL https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main/install.sh | sh
```

Windows (PowerShell):

```powershell
irm https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main/install.ps1 | iex
```

The wizard has five steps:

1. **The pool server** — address (e.g. `http://your-mac-mini:8787`) and password. It checks
   both before going on, and tells you the pool name and its classes.
2. **You** — the name shown as the source of your notes.
3. **Granola** — a browser window opens; sign in. It then says out loud who you signed in
   as and how many notes that account has: `Signed in as you@gmail.com (Your workspace) ·
   found 12 notes in the last 30 days`. If the answer is **0 notes**, it says so and offers
   to sign in again with a different Google account (up to three tries).
4. **Sharing** — "Review each finished note in the control panel before it is shared?
   (No = share everything automatically)".
5. **Keep it running** — installs the background watcher so it starts at login.

It finishes by printing `Control panel: http://127.0.0.1:8790`, which is open whenever
`granola-share client run` is watching.

## The control panel

This is where a friend lives. It is a small web page on **your own laptop only**, served by
`granola-share client run` at **http://127.0.0.1:8790**. It exists to answer four questions
at a glance:

- **Connected?** — a green dot when the pool answers and Granola is signed in; amber when
  something is waiting on you; clay when something is broken.
- **As whom?** — the signed-in Granola email, in big letters, on every page. This is the
  single most useful line on the screen: the wrong Google account looks exactly like a
  working setup otherwise.
- **What is waiting?** — the approval queue.
- **What did it do?** — history and the log.

### Status header

A card with the dot and the email, then a meta line — `workspace X · pool Y · last checked
2 min ago` — then three tiles: **Found**, **Shared**, **Waiting** (amber when above zero).

Under it, callouts appear only when they matter:

- the pool server is unreachable, or rejects the password;
- you are not signed in to Granola, with a **Sign in** button;
- **"Connected, but this account has 0 notes. If your lectures live in a different Google
  account, switch."** with a **Sign in as someone else** button;
- a sign-in is in progress.

### The approval queue (`/`)

Head line: *"3 notes waiting to share"*, with **Share all** and **Skip all**.

Each note shows its title, a meta line `Sep 11 · <guessed class>`, and a dropdown listing
your pool's classes plus `Unsorted`, pre-selected to the pool's guess. A weak or missing
guess shows as an amber **Unsorted — pick a class**, so you know the machine did not
actually decide. Change the dropdown if the guess is wrong, then press **Share**
(the class you picked wins over the model) or **Skip**.

Click a title to read the rendered note before deciding. A note whose push failed stays in
the queue with the error on it and is retried when you press Share again.

When the queue is empty it says *"Nothing waiting. Next check in N min."* with a
**Check now** button that wakes the watcher immediately instead of waiting out the interval.

### History (`/history`) and log (`/log`)

History is a table of everything shared or skipped — date, title, class, an "open in pool"
link, and how long ago. The log view is the last 200 lines of
`~/.granola-share/logs/client.log`, so you never have to find the file yourself.

The page re-checks `/api/state` every 15 seconds and reloads itself when the waiting count
or the last-checked time changes.

### Safety

The panel binds to `127.0.0.1` only. It is never exposed on your network, not over
Tailscale, not to the pool owner. There is no password, because nothing outside your laptop
can reach it. Every form carries a per-process token and the app rejects any POST whose
token is wrong or whose `Host` header is not `127.0.0.1` / `localhost` on the panel port,
so a web page you happen to be visiting cannot share your notes for you.

If port 8790 is already taken, the watcher logs that and keeps polling without a panel.
Change `panel_port` in `client.toml` to fix it.

## The three sharing modes

| `mode` | What happens when a note finishes |
|---|---|
| `ask` (default) | The note is queued in the control panel and waits for your Share / Skip, however long that takes. One desktop notification: `3 notes ready to review · http://127.0.0.1:8790`. |
| `auto` | Shared immediately, no questions. You get a notification per shared note. |
| `dialog` | The old v0.1 behaviour: a native popup per note, one at a time, which gives up after `dialog_timeout_seconds` and asks again next poll. |

To switch: edit `mode` in `~/.granola-share/client.toml` and restart the watcher, or rerun
`granola-share client setup` (which offers `ask` or `auto`). For a one-off,
`granola-share client once --auto` shares this round without asking.

`dialog` is kept because some people genuinely prefer the popup. It is not the default
any more: a popup that vanishes after five minutes loses notes silently, and only one can
be on screen at a time.

## The pool web UI

`http://<your-mini>:8787`, one password for everyone in the pool.

| Page | What's on it |
|---|---|
| `/` Classes | Pool status strip (notes, classes, contributors, last ingest), a card per class with its count, last note date and a zip link, an Unsorted callout when it isn't empty, and the 10 newest notes. |
| `/unsorted` | Notes the sorter wasn't sure about. File them from the note page. |
| `/all` | Everything, newest first. |
| `/status` | **New in v0.2.** The whole server in one page. |
| `/note/<id>` | The rendered note, who shared it, how it was sorted, a move-to-class form, and a Markdown download. |
| `/class/<name>/zip` | One class as a zip. |

The **Status** page shows: pool name and note count; server sync (on/off, signed-in
account and workspace, notes listed, last check, last error — with the same
"Connected, but this account has 0 notes" warning when sync is on and it found nothing);
Ollama (reachable, model installed, cached for 60 seconds so a dead Ollama never hangs the
page); contributors with note counts and latest dates; per-class counts; and a config
summary (pool folder, port, model, min confidence, version). **The password is never shown.**

HTTP API, all with `Authorization: Bearer <pool password>`:

| Endpoint | Purpose |
|---|---|
| `GET /api/health` | `{ok, pool_name, classes, notes, version, server_time, last_ingest}` |
| `POST /api/preview` | Classify a note **without saving it** — this is the guess the control panel shows. Cached, so the ingest that follows doesn't run Ollama twice. |
| `POST /api/ingest` | File a note. Accepts an optional `class_name` (must be one of your classes or `Unsorted`) which overrides the model — that's your dropdown pick. |
| `GET /api/status` | The Status page as JSON. |
| `GET /api/notes` | The index, optionally `?class_name=`. |

## doctor

Both roles have a one-command checkup. Each line is `✓` (good), `✗` (broken, with a hint
on the next line) or `–` (not applicable). It exits non-zero if anything is broken.

```bash
granola-share client doctor    # on a friend's laptop
granola-share doctor           # on the pool server
```

**`client doctor`** checks, in order:

| Check | What it means |
|---|---|
| Config | `client.toml` exists and has a `server_url`; prints the current mode. |
| Pool server | `/api/health` answered: pool name and how many notes are in the pool. `✗` separates "wrong password" from "cannot reach it at all". |
| Granola login | Tokens exist and Granola answered — prints `Signed in as you@gmail.com · Your workspace`. |
| Notes in Granola | How many notes that account has in the last 30 days. `✗` on 0, with the hint to sign in with the other Google account. |
| Control panel | `http://127.0.0.1:8790/api/state` answered, and how many notes are waiting. Distinguishes "not running — start with `granola-share client run`" from "that port is taken by something else". |
| Autostart | The launchd / Startup / systemd file exists. |
| Log file | Where the log is and its last line. |

**`doctor`** (server) checks: config file and pool name; `pool_dir` is an absolute path and
writable; the web port answers `/api/health` with this config's own password; Ollama is
reachable and the configured model is actually installed (it lists what you do have);
classes are configured; server sync (off, or signed in as whom with how many notes);
autostart; and how many notes are filed in the pool.

## Commands

Global: `--home PATH` (default `~/.granola-share`, or `$GRANOLA_SHARE_HOME`) and `--version`.

Pool server:

| Command | What it does |
|---|---|
| `setup` | Guided server setup. Safe to rerun. |
| `init` | Write a starter `config.toml` without the wizard. |
| `run [--no-ollama]` | What the server runs: web UI + ingest API, plus its own sync if `server_sync` is on. |
| `serve` | Web UI + ingest API only, no sync. |
| `sync [--once] [--no-ollama]` | Pull from the server's own Granola account. |
| `login [--no-browser]` / `logout` | Sign the server in to Granola (only needed for `server_sync`). |
| `doctor` | Check config, pool folder, web port, Ollama, classes, sync login, autostart, note count. |
| `tools [--probe]` | Print the MCP tools Granola exposes; `--probe` also lists your notes and fetches one. Debugging. |

Friend's laptop:

| Command | What it does |
|---|---|
| `client setup` | Guided friend setup. Safe to rerun. |
| `client run` | Watch forever **and** serve the control panel. This is what autostart runs. |
| `client once [--auto]` | Check once and exit. Prints `listed= considered= shared= skipped= queued= pending= errors=`. |
| `client panel` | Serve only the control panel in the foreground, with no polling. Debugging. |
| `client login [--no-browser]` | Sign in to Granola again — including as a different account. |
| `client doctor` | The checkup above. |

Both:

| Command | What it does |
|---|---|
| `autostart install --role server\|client` | Install the background service: launchd on macOS, the Startup folder on Windows, `systemd --user` on Linux. |
| `autostart uninstall --role server\|client` | Remove it. |

All state lives in `~/.granola-share` (override with `--home` or `GRANOLA_SHARE_HOME`):

```
config.toml          server settings
client.toml          friend settings (chmod 600 — it holds the pool password)
tokens.json          Granola OAuth tokens (chmod 600)
oauth_client.json    the dynamically registered OAuth client
state.db             server: the note index
client_state.json    friend: what has been seen, shared, skipped, queued
queue/<id>.json      friend: the body of each note waiting for your Share
logs/server.log      server log
logs/client.log      friend log (line-buffered, so it is never blank)
debug/last_meetings.json   the last raw MCP payload, for when a field name changes
```

## Config reference

### `config.toml` (pool server)

| Key | Default | Meaning |
|---|---|---|
| `pool_name` | `"Lecture notes pool"` | Shown in the web UI and reported to clients. |
| `pool_dir` | `"~/GranolaShare"` | Where the Markdown files live, one folder per class. Must be an absolute path. |
| `pool_password` | `""` | One password for the web UI (cookie) and the API (bearer). Blank = open pool. |
| `web_host` | `"0.0.0.0"` | Listen address. |
| `web_port` | `8787` | Listen port. |
| `poll_interval_seconds` | `300` | How often the server's own sync runs. |
| `server_sync` | `false` | Also pull the server's own Granola account. Needs `granola-share login`. |
| `include_transcripts` | `true` | Fetch transcripts too, when the account's plan has them. |
| `oauth_prompt` | `"login"` | Sent as `prompt=` on sign-in. `login` forces the account picker. Blank = let Granola decide. |
| `oauth_callback_port` | `3334` | Local port the OAuth redirect comes back on. |
| `mcp_url` | `https://mcp.granola.ai/mcp` | Granola's MCP endpoint. |
| `[ollama] enabled` | `true` | `false` = folder and title rules only. |
| `[ollama] host` | `http://localhost:11434` | |
| `[ollama] model` | `"qwen3.6:35b-a3b"` | |
| `[ollama] min_confidence` | `0.6` | Below this a note goes to Unsorted. |
| `[[classes]]` | none | Repeatable: `name`, `aliases` (matched against Granola folder names and note titles), `description` (helps the model). |

### `client.toml` (friend's laptop)

| Key | Default | Meaning |
|---|---|---|
| `server_url` | `""` | e.g. `http://your-mac-mini:8787`. |
| `pool_key` | `""` | The pool password. |
| `pool_name` | `""` | Filled in from the server, for nicer wording. |
| `display_name` | `""` | Shown as the source of your notes. |
| `mode` | `"ask"` | `ask` (queue in the panel) / `auto` (share everything) / `dialog` (legacy native popup). |
| `panel_enabled` | `true` | Serve the control panel from `client run`. |
| `panel_port` | `8790` | The panel lives at `http://127.0.0.1:<panel_port>`. Localhost only. |
| `notifications` | `true` | Desktop notification when notes are waiting, or when one is shared. |
| `poll_interval_seconds` | `180` | How often Granola is checked. **Check now** in the panel skips the wait. |
| `share_lookback_days` | `7` | How far back the first poll looks. |
| `include_transcripts` | `true` | Send transcripts along with the notes, when your plan has them. |
| `dialog_timeout_seconds` | `300` | `dialog` mode only: how long the popup waits before giving up. |
| `oauth_prompt` | `"login"` | `login` forces Google's account picker, so a cached browser session cannot silently sign you in as the wrong you. |
| `oauth_callback_port` | `3334` | |
| `mcp_url` | `https://mcp.granola.ai/mcp` | |

## How sorting works

1. **Folder and title rules.** If the note's Granola folder or its title matches a class
   name or alias, that wins. No model involved.
2. **Ollama.** Otherwise the summary goes to the local model with a strict JSON schema
   (`class_name` must be one of your classes, or `Unsorted`). Below `min_confidence` it
   goes to Unsorted. The answer is cached per note, so the guess the control panel showed
   you is the same one the server reuses when the note actually arrives.
3. **Humans win.** The dropdown in the control panel and the move form on a note's page
   both override the model; the file moves with it and the note is recorded as sorted
   `human` with confidence `1.00`.

## Troubleshooting

Everything in this section is a real failure from the week before v0.2, and each one was
hard to see. That is why the panel and `doctor` exist.

### "It says it's working but nothing ever shows up in the pool"

Almost always the **wrong Google account**. Granola signs in through a browser that may
already have a session, so you can end up connected to an empty workspace — which looks
identical to a working setup.

What to look at: the control panel puts the signed-in **email** at the top of every page,
and when that account has no notes it says so out loud:

> Connected, but this account has 0 notes. If your lectures live in a different Google
> account, switch.

Press **Sign in as someone else** there, or run `granola-share client login`. Because
`oauth_prompt = "login"`, the account picker is forced every time instead of silently
reusing the session. `granola-share client doctor` reports the same thing on one line.

### "It used to find notes and now it finds zero"

Granola changed `list_meetings` and `get_meetings` to return **XML** instead of JSON. The
v0.1 parser read JSON only, found nothing, and said nothing. v0.2 parses both, tolerantly
— including the unescaped `<name@example.com>` addresses in participant lists that are not
well-formed XML.

If you suspect the format moved again: `granola-share tools --probe` prints the tools, the
notes it can see and one full note, and the last raw payload is always saved at
`~/.granola-share/debug/last_meetings.json`.

### "The log file is empty"

It isn't any more. The background service runs Python with `-u` and
`PYTHONUNBUFFERED=1`, and the CLI line-buffers its own output, so lines land in
`logs/client.log` as they happen instead of sitting in a buffer for hours. The panel's
**Log** tab shows the last 200 lines without you going looking for the file.

If a log is genuinely still empty, the service probably never started:
`granola-share client doctor` checks autostart and the log file together.

### "A popup asked me about a note, I got distracted, and it disappeared"

That was v0.1: one native dialog at a time, five-minute timeout, and a lost note. In v0.2
(`mode = "ask"`) each note is written to `~/.granola-share/queue/` and listed in the panel
until you decide. Nothing times out. A note whose push then fails keeps its error and is
retried the next time you press Share. `Share all` / `Skip all` clear a backlog in one go.

### Other quick ones

- **Panel won't open.** It only runs while `granola-share client run` is watching. Check
  with `granola-share client doctor`, or run `granola-share client panel` to serve just
  the panel. If the port is taken, change `panel_port`.
- **Server unreachable from a laptop.** Both machines need to be on the same Tailscale
  network; `granola-share doctor` on the server confirms the web port answers locally
  first.
- **Notes all land in Unsorted.** Either Ollama isn't running (`doctor` says so) or your
  classes have no aliases matching your Granola folders. Adding aliases is cheaper than a
  bigger model.

## Why this design

Granola's official connectors are read-only, so no app can move a note into a Granola
folder for you. Instead each friend's client pushes approved notes straight to the pool.
Granola's public API (used by the Obsidian plugins and granola-exporter) needs the paid
Business plan; the MCP connector is free. Details and alternatives are in
[docs/DESIGN.md](docs/DESIGN.md).

The v0.2 rule of thumb, learned the hard way: **say the quiet failures out loud.** Show the
identity on every screen, make approval durable instead of ephemeral, and never let a
component fail by returning zero.

## Development

```
granola_share/
  config.py     server + client TOML config (reader and writer)
  wizard.py     the two guided setups
  oauth.py      discovery, dynamic client registration, PKCE login, refresh
  granola.py    MCP client: tolerant XML/JSON parsing, runtime argument discovery
  client.py     friend watcher: poll → queue → share, and the durable queue itself
  panel.py      the friend's local control panel (FastAPI, 127.0.0.1 only)
  dialogs.py    native Share/Skip dialogs and notifications (legacy `dialog` mode)
  doctor.py     the two checkups, all IO injected
  autostart.py  launchd / Startup folder / systemd --user
  classify.py   rules → Ollama structured output → Unsorted
  store.py      SQLite index + Markdown writer
  sync.py       server-side poll loop (optional)
  ui.py         the shared look: layout, pill, dot, stat, alert, relative times
  web.py        FastAPI: pool web UI, /status, /api/health, /api/preview, /api/ingest
  cli.py        the granola-share command
tests/          no network, no live install: .venv/bin/python -m pytest -q
```

Tests use `tmp_path` throughout and never touch a real `~/.granola-share`.

## Known unknowns

- **Granola's MCP output has changed once and can change again.** v0.2 accepts JSON and
  XML and keeps the last raw payload in `debug/last_meetings.json`; run
  `granola-share tools --probe` after your first login to see what your account returns.
- **`list_meetings` has no offset or limit** — only a time range (`this_week`,
  `last_week`, `last_30_days`, `custom`). We ask for a custom range from your lookback
  date. If Granola caps how much it returns inside that range, we cannot page past it.
- **Transcripts depend on the plan.** On the free plan `get_meeting_transcript` returns
  nothing, so notes arrive without one. Not an error, just quieter notes.
- **The Windows installer, Startup-folder autostart, and MessageBox dialog** are written
  from the Windows APIs but have only been tested on macOS.
- **The control panel assumes one person at one laptop.** It has no password because it is
  bound to localhost; if you run it on a shared machine, everyone with an account on that
  machine can reach it.
- **Class guesses are only as good as your aliases and your model.** Expect to file some
  notes by hand, especially early in a term.
