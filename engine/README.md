# The engine

Study Stash's engine and app, on C# and .NET 10. `src/StudyStash.Core` is the engine itself:
config, the lecture database and note files, the AI providers, Canvas, recording and
transcription, autostart, and updates. `src/StudyStash.Library` has the web pages — the library's,
its setup page, and the laptop-facing ingest API — on ASP.NET Core. `src/StudyStash.Engine` is the
`studystash` command line. `src/StudyStash.Audio` has the microphone and Whisper bindings
(Whisper.net; Metal on Apple silicon, Vulkan or the CPU on Windows). `src/StudyStash.App` is the
Avalonia app: menu bar/tray, quick panel, full window, setup — the app runs `studystash`'s own
commands for its library and MCP server, so there's one program.

## Commands

- `studystash run` serves the library and checks for updates every few hours; `serve` does the
  same without the update check.
- `studystash init` writes a starter `config.toml`.
- `studystash setup --page` serves the guided setup page, with every button working: installing
  Ollama and Tailscale, the Windows firewall rule, keeping a PC awake, starting the library at
  login.
- `studystash doctor` checks a setup and says how to fix what's broken.
- `studystash mcp` is the MCP server for Claude Code and Claude Desktop, over stdin and stdout.
- `studystash ai [use PROVIDER | test [PROVIDER] | ask QUESTION]` picks, tests, or asks an AI
  engine from a terminal.
- `studystash autostart install|uninstall|status --role server` runs the library in the background
  (launchd on a Mac, the Windows Startup folder, systemd `--user` on Linux).
- `studystash update [--check]` installs a new release.

The app itself (no command, or `--background` at login) opens the menu bar/tray, quick panel, and
main window; give it one of the commands above and it's the engine instead.

## Tests

```sh
dotnet test engine/StudyStash.slnx
```

- **`StudyStash.Core.Tests`** (xunit 2): the engine and library, offline and fast.
- **`StudyStash.App.Tests`** (xunit.v3 + Avalonia.Headless): the app's views, drawn headless.
- **Golden files** (`StudyStash.Core.Tests/Golden/`): fixed expectations — config files, rendered
  pages, service files, note text, doctor's wording — first written by the Python engine this one
  replaced, and now edited by hand alongside the code they check. See a fixture's own comment or the
  test that reads it before changing one.
- **Pages** (`LibraryWebTests`, `LibrarySetupTests`): every library and setup page against
  `Golden/pages.json`, byte for byte except a lecture's own note text (Markdown libraries differ
  enough that it's checked for safety instead).
- **The whole flow** (`LectureFlowTests`): a recording, filed by the timetable when it has no class,
  sent to a library, written up and sorted by a fake AI, and read back through Claude's own tools.
- **The platform** (`AutostartTests`, `ReadyTests`, `UpdaterTests`, `DoctorTests`): the launchd,
  systemd and Windows Startup files; installing Tailscale, Ollama, a Windows firewall rule;
  `doctor` in every scenario; a real update (download, unpack, check, swap) with a stand-in engine
  in a scratch folder.
- **Signing in for Claude** (`ClaudeTests`): OAuth 2.1 with PKCE, dynamic client registration,
  token refresh and rotation, against `/.well-known/oauth-authorization-server`.
- **Rendering the app's screens** (`STUDYSTASH_SHOTS=<dir> dotnet test engine/tests/StudyStash.App.Tests`):
  draws every surface, light and dark, for each colour theme, to PNGs under `<dir>`, for comparing
  against the design.

### Live tests (need something real; CI runs them, your own computer shouldn't)

- `STUDYSTASH_WHISPER_MODEL=path/to/ggml-tiny.bin dotnet test engine/StudyStash.slnx` —
  transcribes a real recording with a real Whisper model.
- `STUDYSTASH_LIVE_OLLAMA=qwen3:1.7b dotnet test engine/StudyStash.slnx` — notes, sorting, and the
  length cap against a real Ollama.
- `STUDYSTASH_LIVE_SERVICE=1 dotnet test engine/StudyStash.slnx` (Mac or Windows only) — installs
  the library as the real background service on a spare port, checks it answers, restarts, and
  removes itself. Refuses to run if a library service is already installed, since its label is the
  real one. **Never run this on a computer with a real Study Stash install** — it could disturb it.

## Updates

Each release carries a zip of the engine for each computer,
`Study-Stash-engine-<mac|windows|linux>-<arm64|x64>.zip`, with `study-stash-engine.txt` (its
version) beside the program. `studystash update` downloads it, unpacks it beside the folder in
use, and runs the new copy's `version` to check it works on this computer before swapping the
folders in. Windows can't replace a running program, so there a helper waits for the engine to
stop, swaps the folders, and starts the services again. A build folder (no
`study-stash-engine.txt`) is never updated.

## What changes the computer is off unless asked for

Tests replace everything that installs, starts, or changes something, and nothing does so by
default. Setup's installers are off unless the real setup page turns them on
(`SetupHost.ThisComputer()`). Installing a service, running an installer, or changing the firewall
or sleep takes its runner and folders explicitly, so a test that forgets one fails instead of
installing something.
