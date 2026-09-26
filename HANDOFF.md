# Study Stash handoff — what's done, what's in progress, what's next

Read this first. It's the state of the project as of Sat 26 Sep 2026, after the overnight run
finished all 12 workstream lanes but was cut off (the API spend limit) before the integration
phase ran.

Quick orientation:

- The product is one C# / .NET 10 / Avalonia desktop app for Mac and Windows, installed as
  **laptop** or **library**. It records lectures, transcribes locally with Whisper, files them by
  class, writes notes with an AI engine, and mirrors Canvas. Granola and the Python engine are
  retired; the repo is C# only.
- Two people work on this: the owner and Eli (who ported Canvas + AI from Marginalia, the
  original Python app).
- The tooling this repo needs (the .NET 10 SDK, the design refs, the Tabler icon set) is kept
  OUTSIDE the repo, in `~/study-stash-night/` — see "Local tooling" below. Without it the
  solution can't build on this machine (the system `dotnet` is 8.x).

## Where things stand

All 12 `night/*` branches are committed and pushed to GitHub (origin =
`https://github.com/Joseph-Rus/study-stash.git`). Nothing is on `main` yet. The run's last
verified state for every branch: every lane finished its task list, each with a warning-free
build and its full test suite green (audited in the run journal,
`~/study-stash-night/plans/run-journal.jsonl`).

| Branch | Lane | State |
|---|---|---|
| `night/base` | base | green; 19 test commits beyond `main` |
| `night/wave2-base` | merge base | green; early wave-1 work merged (recorder + shell + shots) |
| `night/ws1-no-granola` | no Granola | **done** — all 7 tasks (T1–T7) |
| `night/ws2-theme-glass` | theme + Liquid Glass | **done** — all 8 tasks (T1–T8) |
| `night/ws3-canvas-ui` | Canvas screens (a) | **partial** — T1, T2 done; T3 in progress, unverified |
| `night/ws3b-canvas-ui` | Canvas screens (b) | T2-fix + T5 (connect flow) done |
| `night/ws3-canvas-ui` note | — | T3's Due list/assignment views exist but are wired to fixtures only, "tests not yet run" at last checkpoint |
| `night/ws4-ai-ui` | AI screens (a) | T1–T4 done (engines pane, setup step, ask bar + engine menu) |
| `night/ws4b-ai-ui` | AI screens (b) | T6 (AI tool access) done |
| `night/ws5-canvas-data` | Canvas data | T1–T4 + T7 API done; **partial** — T5/T6 structured crawl never ran |
| `night/ws5-canvas-data` note | — | Crawl writes modules.md/announcements.md as plain Markdown; never asks Canvas for quizzes/discussions/planner; the API endpoints for modules/files/announcements/pages answer with the right JSON shape but empty lists against a real sync |
| `night/ws6-app-runs` | app runs | T1–T7 done; **T8 (full self-test) not done** |
| `night/ws6b-app-runs` | app runs (b) | T7 mostly done (placement, menu-bar icon); unverified ends: the full self-test path (needs ws6's T8), multi-monitor Y-flip (one display available), right-click menu not exercised live |
| `night/ws7-ship` | installers + CI | **done** — T1–T6 (installers, updates, CI, retire wrappers) |

## What the next agent should do, in order

1. **Merge all lanes into `night/integration`.** The exact recipe (worktree creation, merge
   order, conflict expectations, verify steps) is in `~/study-stash-night/plans/night-tail.js`.
   Merge order: ws1 → ws5 → ws6 → ws6b → ws2 → ws7 → ws3 → ws3b → ws4 → ws4b.
   The `night/wave2-base` branch already shows how Recorder.cs / Shell.cs / Shots.cs conflicts
   were resolved once, use its merge commits as a reference.
2. **Wire the Canvas and AI screens into the app.** The wiring instructions live in
   `docs/handoff/ws3-WIRING.md` and `docs/handoff/ws4-WIRING.md` in this repo, and were copied
   from the run's scratchpad (originals in `~/study-stash-night/plans/`). They name every
   control, model, and the files WS2/WS6 owned that integration must edit.
3. **A light review** (build + tests, look at a few shots, at most 5 must-fix problems; fix
   only build/test failures, crashes, or something clearly broken). The run was told to keep
   the review light — keep it light.
4. **Then stop and report.** Don't open a PR to main and don't merge to main without the owner
   saying so. CI runs on pushes to main and PRs only (pushing `night/*` runs nothing).
4b. **The Tabler icon swap was deliberately deferred** (the owner said "remove the tabler icon
   swap for now"). The task spec and `tools/tabler.py` are preserved (`~/study-stash-night/ICONS.md`,
   `~/study-stash-night/plans/ICONS.md`, `tools/tabler.py` in the durable folder) — run it
   whenever the owner asks.
4c. **The Co-Authored-By lines on `night/ws7-ship` are allowed** — the owner said keep them
   (they mark work done under the earlier "no AI attribution" rule, which he waived). Don't
   rewrite history to strip them; don't add new ones either (the standing rule for new commits
   is: no `Co-Authored-By:` / AI attribution in commit messages, code or docs).

