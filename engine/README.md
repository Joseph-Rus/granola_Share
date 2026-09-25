# The C# engine

Study Stash's engine is moving from Python (`granola_share/`) to C# on .NET 10, so the apps no longer need a Python
install. That ends the Windows trouble: uv under OneDrive, console windows flashing, and a 34 MB bundle. On a Mac,
Accessibility permission will go to Study Stash itself instead of python3.12.

The Python engine keeps shipping until the C# one can replace it. Until then, the rule is **one data folder, two
engines**. Both read and write `~/.granola-share` and the notes folder byte for byte the same way:
- `config.toml` and `client.toml`, line for line;
- `state.db`, with the same schema and migrations;
- every note's Markdown file;
- the laptop-to-library wire format.

That way a computer can switch engines and keep its library as it is.

## What's here

| Stage | What | State |
|---|---|---|
| 1 | Config, lectures, the database and note files, Ollama notes and sorting, the pipeline | done |
| 2 | Granola sign-in, reading lectures from its MCP server, and the library's own sync | done |
| 3 | The library: its web pages, the laptop's API, and the setup page (ASP.NET Core) | done |
| 4 | The laptop: watching Granola and its local page | done |
| 5 | Installing Ollama and Tailscale, start at login, updates, `doctor`, Windows firewall and sleep | done |
| 6 | Mac transcript copying moves into the Swift app | |
| 7 | The installers and apps switch to this engine; Python retires | |

The code:
- `src/StudyStash.Core` is the engine.
- `src/StudyStash.Library` has the web pages: the library's, its setup page, and the laptop's Study Stash page.
- `src/StudyStash.Engine` is the command. The Study Stash apps still start the Python engine; these are for trying
  this one:
  - `studystash run` serves the library, as `granola-share run` does. `serve` does the same without the library's own
    Granola sync, and `init`, `login`, `logout`, `sync [--once]` and `tools [--probe]` do what the Python engine's do;
  - `studystash client run` is the laptop's background service: the watcher and its Study Stash page.
    `client open [--install]` starts it if needed and shows the page (what the apps run), and `client once` and
    `client login` do what the Python engine's do;
  - `studystash setup --page` serves the setup page, with every button working: installing Ollama and Tailscale,
    the Windows firewall rule, keeping a PC awake, and starting the library at login;
  - `studystash doctor` checks every piece of a setup and says how to fix what's broken, word for word as the Python
    engine does;
  - `studystash autostart install|uninstall|status --role server|client` runs the library or the laptop's watcher in
    the background. It uses the Python engine's service names and files (`com.granola-share.server` on a Mac,
    `granola-share-server.cmd` in the Windows Startup folder, `granola-share-server.service` on Linux, and the same
    for `client`), so installing either engine's service replaces the other's;
  - `studystash update [--check]` installs a new release, and `run` checks for one every six hours when
    `auto_update` is on;
  - `studystash config-check` checks it would leave this computer's config files as they are. It prints line
    numbers, never contents.

The laptop's watcher keeps the Python engine's record of what it sent (`client_state.json`), byte for byte, so a laptop
can switch engines without sending anything twice. Copying transcripts out of the Granola window stays with the Python
engine until the Study Stash app takes it over (stage 6): the watcher here reads what was copied, from the same
`transcripts` folder. The terminal wizards (`setup` without `--page`, `client setup`) aren't here: the apps use the
pages.

Messages still name the `granola-share` command: that's what people type today, and stage 7 decides what it runs.

### Updates

The engine is one folder. Each release is meant to carry a zip of it for each computer, named
`Study-Stash-engine-<mac|windows|linux>-<arm64|x64>.zip`, with `study-stash-engine.txt` (its version) beside the
program. CI starts attaching those in stage 7; until then an update finds nothing to install and says so.

An update downloads the zip, unpacks it next to the folder in use, and runs the new copy's `version` to check it works
on this computer. Only then does it swap the folders. On a Mac or Linux it swaps them at once. Windows won't replace a
running program, so there a helper waits for the engine to stop, swaps the folders, and starts the services again.
The Study Stash apps update along with it, as with the Python engine. A build folder (no `study-stash-engine.txt`) is
never updated.

### What changes the computer is off unless asked for

Tests replace everything that installs, starts or changes something, and nothing does so by default. Setup's
installers are off unless the real setup page turns them on (`SetupHost.ThisComputer()`). Installing a service,
running an installer, or changing the firewall or sleep takes its runner and folders explicitly. A test that forgets
one fails instead of installing something.

## Tests

```sh
dotnet test engine/StudyStash.slnx
```

- **Golden files** (`tests/StudyStash.Core.Tests/Golden/`): written by the Python engine itself, with
  `python engine/tests/golden.py`. CI regenerates them and fails if they change, so a change to the Python engine
  can't quietly leave this one behind.
- **Cross-engine** (`CrossEngineTests`, `tests/crosscheck.py`): Python builds a library, C# reads every row, then moves,
  deletes, adds and files notes, and Python checks every row and file C# wrote. It uses the repo's `.venv`, or set
  `STUDYSTASH_PYTHON`.
- **Pages** (`LibraryWebTests`, `LibrarySetupTests`): the Python engine serves 19 library pages and draws the setup
  page in four states, with the nonce pinned. The C# engine must serve the same bytes. The only exception is a
  lecture's note text: Markdown libraries differ, so that's checked for safety instead.
  - The pages' CSS and JavaScript are copied from Python by `golden.py` into `src/StudyStash.Library/PageText.cs`, so
    there's nothing to retype.
