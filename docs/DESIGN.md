# Study Stash: design

Study Stash sends the lectures you record in Granola to a library on your own always-on computer
(a Mac mini, say). There, a local model writes study notes from each lecture and files it under
its class. It's for one person and their own lectures. It's an independent project, not
affiliated with or endorsed by Granola.

```
laptop (records in Granola)                     library (Mac mini or any always-on computer)
  watcher: Granola MCP → finished lectures        /api/ingest → queue (SQLite)
  optional: copy transcript from the app    ──▶   pipeline: study notes (Ollama) → sort → Markdown
  Study Stash app / page on 127.0.0.1             web page + the app's Library tab, over Tailscale
```

The code names predate the name Study Stash (the `granola-share` command, the `granola_share`
package, `~/.granola-share`, the `pool_*` config keys). They stay as they are so existing installs
keep working.

## Getting lectures out of Granola

- **The official Granola MCP server** is the only source of notes. The laptop signs in with
  browser OAuth (PKCE, dynamic client registration) and polls `list_meetings` / `get_meetings`.
  Tool argument names are discovered at runtime, and results arrive as XML-ish text. `granola.py`
  normalizes both. This works on Granola's free plan.
- **Transcripts** come from MCP's `get_meeting_transcript` on paid plans. On free plans that tool
  says transcripts need a paid tier, and the lecture keeps Granola's summary.
- **Optional, off by default: copying the transcript from the Granola app** (macOS). With it on,
  the laptop presses Granola's own "Copy transcript" button over the Accessibility API when a
  recording ends. This automates the Granola app, which may go against Granola's terms of service
  (they forbid scraping content "through use of manual or automated means"). So it's never on
  unless the user turns it on, and every place that offers it says so. Details are below.
- **Not used:** Granola's local files, or any unofficial API. Both would mean working around
  Granola's own protections, and both break with app updates.

## The library

- `/api/ingest` only queues a lecture (bearer: the library password). `pipeline.py` then works
  through the queue in the background: study notes, then sorting, then saving. It survives restarts
  and never writes over a class a person picked.
- **Study notes** (`summarize.py`): the model you pick writes an overview, key concepts, worked
  examples and formulas, announcements, and review questions from the transcript. Long transcripts
  are summarized in parts, then merged, sized to the model's context. Without a transcript, the
  lecture keeps Granola's summary.
- **Sorting** (`classify.py`): Granola folder or title rules first, then the model with a strict
  JSON schema. Below `min_confidence`, the lecture goes to Unsorted.
- Each lecture is a Markdown file under `~/GranolaShare/<Class>/`, indexed in SQLite with search.
- The library can also sign in to Granola itself (`server_sync`, off by default). It only adds
  lectures it hasn't seen, so it never writes over one the laptop sent with its transcript.

## The laptop

- The watcher (`client.py`) polls MCP, sends each finished lecture (automatically, or after asking),
  retries without asking again when the library can't take one, and sends one notification when the
  library has filed it.
- **Problems show where people look.** A refused password or an unreachable library turns the
  Library row red on the laptop's page, with the fix beside it, plus one notification when only the
  user can fix it.
- **The laptop's page** (`client_app.py`, on 127.0.0.1) does setup and shows status:
  - it listens on loopback only, and checks the Host header against DNS rebinding;
  - it needs a per-install token, passed by the app and then kept in a SameSite=Strict cookie;
  - every POST must carry a custom header, which cross-site forms can't send.
- **Transcript copying, when it's on** (`transcript_grab.py`):
  - CoreAudio shows when Granola's helper stops using the microphone. That's the end of a recording,
    wherever Granola's window is.
  - The copier acts only with Granola in front and after 2 s without typing. It restores the user's
    clipboard, focus, and selection afterwards.
  - If Granola isn't in front, one "Save it with its transcript?" popup brings Granola forward and
    returns to the previous app.
  - macOS applies the Accessibility switch only to new processes. So the service asks a fresh
    process whether it's allowed, and restarts itself (at most once every 30 minutes) when it is.

## Install, updates, and the Mac app

- The installers run `uv tool install` from the newest GitHub release, with a uv-managed Python
  3.12, so there's no git and no system Python. Services run under launchd, systemd `--user`, or the
  Windows Startup folder. Windows has nothing like launchd's KeepAlive, so there the service runs
  under a small keep-alive loop that restarts it and writes its log (pythonw has no console).
- **Library setup gets the computer ready first** (`ready.py`): Tailscale and Ollama, each installed
  from its official download if missing (asking first), started, or connected. Tailscale's
  installers need an administrator, so the installer window opens and setup waits. Models download
  through Ollama's API, so no `ollama` command is needed, and each one answers once before setup
  moves on. On Windows, setup adds one firewall rule for the library's port, open only to
  Tailscale's range (100.64.0.0/10) and the local subnet, through Windows' own permission prompt.
- **The Windows app** (`windows/StudyStash.cs`) is the Mac app's twin: a WinForms window around
  two WebView2 views (This PC and Library), on .NET Framework 4.8, which every Windows 10 and 11
  has, so it's a 0.5 MB zip. It fills in the library's password itself, routes links like the Mac
  app, and installs the helper from its welcome screen. The installer, `client open --install`,
  and auto-update all put it in `%LOCALAPPDATA%\Programs\Study Stash`. Windows can't overwrite a
  running program but can rename it, so an update moves files in use aside to `*.old`, which the
  app deletes when it next starts.