## The unfinished tasks, in detail

These are the lane tasks that never finished (the run ran out of API credit mid-wave). They're
ordered by impact. The full task specs with acceptance criteria live in
`~/study-stash-night/plans/<lane>-PLAN.md`.

### 1. WS5 T5/T6 — the structured Canvas crawl (highest impact)

The API exists but its data source is missing. `Crawl.cs` (in the library) writes modules.md
and announcements.md as plain Markdown, and never asks Canvas for quizzes, discussions or
planner items. So `GET /api/v2/canvas/modules`, `/files` (the files area), `/announcements` and
`/pages` answer with the correct JSON shape (tested for shape) but empty lists against a real
sync, and the Due list only ever carries assignments (Todos stays empty).
What to do: implement WS5 PLAN T5 (modules: every item kind, module folders by id, Files area
mirroring when Canvas allows) and T6 (announcements with read state, quizzes, discussions,
planner, `Due.Build` grouping). The full specs are in `~/study-stash-night/plans/ws5-PLAN.md`
(T5 at §"T5. Modules show every item as the design does", T6 at §"T6. Appointments, quizzes,
discussions and the Due list").

### 2. WS6 T8 — the full self-test

The self-test records, transcribes, files and reads back a lecture end to end, through the real
setup screens, on a temp home with a fake notes engine and a local model mirror. `SelfTest.cs`
needs rewriting: Program.Main gets `STUDYSTASH_SELFTEST` handling with `SelfTest.Prepare()`
before Avalonia, SelfTestEngine (Kestrel fake: /api/tags, /api/show, /api/chat with/without
format, /models/{file} with Range), library home prep, then the script (setup-microphone →
setup-library-found → ... → quick-search → panel-library-unreachable → panel-waiting), each
step logs ok/FAILED, pictures its window, watchdog fails everything after 8 minutes, exit
non-zero on failure. Spec: `~/study-stash-night/plans/ws6-PLAN.md` §"Task 8".

### 3. WS4 T5 — rewrite notes + AI problem states (never started)

The lecture's notes get the "Rewrite notes with" menu, the "Rewriting with…" bar with Cancel,
the "New notes from … are ready" bar with Keep old / Compare / Use new, a compare view and a
failed state; plus the problem banners for every AI state, both looks, matching design refs
17/18. `AiNotesModel` polls RewriteAsync every 2 s, never replaces the notes on screen until
Use new, polling stops on Keep/Use/Cancel/dispose. Spec: `~/study-stash-night/plans/ws4-PLAN.md`
§"T5 · Rewrite the notes, and the AI problem states".

### 4. WS3 T3/T4 — Due list wiring + class tabs (partial)

T3 (Due list + assignment detail) views exist and are shot-tested against fixtures, but are
"wired to fixtures (builds clean, tests not yet run)" at last checkpoint — verify tests pass,
then wire the real library window's Due page per `docs/handoff/ws3-WIRING.md`. T4 (a class with
Canvas: tabs and sections) was never started. T6 (next-due in the dropdown, due work in the
quick panel, notifications) was never started. Spec: `~/study-stash-night/plans/ws3-PLAN.md`
§4 (T3 at "### T3", T4 at "### T4", T7 at "### T6").

### 5. WS6 problem-state copy (T6) — plain-English states

One place decides what's wrong and the dropdown says it with a fix button. T6's work (per its
plan §"Task 6") was folded into T7's commits on ws6b — one place decides what's wrong, and the
dropdown says it with a fix button (commit 0297ce4). Check it's complete at integration time.

### 6. The three known gaps the lanes reported (small, well-defined)

