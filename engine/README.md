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
| 2 | Granola sign-in and its MCP server | |
| 3 | The library: its web pages, API and setup page | |
| 4 | The laptop: watching Granola and its local page | |
| 5 | Start at login, updates, `doctor`, Windows firewall and sleep | |
| 6 | Mac transcript copying moves into the Swift app | |
| 7 | The installers and apps switch to this engine; Python retires | |

`src/StudyStash.Core` is the engine, and `src/StudyStash.Engine` the command. For now the command only checks that
it would leave this computer's config files as they are (`studystash config-check`). It prints line numbers, never
contents.

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
- **Live Ollama**, only when asked: `STUDYSTASH_LIVE_OLLAMA=qwen3:1.7b dotnet test engine/StudyStash.slnx`.

## Where it knowingly differs from Python

Only where Python lost data or could not read its own files:
- An emoji in a class or library name is written as TOML's `\U0001f9ec`. Python's surrogate pair `\ud83e\uddec` broke `config.toml`.
  The Python engine now writes it the same way.
- Renaming a class only by capitals ("bio 110" to "Bio 110") keeps the note file. On a Mac or Windows the old and new
  paths are the same file, and Python deleted it right after writing it.
- An Ollama error reads as Ollama's own words ("Ollama answered 404: model 'x' not found"). Odd answers from the
  sorting model send the note to Unsorted instead of failing it.
