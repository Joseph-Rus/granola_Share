# Study Stash: Canvas

Study Stash mirrors each class's Canvas course into that class's folder in the library, so the student, the app's
Canvas screens and Claude can all read their coursework without opening Canvas. Canvas is read through a small
Chrome extension with the student's own sign-in (many schools turn off Canvas access tokens). Nothing is ever written
to Canvas.

```
Chrome extension (student's Canvas session)            library (StudyStash serve)
  every minute: GET /api/v2/canvas/work     ──────▶     CanvasSync.Work: start a sync when due, hand out jobs
  fetch each job's Canvas URL, with cookies             (AI reads first, then the sync's queue)
  POST /api/v2/canvas/results               ──────▶     Crawl.Handle: file each answer, queue what follows
                                                        CanvasSync.Finish: assignments list, changes, errors
```

The code: `engine/src/StudyStash.Core/Canvas/` (`Crawl.cs` the queue and the mirror, `CanvasSync.cs` the
orchestration and AI reads, `Assignments.cs` the flat list, `CanvasSettings.cs` `home/canvas.json`,
`Extension.cs` the extension folder, `HtmlText.cs` HTML to Markdown, `Scout.cs` the course scout),
`engine/src/StudyStash.Library/LibraryWeb.Canvas.cs` (the extension's door, the API, the pages) and `extension/`.

This document starts as an audit of Eli's port of Marginalia's Canvas mirror against Marginalia itself, the design's
Canvas screens (sections 06 to 12) and the Canvas REST API. Workstream WS5 closes the gaps in eight tasks (T1 to T8);
each section says which task does what.

## Audit: bugs found in the port

Verified by reading the code. Several came from Marginalia. The last column is the task that fixes each one.

| # | Bug | What goes wrong | Fixed in |
|---|---|---|---|
| 1 | **Every 401 means "signed out".** | Canvas answers `401 {"status":"unauthorized","errors":[{"message":"user not authorized to perform that action"}]}` for a tab the student can't see (Files, Pages and Quizzes are often hidden). The crawl then cleared its whole queue and Settings said "Chrome isn't signed in". Signed out is `401 {"status":"unauthenticated"}` ("user authorization required"), an HTML page for a JSON job, or a bounce to `/login`. | T1 |
| 2 | **Announcements stop after 28 days.** | `/api/v1/announcements?start_date=…` without `end_date`: Canvas defaults `end_date` to start + 28 days, so in the fall only August's announcements were ever read. | T1 |
| 3 | **Paged listings overwrite themselves.** | Modules and announcements wrote modules.md / announcements.md from each page, so a second page left only page 2. | T1 |
| 4 | **Big modules mirror empty.** | With `include[]=items` Canvas may leave `items` out (giving `items_url`); the port treats that as no items. | T5 |
| 5 | **Module file links break.** | modules.md links to `SafeName(item title)` but the file is saved under its `display_name`; two files with one name in a module overwrite each other and swap on every sync. | T5 |
| 6 | **Module folders duplicate.** | Folders are "NN Name" by position; a rename or reorder makes a second folder with fresh copies. | T5 |
| 7 | **False "Removed".** | A failed assignments listing (a 403 or 5xx was dropped) made `Finish` diff a short list: "Removed: …" changes, and `canvas_assignments.json` lost those assignments. | T1 |
| 8 | **No back-off.** | `403 Forbidden (Rate Limit Exceeded)` was treated as "forbidden" and dropped; no retry for 5xx or network errors. | T1 |
| 9 | **Extension updates stall the sync.** | `CanvasSync.Work` gives no jobs to an extension of another version; the library's own extension folder is only rewritten from Settings, so after an update Chrome polls "hot" (every 1.5 s for 90 s each minute) and never syncs. | T2 |
| 10 | **Big files are rejected.** | A 40 MB file is ~53 MB of base64 in a JSON POST, six results posted together; Kestrel's default 30 MB request limit answers 413, the extension throws, and the job comes back every 10 minutes forever. | T2 |
| 11 | **The extension reads error bodies as files.** | Bytes jobs don't check `r.ok`, and the size check happens after reading the whole body. | T2 |
| 12 | **One file host, hard-coded twice.** | `*.inscloudgate.net` is in background.js and `CanvasSync.FileStore`, plus the manifest via `Prepare`: it should be one list (`Extension.FileHosts`). | T2 |
| 13 | **HTML → Markdown is messy.** | "Links to an external site." screen-reader spans end up in the text; equation images lose their LaTeX; iframes (videos) vanish; relative links (`/courses/…`) break outside Canvas; files linked inside instructions, pages and announcements are neither saved nor linked locally. | T4 |
| 14 | **Status is too coarse.** | Pass/fail and letter-graded work (score null, grade set) shows as "submitted"; graded late work loses "late" (the design shows "Submitted late · 17/20"). | T3 |
| 15 | **Submissions lose detail.** | Grader comment attachments (marked-up PDFs) aren't saved; attempt history isn't read; rubric marks lack rating names and points possible. | T3 |
| 16 | **`canvas_page` converts any body to Markdown.** | Marginalia converted only HTML (by content type). | T2 |
| 17 | **Other people's data.** | AI reads (`canvas_api`) can fetch rosters (`/users`, `/enrollments`) and other students' posts (`/discussion_topics/:id/entries`, `/view`). | T8 |
| 18 | **crawl.json holds too much.** | It keeps every raw assignment JSON of the sync and is rewritten after every result. | T3 |

## Audit: parity with Marginalia

Marginalia is Eli's original Python app (`marginalia/{crawl,canvas,canvas_browser,engine,web}.py` and its
`extension/`). Nothing it did for a student should be missing or wrong here.

