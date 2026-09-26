# Study Stash

Your Granola lectures, rewritten into study notes by your own model and sorted by class, on
your own Mac mini or Windows PC (any computer that stays on). Free and open source.

> Study Stash is an independent project, **not affiliated with or endorsed by Granola**.
> "Granola" is a trademark of its owner, and is used here only to say what this works with.

- **Your laptop** records lectures in Granola, as usual. When Granola finishes a lecture, a
  small background app sends it to your library.
- **Your Mac mini** (or a Windows PC that stays on) keeps the library. It writes study notes from each lecture's transcript
  with the Ollama model you pick, files each lecture under its class, and serves a web page
  you can open from your laptop or phone over Tailscale.

It uses Granola's official MCP connector (free) and a local Ollama model, so nothing is paid.
The command it installs is still called `granola-share`, from before it was renamed.

<p align="center"><img src="docs/screenshots/library.png" width="800" alt="The Study Stash app showing the library: a sidebar of classes with colored dots, and a list of recent lectures"></p>

```
your laptop                                        your Mac mini
┌──────────────────────────┐   POST /api/ingest   ┌────────────────────────────────────┐
│ Granola records          │ ───────────────────▶ │ queue → study notes from the       │
│ Study Stash (bg app)   │   over Tailscale     │   transcript (your Ollama model)   │
│  ↳ finished lectures     │                      │ → sort into a class                │
│  ↳ copies the transcript │ ◀─────────────────── │ ~/GranolaShare/<Class>/*.md        │
└──────────────────────────┘  browse, search, zip │ web page  http://<mini>:8787       │
                                                  └────────────────────────────────────┘
```

## 1. Set up the library's computer

