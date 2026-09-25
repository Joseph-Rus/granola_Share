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
| 4 | The laptop: watching Granola and its local page | |
| 5 | Installing Ollama and Tailscale, start at login, updates, `doctor`, Windows firewall and sleep | |
| 6 | Mac transcript copying moves into the Swift app | |
| 7 | The installers and apps switch to this engine; Python retires | |

The code:
- `src/StudyStash.Core` is the engine.
- `src/StudyStash.Library` has the library's pages and the setup page.
- `src/StudyStash.Engine` is the command. The Study Stash apps still start the Python engine; these are for trying
  this one:
  - `studystash run` serves the library, as `granola-share run` does;
  - `studystash setup --page` serves the setup page;
  - `studystash config-check` checks it would leave this computer's config files as they are. It prints line
    numbers, never contents.

The setup page's buttons that change the computer come with stage 5. That covers installing Ollama or Tailscale, the
Windows firewall rule, keeping a PC awake, starting at login, and installing updates. Until then they give Python's own
"do it by hand" answer, or say they aren't in this engine yet.

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
- An Ollama error reads as Ollama's own words ("Ollama answered 404: model 'x' not found"). Odd answers from the
  sorting model send the note to Unsorted instead of failing it.