| Marginalia | Study Stash | Why |
|---|---|---|
| Persisted job queue (crawl.json), six jobs per ask, in-flight jobs back in the queue after 10 minutes | **Kept** | A restart or a closed Chrome loses nothing. |
| Four listings per course: assignments (with your submission), your submissions (comments, rubric assessment, assignment), modules with items, announcements | **Kept**; announcements now come from `/api/v1/courses/:id/discussion_topics?only_announcements=true` (T1) | The old endpoint stops after 28 days (bug 2); the course's list has the whole term and `read_state`. |
| spec.md, feedback.md, submission files, module files and pages, modules.md, announcements.md | **Kept**, same content | Under `<class>/Canvas/` instead of `materials/canvas/` in a notes repo. |
| A spec.md written by hand (no `generated_by:`) is left alone | **Missing in the port, restored (T1)** | The student's own notes win over the mirror. |
| An assignment's folder found by a spec.md naming its `canvas_id` or Canvas URL | **Restored (T1), narrowed** for any folder directly under `Canvas/assignments/` | Marginalia matched the URL anywhere in any spec, so a copied link in one assignment's instructions handed its folder to the linked one. A spec the sync wrote now names its assignment only by the `canvas_id:` line in its front matter; only a hand-written one may name it by URL. Marginalia also guessed from folder names (`match_existing`); see below. |
| Manifest: a file version already here (`updated_at`) isn't downloaded again; files are compared before writing | **Kept** | A second sync of the same Canvas writes nothing (tested). |
| Videos, audio, locked files and files over 40 MB skipped | **Kept** | Too big for the extension's transport; T5 records them as skipped with a Canvas link. |
| Status rules (excused, graded, missing, late, submitted, no submission, past due, open) | **Kept**, refined in T3 | Letter and pass/fail grades, graded-and-late (bug 14). |
| Changes between syncs in words ("NEW", "DUE MOVED", "STATUS", "REMOVED") | **Kept** as "New:", "Due date moved:", "Now graded:", "Removed:"; structured in T3 | The design's notifications (section 12) need kinds, not only text. |
| AI reads through the extension (json, text, bytes), only Canvas and its file store, bytes saved only inside the library | **Kept** | `canvas_api`, `canvas_page`, `canvas_download`. |
| `canvas_page` converts HTML to Markdown only when the answer is HTML | **Missing in the port, restored in T2** | Bug 16. |
| Course import read `include[]=term` and `course_code` | **Missing in the port, restored in T7** | Course info for matching "COMP 101 on Canvas" to class "CS 101". |
| Read-only extension: refuses non-Canvas URLs, strips `while(1);`, reports a sign-in bounce or HTML for JSON as 401, reloads itself when its folder has a newer version | **Kept** | Eli added a per-library key (`X-Study-Stash-Key`), host permissions narrowed to this Canvas and this library, and the `public_url` fallback for downloads a service worker can't follow. |
| `Requeue` when a fresh extension asks with `force` | **Added by Eli, kept** | An updated extension loses nothing. |
| Course scout (AI explores a course, writes canvas-recipe.md) | **Kept** | T8 tells it what the sync now saves, so it only explores what's outside. |
| Personal access token mode (`canvas.py`) | **Not ported** | Schools turn tokens off; the extension works at every school. |
| Headless Playwright browser with its own Chrome profile (`canvas_browser.py`) | **Not ported** | No bundled browser; the extension already has the student's session. |
| `match_existing`: guessing an assignment's folder from hand-made folder names ("HW 1" ↔ "Homework 1") | **Not ported** | That was for Eli's own hand-kept folders; Study Stash makes the folders. A spec.md naming the assignment still claims its folder. |
| `integrate` (an AI folds new material into course notes) and the semester.md deadlines reconcile | **Not ported** | Study Stash has no hand-kept course notes; chat, the scout and the Due list cover it. |
| Git commits of Canvas content, the `<SEM>/canvas.md` snapshot table | **Not ported** | The library isn't a git repository; the Due page and the API replace the snapshot. |

