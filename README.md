# Study Stash

Your own lecture library: record on your laptop, and it's transcribed, filed under the right
class, and turned into study notes on your own always-on computer. Free and open source, and
everything stays on your own computers.

Study Stash is one app for both jobs, on Mac and Windows:

- **Your laptop** (the computer you carry to class): a menu bar (Mac) or tray (Windows) app.
  Click **Record**, and it listens to the microphone and transcribes what it hears locally, with
  Whisper large-v3 (Metal on Apple silicon, Vulkan or the CPU on Windows) — the audio never leaves
  the laptop. It looks at your timetable to know which class is on, then sends the lecture to your
  library once it's reachable. A quick panel (⌥Space on a Mac, Alt+Shift+Space on Windows) searches
  your lectures from anywhere, and the full window browses classes, lectures, notes, Canvas work,
  and lets you ask an AI about any of it.
- **Your library** (a home server: a Mac mini, an old laptop, any computer that stays on): run
  `StudyStash serve`, and it stores each lecture under its class, writes study notes with an AI
  engine, mirrors your Canvas courses through a small Chrome extension, and serves a web page and
  MCP so Claude can read your lectures too.

```
your laptop                                     your library
┌───────────────────────────┐  POST /api/ingest  ┌──────────────────────────────────────┐
│ record → Whisper (local)   │ ──────────────────▶│ queue → study notes (your AI engine)  │
│  ↳ filed by the timetable  │   over Tailscale    │ → sort into a class → Markdown file   │
└───────────────────────────┘                     │ web page, Claude/MCP, Canvas mirror    │
                                                   └──────────────────────────────────────┘
```

## AI engines

Study notes and sorting are written by whichever engine you pick in Settings: Ollama, running
locally and for free, or Claude Code, Codex, or Gemini's CLI on the library's computer, signed in
with your own account and plan. Pick one engine for everything, or a different one for notes,
sorting, or answering questions. Without an engine, a lecture is still filed by its class and
title, and keeps its transcript without study notes.

## Canvas

Settings → Canvas connects your school's Canvas with a small Chrome extension (in `extension/`)
that reads Canvas with your own sign-in — no Canvas API token, which many schools turn off — and
hands what it reads to the library. The library mirrors each class: assignment instructions and
rubrics, your submissions with their scores and comments, module files and pages, announcements.

## Claude and MCP

Your library speaks [MCP](https://modelcontextprotocol.io), so Claude can read your lectures and
notes directly:

- **Claude Code or Claude Desktop**, on the library's own computer: `studystash mcp` over stdio.
- **claude.ai**, from anywhere: HTTP with OAuth 2.1, behind a [Tailscale Funnel](https://tailscale.com/kb/1223/funnel).

## Where your data lives

Everything is under `~/.study-stash` (or `--home`, or the `STUDYSTASH_HOME` environment
variable): `config.toml` or `client.toml`, the lecture database, and the notes themselves as plain
Markdown files under a class folder you can open, back up, or sync however you like. An install
from before this folder was renamed keeps using `~/.granola-share` instead, so nothing has to
move.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com).

```sh
dotnet build engine/StudyStash.slnx
dotnet test engine/StudyStash.slnx
dotnet run --project engine/src/StudyStash.App -- --home /some/temp/dir
```

`macos/build-app.sh` builds "Study Stash.app" and a DMG (Xcode's command line tools too).
`engine/README.md` has more on the engine's projects, tests, and fixtures.

## License

[MIT](LICENSE). No warranty: it's provided as is, so check your notes against the lecture before
relying on them, since models make mistakes. See [SECURITY.md](SECURITY.md) to report a security
problem.