- **AiShots.cs duplication**: both ws4 lanes independently wrote an equivalent `AiShots.cs`
  test-only scaffolding; expect a duplicate to reconcile at merge (keep one, fold in the
  other's shot methods). Noted in ws4-WIRING.md.
- **CanvasShots mac-06/win-06**: ws3b's fix to WinCanvasSettings.axaml was a stopgap; T2's own
  shot tests for the settings screen were cut off. Reconcile at merge.
- **docs/DESIGN.md has one line** about `macos/tour.py` (retired) — ws7 T6 flagged it for
  whoever owns the screenshots paragraph.

## Hard rules that carry over

- **Never push, never touch `main`**. Pushing `night/*` branches is fine (backups) — CI only
  runs on `main` pushes and PRs.
- **No `Co-Authored-By:` or AI attribution** in new commits, code or docs (the two existing
  lines on ws7-ship are allowed to stay; the owner waived the rule for them).
- **This Mac is the owner's real laptop with a real install**: never use the real home folders
  (`~/.granola-share`, `~/.study-stash`); always run the app/engine with `--home <temp dir>`;
  never run `doctor`/`setup`/`service`/`install`/`start-at-login` or anything that installs
  services, edits Claude's configs, or touches Tailscale; never open a real microphone
  (use `STUDYSTASH_MIC_FILE`); never contact a real Canvas or AI account.
- **Don't bump the version** (stays 0.4.4) and never publish a release.
- **Personal-info scan before every commit**: `git diff --cached | grep -inE '<pattern>'` must
  print nothing. Use made-up example data (CS 101, BIO 110, Dr. Okafor). Never write scratchpad
  or home paths into a repo file.
- **Commit early and often** — commit after every step that builds and tests green, and never
  leave more than ~20 minutes of work uncommitted. Not-green states are committed as
  `Checkpoint: <what's in progress>`.
- **Personal-info scan** — see the pattern in `~/study-stash-night/plans/BRIEF.md` §"Hard rules"
  rule 8 (it's not written here because writing the banned-token list into a repo file would
  itself leak the tokens).
- Views come in pairs (`MacX.axaml`/`WinX.axaml` over one view model); colours/sizes are
  `{DynamicResource Token}` from `Skin.cs`; UI copy: sentence case, plain verbs, the design's
  exact words where it has them.

## Build and test

The system `dotnet` is 8.x — always use the durable SDK:

```sh
export DOTNET_ROOT=~/study-stash-night/dotnet10
export PATH=$DOTNET_ROOT:$PATH
dotnet --version   # 10.0.401

cd <your worktree>
dotnet build engine/StudyStash.slnx   # warnings are errors
dotnet test engine/StudyStash.slnx    # ~300–400 tests, under a minute once built
```

Known flaky tests (port races under parallel load): `UiTests.A_port_is_free_ours_or_busy`,
`OAuthTests.A_busy_callback_port_says_so` — rerun once before believing a failure there.

## Local tooling (kept outside the repo, in ~/study-stash-night/)

These were in `/private/tmp` (auto-cleaned by macOS within days) and were moved out so they
survive:

| Path | What it is |
|---|---|
| `~/study-stash-night/dotnet10` | .NET 10 SDK 10.0.401 (649 MB) — the build needs it |
| `~/study-stash-night/design2` | the design (source of truth for every screen): `template.html` (all tokens, the 10 OKLCH themes via `renderVals()`, all screens' markup), 37 per-screen HTML files, `ref/` = 73 PNG reference renders (mac-01 … win-18) with `index.json` |
| `~/study-stash-night/tabler` | Tabler Icons shallow copy (5166 outline + 1054 filled SVGs, MIT) |
| `~/study-stash-night/tools/tabler.py` | Tabler → IconPaths.cs entry generator (`python3 tabler.py add=plus`); `--find <word>` searches names/tags |
| `~/study-stash-night/models` | ggml-tiny.bin Whisper model (self-tests and tests) |
| `~/study-stash-night/marginalia` | Eli's original Python app (read-only reference for Canvas + AI behaviour) |
| `~/study-stash-night/ship-dist` | the last-built installers (DMGs, Setup.exe, 1.4 GB) |
| `~/study-stash-night/plans/` | the run's BRIEF.md, all 7 lane PLANs, both WIRINGs, ICONS.md, night-tail.js (the integration recipe), night5.js, run-journal.jsonl (the audits) |

## What's in the repo vs. what's local

- The two wiring docs are IN the repo at `docs/handoff/ws3-WIRING.md` and
  `docs/handoff/ws4-WIRING.md` (this branch, `night/base`) so the integration agent finds them
  with the code.
- Everything else from the run's scratchpad is in `~/study-stash-night/` (see the table above),
  not in the repo — the repo stays code + docs, and local tooling stays local.

## Branch map for the next agent (exact names)

```
main                      # don't touch
night/base                # this handoff lives here
night/wave2-base          # merge base for night/integration
night/ws1-no-granola      # done (T1–T7)
night/ws2-theme-glass     # done (T1–T8)
night/ws3-canvas-ui       # T1–T2 done, T3 partial (fixtures), T4/T6 not started
night/ws3b-canvas-ui     # T2-fix + T5 done
night/ws4-ai-ui           # T1–T4 done
night/ws4b-ai-ui          # T6 done
night/ws5-canvas-data     # T1–T4, T7 done; T5/T6 crawl not done (the big gap)
night/ws6-app-runs        # T1–T7 done; T8 self-test not done
night/ws6b-app-runs       # T7 done
night/ws7-ship            # done (T1–T6)
```

## One-paragraph summary

All 12 lanes of the overnight run finished and are pushed; every branch builds warning-free
with its tests green. The spend limit cut the run right as the integration phase started
(10:23 PDT), so nothing is merged yet: the next agent creates `night/integration`, merges the
lanes in the order above, wires the Canvas and AI screens in per the wiring docs, does a light
review, and reports — without touching main or pushing anything beyond night/* backup pushes.
The three real gaps to schedule after that: WS5 T5/T6 (structured Canvas crawl — the API
endpoints return empty lists without it), WS6 T8 (full self-test), and WS4 T5 (rewrite notes +
problem states).