- **The laptop** (`CrossEngineTests`, `tests/laptop_check.py`): the Python engine's own laptop code talks to a C#
  library over HTTP. It checks health, a wrong password, sending a lecture, asking if it's filed, and setup's checks
  that the library is running.
- **The laptop** (`LaptopTests`, `LaptopWebTests`): the Python engine's watcher runs two checks (ask, send, skip,
  wait, filed), and the C# one must ask, send and notify the same, and write the same `client_state.json` bytes. Its
  Study Stash page is drawn in 11 states on a Mac, Windows and Linux, with its status JSON and the "Open your library"
  page; copied transcripts are read and matched to their lectures as Python does.
- **Laptop to library, both ways** (`CrossEngineTests`, `tests/laptop_flow.py`): the Python engine's watcher sends to
  a C# library until it's filed, the C# watcher sends to the real Python library (`granola-share run`), and the C#
  laptop connects from its own page to a C# library and sends until the notes are filed.
- **The platform** (`AutostartTests`, `ReadyTests`, `UpdaterTests`, `DoctorTests`): the Python engine writes the
  launchd and systemd files and the firewall rule, and runs `doctor` in 20 scenarios (the library's and the
  laptop's). The C# engine must write and say the same bytes; `golden.py` keeps the scenarios, so a new one needs
  no C#. Updates are tested for real, with a stand-in engine in a scratch folder: download, unpack, check, swap.
- **The background service, live**, only when asked: `STUDYSTASH_LIVE_SERVICE=1 dotnet test engine/StudyStash.slnx`
  on a Mac or Windows. It installs the library as the real service from a scratch folder on a spare port, checks it
  answers, restarts, and goes away. It won't run where a library service is already installed or running. CI runs it
  on Windows, where the Startup file hands over to a copy with no window.
- **Signing in** (`OAuthTests`): checked against the [MCP authorization spec](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization)
  and the RFCs it builds on:
  - RFC 7636's own PKCE example;
  - the discovery order of RFC 9728 and RFC 8414;
  - `resource` (RFC 8707) on every request;
  - a fake sign-in server that serves Granola's real published metadata and redeems a code only with its verifier;
  - a real browser-style round trip through the localhost callback.
- **Live Ollama**, only when asked: `STUDYSTASH_LIVE_OLLAMA=qwen3:1.7b dotnet test engine/StudyStash.slnx`.
- **Live Granola**, only when asked: `STUDYSTASH_LIVE_GRANOLA=1 dotnet test engine/StudyStash.slnx`.
  - It fetches Granola's public sign-in metadata. On a computer that's signed in, it also reads tools, the account and
    recent lectures with that sign-in.
  - It's read-only: it never registers an app or refreshes a token. Granola rotates refresh tokens, so a refresh here
    would sign the computer's own Study Stash out.
  - It prints counts, never names or titles.

## Where it knowingly differs from Python

Only where Python lost data, could not read its own files, or skipped part of a spec:
- An emoji in a class or library name is written as TOML's `\U0001f9ec`. Python's surrogate pair `\ud83e\uddec` broke `config.toml`.
  The Python engine now writes it the same way.
- Renaming a class only by capitals ("bio 110" to "Bio 110") keeps the note file. On a Mac or Windows the old and new
  paths are the same file, and Python deleted it right after writing it.
- The loose XML reader keeps attribute values in single quotes. Python's lost them; it keeps them now too.
- Signing in follows the parts of the MCP spec the Python engine skipped. None of these change anything with Granola
  today:
  - it tries the MCP server's metadata under its own path before the root;
  - it falls back to OpenID discovery;
  - it refuses a sign-in server that doesn't advertise S256 PKCE;
  - the callback also answers on `::1`, since `localhost` may mean either.
- On pages with no sidebar item of their own (search, not found), Python marked Log out as the current page. Both
  engines mark nothing now.
- Note text goes through Markdig instead of Python-Markdown, with the same rules: no raw HTML, only http, https and
  mailto links, and math left for KaTeX. Markdown's own attribute syntax is off, so a note can't add an HTML attribute.
- A class with "/" in its name opens. Python's web framework decoded "%2F" before routing, so it couldn't.
- Windows' Startup file runs the engine itself, which hands over to a copy of itself with no window and restarts it
  when it stops. Python used pythonw with `start /min`, but this engine is a console program, and its window would
  have stayed in the taskbar.
- A batch file whose paths aren't plain ASCII (a home folder named José) switches cmd to UTF-8 first. Python's
  Startup file broke there.
- The firewall script unblocks this engine's program, where Python's unblocked Python.
- Connecting Tailscale from the setup page gives up after 10 minutes when nobody signs in. Python's waited forever,
  and its button stayed off until the page restarted.
- An update checks that the new engine runs on this computer before replacing the old one. It restarts only the
  services that run this engine, and leaves the Python engine's alone.
- The laptop's status page asks to allow transcript copying only where copying can run. Python asked even where it
  couldn't; it doesn't now either.
- A laptop with no way to show a popup (Linux without zenity) leaves the lecture waiting to be asked about. Python's
  last resort, a Tk dialog, said no without Tk, which skipped the lecture.
- The Start Menu entry, before the Windows app is installed, opens this engine minimized: it's a console program,
  where Python used pythonw.
- An Ollama error reads as Ollama's own words ("Ollama answered 404: model 'x' not found"). Odd answers from the
  sorting model send the note to Unsorted instead of failing it.