## Audit: what neither had (the gaps)

Compared with the design's Canvas screens (06 settings, 07 connect, 08 states, 09 Due list and an assignment,
10 class tabs, 11 class sections, 12 next due, quick panel and notifications) and the Canvas REST API.

| Gap | Design | Closed in |
|---|---|---|
| Assignment status: to do / submitted / late / missing / excused / graded, with letter and pass/fail grades | 09, 10 | T3 |
| Rubric with ratings and points, per-criterion marks and comments ("Stack traces 8 / 10 · 'The frame for n = 1 is missing in 3b.'") | 10 | T3 |
| Submission: attempts, submitted at, grader comments with author and date, files with sizes, grader attachments | 10 | T3 |
| A per-class index (JSON) that the API and tools read | all | T3 |
| Canvas HTML read well as Markdown; files linked in instructions, pages and announcements saved and linked locally | 10 | T4 |
| Pages outside modules, the front page, the syllabus (`Canvas/syllabus.md`) | 11 | T4 |
| Module items of every type (File, Page, Assignment, Quiz, Discussion, ExternalUrl, ExternalTool, SubHeader); Box, Drive and OneDrive links with their source ("Saved from Box") | 11 | T5 |
| The Files area ("Files · 23"), with a graceful fallback when Canvas hides it (401/403) | 11 | T5 |
| Announcements with read state ("4 · 1 new"), bodies as Markdown, attachments | 11, 12 | T6 |
| Quizzes (title, due, points, description; never questions or answers) and discussion prompts (never other students' posts) | 10 | T6 |
| Upcoming work across courses (planner items), grouped overdue / this week / later / handed in | 09, 12 | T6 |
| Course code and term; course ↔ class matching ("COMP 101 on Canvas" ↔ "CS 101") | 06, 10 | T7 |
| Connection state (not set up / no extension / signed out / Chrome away / syncing / synced at / error), notifications | 07, 08, 12 | T7 |
| The JSON API for the Canvas screens | 06 to 12 | T7 |
| Read-only Canvas tools for Claude beyond `due_assignments`; other people's data refused in AI reads | 14 | T8 |
| The extension's version handshake, big files, one list of file hosts | 07, 08 | T2 |

## What's on disk

### Today (after T1)

```
<class folder>/Canvas/
  assignments/<name>/spec.md          instructions, due date, points, rubric (a hand-written one is left alone)
  assignments/<name>/feedback.md      your submission: status, score, rubric marks, comments
  assignments/<name>/submission/      the files you turned in
  modules.md                          the module outline, linked to the local copies
  modules/<NN Module>/                module files, and module pages as Markdown
  announcements.md                    announcements, newest first
  canvas-recipe.md                    the scout's map of how this course uses Canvas
home/
  canvas.json                         settings: Canvas address, class → course id, last sync, error, changes
  crawl.json                          the sync's queue, its sections, the manifest of saved files
  canvas_assignments.json             every assignment and where you stand, for the Due list
  canvas_key                          the extension's own key
  chrome-extension/                   the extension's folder, for Chrome's "Load unpacked"
```

### Target (after T8)

```
<class folder>/Canvas/
  syllabus.md                               T4  the course syllabus
  assignments/<name>/spec.md                    instructions, due, points, rubric (+ quiz facts / discussion prompt, T6)
  assignments/<name>/files/                 T4  files linked in the instructions
  assignments/<name>/feedback.md                your submission: status, score, rubric marks, comments, attempts
  assignments/<name>/submission/                latest attempt's files; submission/attempt N/ for older ones (T3)
  assignments/<name>/feedback/              T3  files the grader attached to comments
  modules.md                                    outline, rendered at the end of a sync, links to real local copies
  modules/<NN Module>/                          module files and pages; items the scout saved from Box/Drive (T5)
  pages/<title>.md, pages/files/            T4  pages outside modules, the front page, files they link
  files/<Canvas folder path>/<file>         T5  the Files area, when Canvas lets the student see it
  announcements.md, announcements/files/    T6  newest first; attachments
  quizzes/<title>.md                        T6  practice and ungraded quizzes (graded ones fold into their assignment)
  discussions/<title>.md                    T6  ungraded discussion prompts (graded ones fold into their assignment)
  canvas-recipe.md                              the scout's
home/
  canvas.json  crawl.json  canvas_assignments.json
  canvas/<class>.json (+ .sync.json while syncing)      T3 the per-class index
  canvas_notifications.json                              T7
  canvas_seen.json                                       T6 announcements opened in Study Stash
```

The sync never deletes anything from disk: an item gone from Canvas disappears from the index and the API after a
complete read, and the student keeps the copy they had.

## Robustness: how a sync survives Canvas

Each job's answer is classified (`Crawl.Classify`) before anything is filed:

| Answer | Means | What the sync does |
|---|---|---|
| `signed_out: true` from the extension, or 401 whose body isn't Canvas's `"unauthorized"` JSON (an empty body from a sign-in bounce, `{"status":"unauthenticated"}`, an HTML page) | Chrome isn't signed in to Canvas | Stops: the queue is cleared, Settings says "Chrome isn't signed in to Canvas", the next good answer clears it. |
| 401 with `{"status":"unauthorized"}` ("user not authorized to perform that action"), any other 403, 404 | The student can't see this (a hidden tab, a locked page, something removed) | Skipped quietly; a listing's section becomes `hidden`. |
| 403 or 429 with "Rate Limit Exceeded", or with `X-Rate-Limit-Remaining` ≤ 0 | Canvas asks the sync to slow down | The job goes back to the front of the queue and nothing is handed out for 30 s, doubling (60, 120 … up to 10 min) each time a job sent after the last pause is refused again; `Retry-After` wins when longer. The rest of the burst that caused a pause doesn't lengthen it. While paused, `Work` answers `hot: false`, so the extension sleeps until its next alarm. The back-off resets when a job sent after the pause comes back fine. |
| An OK answer with `X-Rate-Limit-Remaining` ≤ 0 | The answer is good; the next ones wouldn't be | Filed, then a 30 s pause. |
| 5xx, or no answer (status 0 with a network error) | A hiccup | Asked again, up to three times in all; then an error. |
| "refused: not a Canvas URL", "too big", other 4xx | It won't work by asking again | An error. |

**Sections.** Each class's listings (assignments, submissions, modules, announcements; later tasks add pages, files,
quizzes, discussions and the planner) are sections of the sync: `reading` when it starts, then `ok` (the last page
was read), `hidden` or `failed`. `Crawl.Sections` shows the current (or last) sync's; `TakeFinished` hands them to
`CanvasSync.Finish`.

