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
- **One Windows app installs as either role.** Both Setup.exe files share one Inno `AppId` and
  install per-user into `%LOCALAPPDATA%\Programs\Study Stash`; whichever one you run last writes
  `study-stash.ini`'s `role=`, so installing the other role over an existing install is just an
  in-place upgrade, never a second copy.
- **Four installers, one app per system.** The laptop and library installers hold the same
  program; only the role preset differs (`StudyStashRole` in the Mac bundle's Info.plist,
  `study-stash.ini` on Windows). `Apps.RolePreset` reads it once, on first run, so setup already
  knows which one it is; after that the role lives in the app's own settings.
- **No console windows on Windows.** The service and the apps have no console, so every console program
  they start (tailscale, PowerShell, cmd) would flash a window; the CLI starts them with CREATE_NO_WINDOW.
- **Updates swap the installed copy.** An installed app checks GitHub for a new release, downloads
  the matching installer, and checks its SHA-256 against `SHA256SUMS.txt`. On a Mac it mounts the
  DMG, stages the new `.app` beside the old one, swaps them, and restarts any service that runs
  from the bundle. On Windows it runs the new Setup.exe silently, which closes the running app and
  relaunches it. Either way, only an installed copy updates itself; a build folder or `dotnet run`
  never calls GitHub.
- **Without the app** (Linux, or before it's downloaded) the page opens in an Edge or Chrome app
  window, and "Open your library" signs in by posting the saved password to the library's login
  form from a loopback-only page.
- **The laptop checks its own needs**: Granola (the app that records; the library's computer
  doesn't need it) and Tailscale, shown at the top of setup with a fix button for each, and in
  `doctor`.
- CI tests the engine on macOS, Linux, and Windows, builds and self-tests both apps (a fake
  microphone, the tiny Whisper model, real windows drawn headless), installs each with its own
  installer, and publishes a release only when `StudyStashVersion` in `Directory.Build.props` is
  new. The running app checks every six hours and installs when nothing is recording; Windows
  hands off to its own Setup.exe, which relaunches the app once it's done.
- **The Mac app is one universal bundle**, started by a tiny native launcher
  (`macos/launcher.c`) at `Contents/MacOS/StudyStash` — the spot macOS reads the real Info.plist
  from (`LSUIElement`, the microphone usage strings), so it can't be a per-architecture `exec`.
  The launcher `dlopen`s the matching architecture's `libhostfxr.dylib` under
  `Contents/MacOS/{arm64,x64}` and starts .NET in-process; a per-arch self-contained publish can't
  be `lipo`-merged, so this is the trick that makes both look like one program. It's signed ad hoc
  (no paid Developer ID) with the hardened runtime, `disable-library-validation` (so it can still
  load its own unsigned dylibs), and a microphone entitlement.
- The Info.plist carries `NSMicrophoneUsageDescription` and `NSAudioCaptureUsageDescription`
  (without them macOS kills the app on first mic use), `StudyStashRole` for the role preset, and
  `CFBundleShortVersionString`/`CFBundleVersion` set to `StudyStashVersion` so an installed copy
  can report its own version without running .NET.
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