- **Four installers, two apps.** Each app also ships as its library computer's copy: "Study Stash
  Library" (Info.plist `StudyStashRole` on a Mac, `study-stash.ini` on Windows). It shows only the
  library, and before there is one it shows the library's setup as a page (`library_setup.py`,
  `granola-share setup --page`): the terminal wizard's six steps as fields and buttons, with background
  jobs (and progress bars) for installs and downloads. Answers stay in a draft until Finish writes
  config.toml, which is how the app knows the library exists. Nobody needs a terminal.
- **No console windows on Windows.** The service and the apps have no console, so every console program
  they start (tailscale, PowerShell, cmd) would flash a window; the CLI starts them with CREATE_NO_WINDOW.
- **Windows doesn't use uv.** uv's Python install fails under OneDrive's Files On-Demand (the link it
  makes gets "untrusted mount point", os error 448). So CI builds one ready-made folder instead
  (`windows/bundle.ps1`): python.org's embeddable Python 3.13, signed by the PSF, with every package
  installed beside it. install.ps1 unpacks it into `%LOCALAPPDATA%\Programs\granola-share`, and
  writes a `granola-share.cmd`. An update unpacks the next one beside it, and a helper swaps the
  folders once nothing runs from the old one. A uv install from before moves over on its next update.
- **Without the app** (Linux, or before it's downloaded) the page opens in an Edge or Chrome app
  window, and "Open your library" signs in by posting the saved password to the library's login
  form from a loopback-only page.
- **The laptop checks its own needs**: Granola (the app that records; the library's computer
  doesn't need it) and Tailscale, shown at the top of setup with a fix button for each, and in
  `doctor`.
- CI tests on macOS, Linux, and Windows, runs the real installer on each, builds the Mac app, and
  publishes `v<version>` when `__version__` changes. Installed copies check every six hours,
  install, and restart. Windows uses a detached helper, because it locks running files.
- **The Mac app** (`macos/StudyStash.swift`) is a small AppKit window around the pages the service
  serves, so there's one UI:
  - a toolbar with This Mac and Library tabs, real menus, and native confirm dialogs;
  - downloads into Downloads, and Copy through the pasteboard;
  - automatic sign-in to the library with the saved password.

  CI builds a universal binary and attaches a DMG and a zip to each release. The installer and
  auto-update fetch the zip themselves, and nothing fetched that way is quarantined. Only a DMG
  from the browser gets macOS's "Open Anyway" step, which is unavoidable without a paid Developer ID.
- The Info.plist has only `NSAllowsArbitraryLoads`, because adding `NSAllowsLocalNetworking` makes
  macOS ignore it. With both, the library's `*.ts.net` address was blocked.
- Tests run with HOME pointed at a temp folder, so they can't touch the real Applications folder or
  LaunchAgents.

## Look

Apple's design throughout:
- the system font and system colors, in light and dark;
- a Notes-style sidebar;
- grouped rounded lists with inset hairlines;
- switches, segmented controls, and popup menus that apply as soon as you pick;
- one of Apple's tag colors per class.

The icon is a lime folder on a dark tile with lines of notes on it. It doesn't use Granola's logo.
The README's screenshots come from `macos/tour.py`. It runs made-up lectures through the app's tour
mode, where the app captures its own window, and blurs addresses, passwords, and paths.

## The AI, Canvas, chat and undo

- **Providers** (`Core/Ai/Providers.cs`): each is its own CLI, run as a child process with JSON output, so it
  keeps the person's own sign-in and plan and the library never holds a key. Claude Code (`claude -p`, Edit and
  Write allowed only inside the working folder), Codex (`codex exec`, read-only or workspace-write), Antigravity
  for Gemini (`agy -p`, plan or accept-edits; the library adds read-only command rules to its settings, since
  print mode can't ask). Ollama answers plain questions directly; as an agent it runs through Codex (`--oss`).
  `ai.json` says which does what, so `config.toml` stays as the Python engine writes it.
- **Canvas** (`Core/Canvas`): a Chrome extension (in `extension/`, embedded in the engine and written out for
  Chrome to load) is a read-only fetch proxy. It asks the library for work (`/api/v2/canvas/work`) with its own
  key, fetches Canvas URLs with the browser's session, and posts the answers back. It refuses anything but the
  school's Canvas and its file store, and checks often only while work is queued. The sync is a persisted job
  queue (`crawl.json`); reads an AI asks for (the MCP canvas tools) jump the queue.
- **Chat and history** (`Core/Ai/Chats.cs`, `History.cs`): a chat turn runs the agent AI in the library folder
  with the engine's own MCP server. With edits allowed, the library's text is committed first, then exactly the
  files the turn changed are one commit (trailer `Study-Stash-By:`), which History lists and Undo reverts. The
  library folder's git repository is local only.
- **Capture** asks the AI only for a plan (class and title per item, as JSON); the engine moves the files, so
  it works with any AI and needs no write permission.

## Naming, terms, and privacy

- **The name doesn't use Granola's.** Granola is named only to say what the app works with, and the
  README, the app's About box, and Settings all say it isn't affiliated.
- **Transcript copying is opt-in,** for the terms-of-service reason above.
- **Everything stays on the user's computers** unless they pick Claude, ChatGPT or Gemini, which then read
  what they're asked about under the user's own account. Lectures and notes live on the library computer,
  and by default study notes are written by a local model. The only outside services are Granola (the user's
  own account, over its official MCP server) and GitHub (update checks and downloads). There's no
  analytics.
- **Recording is the user's responsibility:** consent laws, and their school's rules on recording
  and sharing lectures.