- A class whose `assignments` section isn't `ok` keeps its previous rows in `canvas_assignments.json`: no false
  "Removed", and the file is unchanged. Settings' error names what failed: "Couldn't read CS 101 assignments from
  Canvas (Canvas answered 503), so what you had is kept."
- Listings whose pages only make sense together (modules, announcements) collect every page and are filed on the
  last one. A failed or hidden one writes nothing, so modules.md and announcements.md stay as they were.
- A class no longer linked to Canvas isn't reported as "Removed".

**Other rules.** Every listing follows Canvas's `Link: <…>; rel="next"` header. Jobs the extension took and never
answered go back in the queue after 10 minutes, or at once when the extension starts afresh (`force`). A spec.md
without `generated_by: study-stash` was written by hand and is never overwritten. AI reads use the same
classification: a hidden tab comes back as Canvas said it (status 401 and its JSON), only a real sign-out is the
error "Chrome isn't signed in to Canvas.", and a file read that Canvas refused saves nothing.

## The extension

_Filled in by T2: protocol, version handshake, file hosts, size limits, reload._

## The JSON API for the Canvas screens

_Filled in by T7._ Until then the app reads `GET /api/v2/canvas` (keys `url`, `courses`, `available`, `last_sync`,
`error`, `needs_login`, `extension_seen`, `extension_version`, `syncing`, `left`, `exploring`, `scouts`, `changes`) and
`GET /api/v2/assignments` (keys `class`, `id`, `name`, `due`, `points`, `status`, `score`, `submitted`, `url`,
`done`, `folder`). The extension's door is `/api/v2/canvas/work`, `/results` and `/status`; AIs read through
`/api/v2/canvas/fetch`, `/agent-courses` and `/courses`.

## Claude's Canvas tools

_Filled in by T8._

## Testing

Nothing here ever contacts a real Canvas. `engine/tests/StudyStash.Core.Tests/FakeCanvas.cs` stands in for Canvas
and the extension together: routes by URL path (the query is ignored except `page`), `Json`, `Pages` (with Link
headers), `Status`, `Bytes`, `FailTimes`, `On`, Canvas's own 404 for anything else, a `Requested` list for "never
asked for" checks, and `Run(sync)`, which plays the extension until the sync is done. Fixtures are in
`Fixtures/canvas/`: COMP 101 (course 4201) with the design's data on the 2025 calendar (Lab 3 due Tue 30 Sep,
Problem set 4 graded 18/20 by Dr. Okafor with the rubric comment "The frame for n = 1 is missing in 3b.", Week 3 and
Week 4 modules, four announcements with one unread). Tests fix the clock at the design's "now", Thu 25 Sep 2025,
10:24 in California (`2025-09-25T17:24:00Z`), and never assert times in the machine's own zone. Made-up people only.