On the computer that keeps your library (a Mac mini, or any Mac or Windows PC that stays on),
download its installer from the [releases page](https://github.com/Joseph-Rus/study-stash/releases/latest):

| | |
|---|---|
| **Mac** | [Study-Stash-Library.dmg](https://github.com/Joseph-Rus/study-stash/releases/latest/download/Study-Stash-Library.dmg) |
| **Windows** | [Study-Stash-Library-Setup.exe](https://github.com/Joseph-Rus/study-stash/releases/latest/download/Study-Stash-Library-Setup.exe) |

Or, from a terminal, one line downloads and installs it for you:

```bash
# Mac (Terminal)
curl -fsSL https://raw.githubusercontent.com/Joseph-Rus/study-stash/main/install.sh | sh -s -- library
```
```powershell
# Windows (PowerShell)
$env:STUDYSTASH_ROLE='library'; irm https://raw.githubusercontent.com/Joseph-Rus/study-stash/main/install.ps1 | iex
```

- **Mac:** open the DMG and drag **Study Stash** into Applications. macOS asks once before opening
  an app from the internet that isn't from the App Store (Study Stash isn't signed with a paid
  Apple Developer ID): click **Done**, then **System Settings → Privacy & Security → Open Anyway**.
- **Windows:** run the Setup.exe. It installs for your account only, no admin rights, and adds
  **Study Stash** to the Start Menu. Since it isn't signed, Windows may say "Windows protected your
  PC": click **More info**, then **Run anyway**.

Open **Study Stash** and click **Set Up**: it downloads the Whisper model that transcribes
lectures, has you name your library and pick a password, and lets you add your classes (you can
always add more later). When you finish, the app shows your library and the address and password
to connect your laptop.

**Coming from 0.4.x?** Install the new app the same way — your lectures and settings stay right
where they are.

## 2. Connect your laptop

This is the computer you record lectures on. Download its installer the same way:

| | |
|---|---|
| **Mac** | [Study-Stash-Laptop.dmg](https://github.com/Joseph-Rus/study-stash/releases/latest/download/Study-Stash-Laptop.dmg) |
| **Windows** | [Study-Stash-Laptop-Setup.exe](https://github.com/Joseph-Rus/study-stash/releases/latest/download/Study-Stash-Laptop-Setup.exe) |

```bash
# Mac (Terminal)
curl -fsSL https://raw.githubusercontent.com/Joseph-Rus/study-stash/main/install.sh | sh
```
```powershell
# Windows (PowerShell)
irm https://raw.githubusercontent.com/Joseph-Rus/study-stash/main/install.ps1 | iex
```

Install it the same way as the library (Mac: drag into Applications, then **Open Anyway**;
Windows: run the Setup.exe, then **Run anyway**). The first time it records, it asks for the
microphone — say yes (it may ask again after an update, since an ad-hoc signed app can't remember
across one). Open **Study Stash**, click **Set Up**, and type the library's address and password
from step 1 (or find them again under **Settings → Connect a laptop** on the library's page).

<p align="center"><img src="docs/screenshots/setup.png" width="700" alt="Study Stash's setup: the microphone allowed, connected to the library, and the transcription model ready, each step with a green check"></p>

## 3. Using the app

Open **Study Stash** from Applications or Spotlight. Its toolbar has two tabs:

- **This Mac** (⌘1): whether everything is working, what was sent and where each lecture was
  filed, and how this laptop sends. If something needs you, like a new password on the Mac mini
  or signing in to Granola again, it shows here in red with the fix next to it.
- **Library** (⌘2): your library on the Mac mini, signed in with the password this Mac already has.

<p align="center"><img src="docs/screenshots/this-mac.png" width="800" alt="The This Mac tab: Library, Granola, Watching, and Transcripts all green, and recent lectures showing where each was filed"></p>

⌘R reloads, ⌘+ and ⌘− zoom, and downloads land in Downloads. It follows your Mac's light or
dark mode. On the Mac mini the app shows just the library.

**On Windows** it's the same app: open **Study Stash** from the Start Menu. Its tabs are **This
PC** (Ctrl+1) and **Library** (Ctrl+2), F5 reloads, Ctrl+plus and Ctrl+minus zoom, and it follows
Windows' light or dark mode. It's built on WebView2, the Edge engine that comes with Windows 10
and 11. (On Linux, Study Stash opens in its own Chrome or Edge window instead.)

## Beyond lecture notes

These run on the library, and show in its web page and the app.

- **Pick the AI.** Settings → AI: the library's Ollama model (free, private), or Claude, ChatGPT or Gemini
  through their own command-line apps on the library's computer (Claude Code, Codex, Antigravity), signed in
  with your own account and plan. One AI for everything, or another for notes, sorting or questions.
  `granola-share ai` (the engine's `studystash ai`) shows, picks, tests and asks from a terminal.
- **Canvas.** Settings → Canvas: your school's Canvas address and which course each class is. A small Chrome
  extension reads Canvas with your own sign-in (no Canvas token, which many schools turn off) and hands it to
  the library, which mirrors each class into its `Canvas/` folder: assignment instructions and rubrics, your
  submissions with scores and comments, module files and pages, announcements. The sidebar gets **Due**, and
  each class lists what's still to hand in. After a sync, the AI explores each class's Canvas once and writes
  `Canvas/canvas-recipe.md`, a map of where that instructor puts things.
- **Chat.** Talk to the AI about everything, a class, or a lecture. It reads your lectures, Canvas and any
  folders you allow, and remembers the conversation. With "Let it write files" on, it can make you a study
  guide; every change it makes is listed under **What the AI changed**, with Undo.
- **Capture.** Jot something down (the Capture page, or ⌥Space → Capture); the AI files it under its class.
- **Search files** as well as lectures: Canvas PDFs and slides, the AI's files, and the folders you add under
  Settings → Folders it may read (private ones by name only).
- **Open in Claude Code:** on the library's computer, a class opens in Ghostty (or the terminal you pick)
  with Claude Code, briefed on the class and with the library's tools.
- **Granola's free plan** shares no folders: the laptop files a Granola lecture by its timetable instead.

## When a lecture finishes

There's nothing to learn. Record in Granola, stop, and carry on. When Granola has finished the
note, Study Stash sends it to your library (or asks first, if you chose "ask before each one").
When the library has filed it, you get one notification: *"… is in Fall 2026 under Bio 110."*

With [transcript copying](#optional-copy-transcripts-from-the-granola-app) turned on:

- **You stopped the recording in Granola** (the usual case): a few seconds later, the transcript
  panel opens, the transcript is copied, and the panel closes again. You don't click anything.
- **Granola wasn't in front** (you stopped it from the menu bar, or it stopped by itself): one popup
  asks *"Save it to Fall 2026 with its transcript?"*
  - **Save** brings Granola forward, copies the transcript, and puts you back where you were.
  - **Not now** sends the lecture with Granola's own summary instead.
- **A lecture went out without its transcript:** open it in Granola later. The transcript is picked
  up and the notes are rewritten.

## Study notes from the transcript

Granola's own summary comes from whatever model Granola runs. When a lecture arrives with its
transcript, the library writes its own study notes from it instead, with your model: an
overview, key concepts, worked examples and formulas, announcements with their dates, and review
questions. Long transcripts are summarized in parts, then merged. It all happens in the
background, one lecture at a time, so sending never waits on the model.

Transcripts come through Granola's official connector on Granola's paid plans. Without a
transcript, a lecture keeps Granola's own summary.

### Optional: copy transcripts from the Granola app

On a Mac, Study Stash can press Granola's own **Copy transcript** button for you when a lecture
ends, so your notes are written from the transcript. **It's off unless you turn it on**, in the
app under **Sending**. Before you do, know that:

- **It automates the Granola app.** Granola's terms of service forbid scraping content from
  their service "through use of manual or automated means", and this may count. Using it could
  put your Granola account at risk. It's your call, for your own account.
- **What it does:** it notices a recording ending when Granola stops using the microphone. Then
  it opens the transcript panel if needed and clicks **Copy transcript**, only in a pause in your
  typing, and puts your clipboard, cursor, and selection back right after. Nothing else is read
  from Granola.
- **It needs** Accessibility permission for **python3.12**. The app's Allow step opens the right
  page of System Settings.

Pick the model, turn our study notes off, or keep Granola's summary next to them under
**Settings** on the library's web page. After switching models, **Rewrite all summaries** redoes
the library one lecture at a time. Each lecture stays readable while it waits.

## The library

It's the app's Library tab, and also a web page at `http://<mac mini>:8787` for your phone or any
browser, behind your password. On the Mac mini itself, it opens without one.

- Each class has a color dot that follows it everywhere it appears.
- Search covers titles, summaries, topics, and transcripts.
- Each lecture has Summary, Transcript, and Typed notes, and formulas render as math. Its class
  is a menu under the title: pick another and the lecture moves. You can also rewrite its
  summary, delete it, or download it as Markdown.
- Download a whole class as a zip.

<p align="center"><img src="docs/screenshots/lecture.png" width="800" alt="A lecture in the library: study notes written by the model from the transcript, with key ideas and a formula"></p>

**Settings** has the models, what gets written and how lectures are sorted, your classes, the
install line for a laptop, updates, and rewriting every summary:

<p align="center"><img src="docs/screenshots/settings.png" width="800" alt="Library settings: which model writes summaries and which sorts, switches for notes and sorting, and the class list"></p>

<sub>Screenshots use made-up lectures; addresses and passwords are blurred.</sub>

## Updates

Both computers update themselves: 10 minutes after it starts, then every 6 hours, the app checks
for a new release and installs it as soon as nothing is recording, paused, or being transcribed,
then restarts on the new version. Turn this off with `auto_update = false` in `client.toml`; to
update right away, run `studystash update`, or click **Update now** in Settings.

On a Mac, an update downloads the release's DMG and swaps in the new **Study Stash.app**. On
Windows, it downloads the new Setup.exe and runs it quietly, which closes and reopens the app.
Either way the download is checked against the release's `SHA256SUMS.txt` first, and only an
installed copy updates itself — a build folder, or `dotnet run`, never calls GitHub.

Releases come from CI (`.github/workflows/ci.yml`): every push tests the engine on macOS, Linux,
and Windows, builds and self-tests both apps (a fake microphone and the tiny Whisper model prove
each one actually transcribes), and installs each with its own installer. To ship a release, bump
`StudyStashVersion` in `engine/Directory.Build.props` and merge to `main`. When everything passes,
CI tags it and publishes the four installers plus `SHA256SUMS.txt`, and both computers pick it up
within the next 6 hours.

## Uninstall

**Mac:** quit Study Stash and drag it from Applications to the Trash. **Windows:** Settings →
Apps → **Study Stash** → Uninstall. Either way, your lectures stay in your home folder (the
library's) and in `Documents\Study Stash` (the laptop's) — uninstalling only removes the app.

## Set up with Claude Code

Open this repo in Claude Code and say "set me up". The `granola-share-setup` agent
(`.claude/agents/granola-share-setup.md`) works out whether it's on the Mac mini or the laptop,
asks for your answers, runs the install and setup, then runs `granola-share doctor` and fixes
what it finds. To have it in every project, copy the file to `~/.claude/agents/`.

## Troubleshooting

Start with `granola-share doctor`. It checks every piece and prints a fix for each problem.

| Problem | Fix |
|---|---|
| The one-line install failed | It prints the reason; rerun the same line, it's safe to repeat. Or download the installer directly from the [releases page](https://github.com/Joseph-Rus/study-stash/releases/latest). |
| "the download didn't match its checksum" | A bad download or a stale mirror. Rerun the install line; if it keeps happening, download the installer from the releases page instead. |
| Laptop: "could not reach …" | Tailscale on and signed in on both computers, and the library's computer awake. Try the `100.x.y.z` address instead of the name. |
| "Granola app ✗ not installed" | Install Granola from [granola.ai/download](https://www.granola.ai/download). Only the laptop needs it, not the library's computer. |
| Windows: "Windows protected your PC" | The app isn't signed with a paid certificate. Click **More info**, then **Run anyway**. |
| Laptop can't reach a library on Windows | Windows Firewall. Reopen Setup on the PC and let it add the rule (Windows asks for permission). |
| Tailscale "signed out" or "turned off" | Open Tailscale and sign in with the same account on both computers, or rerun `granola-share setup` to connect it. |
| Laptop: "wrong password" | The password is in the Mac mini's `~/.granola-share/config.toml`, and under Settings on the library's page. |
| No study notes, only Granola's | The transcript wasn't copied (open the lecture's transcript in Granola), or Ollama is closed on the Mac mini. |
| A lecture shows "Failed" | Open it, then **Try again**. The reason is on the page and in `~/.granola-share/logs/server.log`. |
| Sign-in timed out | Open Study Stash and click **Sign in to Granola again**. |
| "Copy transcripts ✗ Accessibility" | Open Study Stash and click **Allow transcript copying**, then turn on **python3.12**. |
| Transcripts aren't copied | They're copied only while Granola is in front with the lecture's transcript panel open. Open it and wait a few seconds. |

Logs live in `~/.granola-share/logs/` (`server.log`, `client.log`, `update.log`).

## Commands

| Command | What it does |
|---|---|
| `setup` | set up the library (safe to rerun; `--help` lists flags to answer without prompts) |
| `run` | the library: web page, ingest API, study notes (+ its own sync if enabled) |
| `client open [--install] [--no-browser]` | show Study Stash (the app on a Mac, its own window on Windows), starting its service if needed |
| `client setup` | laptop setup in the terminal instead (`--server`, `--key`, `--mode`, `--yes`, …) |
| `client run [--no-ui]` / `client once [--auto]` | the laptop's background service, or check once |
| `client login` / `login` | sign in to Granola again |
| `doctor` | check this computer's setup and print fixes |
| `update [--check]` | install the newest release and restart the background service |
| `autostart install\|uninstall\|status --role server\|client` | background service: launchd, the Windows Startup folder (with a keep-alive loop and log), or systemd --user |
| `tools --probe` | print Granola's MCP tools and your latest notes (debugging) |

Everything lives in `~/.granola-share` (override with `--home` or `GRANOLA_SHARE_HOME`):
`config.toml` (Mac mini) or `client.toml` (laptop), `tokens.json` (0600), `state.db`,
`client_state.json`, `transcripts/`, `logs/`, `debug/last_meetings.json`. Back up the Mac
mini's `~/.granola-share` and `~/GranolaShare` (Time Machine is enough).

## How sorting works

1. **Folder / title rules.** If the lecture's Granola folder or title matches a class name or
   alias, that class wins, and the model still writes a proper lecture title and topic tags.
2. **Ollama.** Otherwise the model reads the study notes (or Granola's summary, without a
   transcript) and picks a class with a strict JSON schema. Below `min_confidence`, the lecture
   goes to Unsorted.
3. **You.** Move a lecture from its page. Rewriting a summary never undoes that.

## Why this design

Granola's official connectors are read-only, so nothing can move a note into a Granola folder
for you. Instead the laptop pushes finished lectures to your library. Granola's public API
(used by the Obsidian plugins and granola-exporter) needs the paid Business plan; the MCP
connector is free. Details and alternatives are in [docs/DESIGN.md](docs/DESIGN.md).

## Legal and privacy

- **Not affiliated with Granola.** Study Stash is an independent, unofficial project. It isn't made,
  endorsed, or supported by Granola, and "Granola" is their trademark. It gets your notes through
  Granola's official MCP connector, signed in as you. Its one optional feature that automates the
  Granola app is off unless you turn it on ([why](#optional-copy-transcripts-from-the-granola-app)).
- **Recording is your responsibility.** Before recording a lecture, class, or conversation, make
  sure you're allowed to. Recording laws differ, and some places require everyone's consent.
  Schools often have their own rules about recording lectures and sharing notes. Study Stash is for
  your own study notes, not for redistributing lecture content.
- **Your data stays with you.**
  - Lectures, transcripts, and study notes live on your own computers.
  - Study notes are written by a model running on your own computer.
  - Nothing is sent to this project or its author, and there's no analytics.
  - The only outside services are Granola (your own account, over its official connector) and
    GitHub (update checks and downloads).
- **No warranty.** It's provided as is, under the [MIT License](LICENSE). Check your notes against
  the lecture before relying on them, since models make mistakes.
- **Security:** see [SECURITY.md](SECURITY.md) to report a problem.

## Development

```
granola_share/
  config.py     library + laptop TOML config (reader and writer)
  wizard.py     the two terminal setups (every question answerable by a flag)
  ready.py      get the library's computer ready: install/connect Tailscale and Ollama, Windows
                firewall and sleep
  doctor.py     `granola-share doctor` health checks
  oauth.py      discovery, dynamic client registration, PKCE login, refresh
  granola.py    MCP client; discovers tool argument names at runtime
  client.py     laptop watcher: poll → popup → send → "it's filed"
  client_app.py the laptop's Study Stash page: setup and status, on 127.0.0.1 only
  launcher.py   the Study Stash app (native on macOS, else a script), Start Menu, .desktop
  assets/       the icon for Windows, Linux, and the pages (from macos/icon_assets.py)
windows/        build.ps1 → win-x64 + win-arm64 self-contained publishes, setup.iss → both Setup.exe
macos/          build-app.sh → the universal app bundle (launcher.c) + both DMGs, selftest.sh →
                proves a built bundle actually works
  transcript_grab.py  macOS, optional: copy transcripts from the Granola window
  dialogs.py    native popups and notifications (macOS/Windows/Linux)
  autostart.py  launchd / Startup folder / systemd --user
  update.py     release check, upgrade (uv on Mac/Linux, the ready-made folder on Windows), auto-update loop
  store.py      SQLite index, processing queue, Markdown writer
  pipeline.py   background worker: summarize → sort → save
  summarize.py  study notes from a transcript (chunked for long lectures)
  classify.py   rules → Ollama structured output → Unsorted
  ollama.py     find / start / pull (with progress) / try / recommend models
  hostinfo.py   Tailscale address, the laptop install line, port checks
  sync.py       the library's own Granola poll loop (optional)
  web.py, ui.py FastAPI app and its HTML
  cli.py        the granola-share command
tests/          offline tests: .venv/bin/python -m pytest
```

Build the Mac app with `sh macos/build-app.sh` (Xcode command line tools, the .NET SDK; the app and
both DMGs land in `dist/mac`), then check it with `sh macos/selftest.sh "dist/mac/Study Stash.app"`
(a fake microphone and the tiny Whisper model prove it transcribes from inside the bundle). Build
the Windows app and both Setup.exe with `windows\build.ps1` (the .NET SDK, plus
[Inno Setup](https://jrsoftware.org/isinfo.php) 6 — `choco install innosetup` if you use
Chocolatey); it also compiles on a Mac with the .NET SDK, which is a quick check before CI. To try
`install.sh` against a DMG you built, run `STUDYSTASH_DMG=dist/mac/Study-Stash-Laptop.dmg sh
install.sh`.

## Known unknowns

- The exact field names Granola's MCP tools return can change. The client maps common variants
  and saves the last raw payload under `debug/`; run `granola-share tools --probe` to check.
- Copying transcripts from the Granola app depends on the app's own labels (the "Copy transcript"
  button), so a Granola redesign can break it until granola-share is updated. The Study Stash
  page and `doctor` show when the last copy happened. Accessibility access is granted to the
  python3.12 that granola-share runs on, so other programs using that same Python share it.
- On Windows, CI runs the installer, the tests, the library as a background service (with its
  keep-alive loop and log), and the Start Menu shortcut. It also builds the Windows app, checks
  that it starts and shows its first screen, and installs it with Setup.exe. Installing Ollama and
  Tailscale from setup, the firewall rule, the app's sign-in to the library, and the popups
  haven't been used on a real Windows PC yet.
- Lecture recordings capture other people (your professors, classmates). Check your school's
  recording policy.
