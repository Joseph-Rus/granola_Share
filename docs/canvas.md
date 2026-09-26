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
| Read-only extension: refuses non-Canvas URLs, strips `while(1);`, reports a sign-in bounce or HTML for JSON as 401, reloads itself when its folder has a newer version | **Kept**, extended in T2 | Eli added a per-library key (`X-Study-Stash-Key`), host permissions narrowed to this Canvas and this library, and the `public_url` fallback for downloads a service worker can't follow. 1.3 also says `signed_out`, passes on Canvas's rate limit, checks a file's size before reading it, checks its folder before every ask, and posts files one at a time (see "The extension"). |
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

### Today (after T2)

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
  chrome-extension/                   the extension's folder, for Chrome's "Load unpacked" (brought up to date when the library starts)
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

`extension/` is a Manifest V3 extension (version **1.3**; it has its own version, separate from the app's). The engine
carries it as embedded resources (`extension/<file>` in `StudyStash.Core`), and `Extension.Prepare(dir, library, key,
canvasUrl)` writes it out as a folder for Chrome's "Load unpacked": the scripts, a manifest, and `config.js`.

**Permissions stay minimal.** `permissions` is `["alarms"]` (the one-minute alarm) and nothing else: no tabs, cookies,
storage or content scripts. `host_permissions` is empty in the repo; `Prepare` fills in exactly three kinds: the
school's Canvas (`https://school.instructure.com/*`), Canvas's file store (every host in `Extension.FileHosts`, today
`https://*.inscloudgate.net/*`) and the library's own origin. A fetch with `credentials: 'include'` to a host it may
reach carries the student's Canvas session; the extension refuses every other URL (`refused: not a Canvas URL`).

**config.js** (owner-only): `const STUDY_STASH = {"app": <library>, "key": <extension key>, "canvas": <Canvas base>,
"files": ["*.inscloudgate.net"], "protocol": 2};`. `files` is the one list of file hosts (`Extension.FileHosts`),
shared with the manifest and the library's own check (`Extension.OnFileHost`, which `CanvasSync.CanvasUrl` uses for
AIs' reads): https only, no port, no user name, the host itself or a subdomain of a `*.` entry. A config.js from before
1.3 has no `files`, and background.js falls back to the old `*.inscloudgate.net` pattern. `protocol` is the protocol
the Study Stash that wrote the folder speaks; the extension sends its own.

### Protocol and handshake

| Who | What |
|---|---|
| extension → library | `GET /api/v2/canvas/work?v=<its version>&p=<its protocol>[&force=1]`, header `X-Study-Stash-Key` (or the library password). 1.2 and earlier send no `p`, read as 1. `force=1` comes from a fresh start (installed, reloaded, the popup's "Sync Canvas now"). |
| library → extension | `{"jobs":[{"id","url","kind":"json\|text\|bytes"}], "hot": bool, "ext": "<the library's extension version>", "p": 2}`. `hot`: ask again in 1.5 s (an AI is reading); false while Canvas has paused the sync. |
| extension → library | `POST /api/v2/canvas/results {"results":[result]}`; result = `{"id","status","link","type","final","text" or "b64","error","signed_out","rate","retry_after"}`. The last three are new in protocol 2; the library reads their absence as an old extension. |

**Every extension gets work.** The library hands jobs to any protocol ≥ 1, whatever its version (bug 9: 1.2 and
earlier were given nothing while their version differed from the library's, so a folder that wasn't updated stalled
the sync for good).

What protocol 2 (1.3) does, answer by answer:

- **Signed out, said plainly.** `signed_out: true` for a bounce to Canvas's `/login`, an HTML page where JSON was
  asked for (a school's sign-on page on its own host), or a 401 whose body says `unauthenticated` / `user authorization
  required`. Never for Canvas's `{"status":"unauthorized"}`, which only means the student can't see that tab. The
  status is 401 in all three, so a library from before `signed_out` still stops the sync.
- **Canvas's allowance.** `rate` (from `X-Rate-Limit-Remaining`) and `retry_after` (from `Retry-After`) are passed on
  as Canvas wrote them, with `type` (the content type); the library parses them and pauses (see Robustness).
- **Files.** The extension checks `Content-Length` before reading a body: over 40 MiB it answers `{status, error: "too
  big"}` and never reads it (and checks the real length again after reading). When Canvas answers a file with an
  error (`!r.ok`), no `b64` is sent: only the first 4,000 characters of the error as `text`, so the library tells a
  hidden file from a sign-out the same way it does for JSON, and never saves an error page as the file.
- **Kept from before:** `while(1);` stripped from JSON, text answers cut at 2,000,000 characters, and the `public_url`
  fallback: when a service worker can't follow `/files/<id>/download`, it asks `/api/v1/files/<id>/public_url` and
  fetches the signed link, only if that link is on a file host.
- **Posting.** A round's JSON and text answers go in one POST; each file answer goes in a POST of its own (one big
  body per request). A POST the library refuses stops the pump: those jobs go out again after the ten-minute in-flight
  timeout, and the next alarm pumps again.

### Versions, updates and reload

- **Reload before work.** Before every ask for work the extension reads its folder's `manifest.json`
  (`chrome.runtime.getURL`, `cache: 'no-store'`). When that version isn't the one running it calls
  `chrome.runtime.reload()` without asking for work, so a reload never drops jobs it had taken. The reloaded copy
  starts with `force=1`, and the library puts back whatever an older copy still held (`Crawl.Requeue`), so an update
  loses nothing. (1.2 only looked at its folder after the library's `ext` differed, so it may take one batch before
  reloading; the new copy's forced ask brings that batch back at once.)
- **The library keeps its folder current.** `Extension.Refresh(dir)` rewrites the scripts and pages and rebuilds
  `manifest.json` with this engine's version and the folder's own `host_permissions`, and leaves `config.js` (the key,
  the library's address) alone; it does nothing to a folder without both `manifest.json` and `config.js`, rewrites
  only files that differ, and returns whether the version changed. The library calls it once on
  `<home>/chrome-extension` when it maps its routes (`LibraryWeb.MapCanvas`), so a library update reaches Chrome
  within a minute.
- **The update is noted once.** Each visit records the running version (`canvas.json` `extension_version`). When it
  goes up from a known older version, `extension_update = {from, to, at, dismissed}` is set, and `GET /api/v2/canvas`
  shows `"extension_update": {"from","to","at"}` until `POST /api/v2/canvas {"dismiss_update": true}` (design 08's
  "Extension updated" state: "The Chrome extension updated itself", "Now version 1.4. Nothing to do.", Dismiss; the
  version is `to`). `extension_latest` is the library's version and
  `extension_outdated` is true while the running one is older, compared as versions (1.10 is newer than 1.9).

**For WS3/WS6 (the app).** The laptop app makes its own folder with `Extension.Prepare(Extension.Folder(home),
serverUrl, key, canvasUrl)` (unchanged). It should also call `Extension.Refresh(Extension.Folder(home))` once when it
starts (catching `IOException` and `UnauthorizedAccessException`), so a new app version reaches Chrome without the
student making the folder again. The Canvas screens read `extension_version`, `extension_latest`,
`extension_outdated` and `extension_update` from `GET /api/v2/canvas` (T7 also puts them in `/api/v2/canvas/state`)
and dismiss the update with `POST /api/v2/canvas {"dismiss_update": true}`.

### Size limits

| Limit | Value | Where |
|---|---|---|
| Biggest file mirrored | 40 MiB (41,943,040 bytes) | `Crawl.MaxBytes` (not queued when Canvas says it's bigger) and background.js `MAX_BYTES` (Content-Length, then the real length) |
| One POST to `/api/v2/canvas/results` | 256 MiB | Raised from Kestrel's 30,000,000-byte default for that route only, after the key check. A 40 MiB file is about 56 MB of base64 in JSON; an old extension posting up to four such files together fits too. |
| A text answer | 2,000,000 characters | background.js; an AI's `canvas_page` keeps the first 60,000 |
| An error page for a file | 4,000 characters | background.js |

**AIs' page reads** (`kind: "text"`, `canvas_page`) come back as Markdown only when Canvas's content type says HTML;
plain text, CSV or JSON come back as Canvas sent them (Marginalia's rule, bug 16).

## The JSON API for the Canvas screens

Every route below is under the library password, `Authorization: Bearer <password>`, like the rest of `/api/v2`
(`LibraryWeb.Api`/`ApiAsync`). A class name goes in the query (`?class=CS%20101`), never the path, since class names
may hold `/`. `*_at` fields are Canvas's own UTC ISO timestamps; `due` (and `synced`, `last_sync`, `posted_at` when
they come from a saved index) are as `AssignmentInfo`/`CourseIndex` keep them. `Core/Canvas/CanvasView.cs` builds
every answer below from `CanvasSettings`, a class's `CourseIndex` (`home/canvas/<class>.json`) and the crawl's live
state; `Library/LibraryWeb.Canvas.cs` only maps routes onto it, so `LocalLibrary` (Claude's tools, T8) can call the
same builders without going through HTTP.

**Where the data comes from today.** Assignments, their rubric, submission, attempts, files and comments are real
(T3): every assignment- and Due-list endpoint below reflects a real sync. `Modules`, `Files`, `Announcements`,
`Quizzes`, `Discussions` and `Todos` are already fields on `CourseIndex` and every endpoint that reads them is
built and tested against that shape, but the crawl doesn't fill them in yet (T5 leaves modules.md and
announcements.md as plain Markdown instead of a structured `ModuleInfo`/`AnnouncementInfo` list; quizzes,
discussions and the planner are T6's). Until then `GET .../modules`, `.../files`, `.../announcements` and
`.../pages` answer with the right shape and an empty (or files-only) list; the Due list and notifications only ever
carry assignments, never a `TodoInfo`. Nothing needs to change in this file or in `LibraryWeb.Canvas.cs` when T5/T6
land: they fill `CourseIndex`, and every builder here reads straight from it.

### State

- `GET /api/v2/canvas/state` (also embedded as `state` in `GET /api/v2/canvas`): `{"state", "school", "url",
  "extension":{"seen","version","latest","outdated","updated":{"from","to","at"}|null}, "last_sync" (when the last
  sync finished), "next_sync", "poll_minutes", "syncing":{"left","total","classes":[…]}|null, "paused_until"|null,
  "error":{"text","at"}|null, "warnings":[…]}`. `state` is decided by `CanvasView.StateOf` in this order: `not_set_up`
  (no Canvas address) > `no_extension` (Chrome has never checked in) > `signed_out` > `chrome_away` (not seen for
  over 5 minutes) > `syncing` > `error` (the last sync ended with one) > `connected`. `syncing.left`/`total` count
  classes, not jobs (`total` = classes in this sync, `left` = classes whose four listings haven't all finished);
  `classes` names them, straight from the crawl's live section states — no new state kept for this.
- `POST /api/v2/canvas` (unchanged routes, wider body): also takes `poll_minutes` (15–1440, clamped) and
  `dismiss_update: true`; its answer (same as `GET /api/v2/canvas`) now also carries `state` and `course_info`
  (Canvas course id → `{code, name, term}`, from `FindCoursesAsync`, which now follows every page and asks for
  `include[]=term`).

### Classes and the Due list

- `GET /api/v2/canvas/classes` → one row per class in this library, linked or not: `[{"class","linked",
  "canvas":{"id","code","name","term","url"}|null, "suggested":{"id","name"}|null,"last_sync","counts":
  {"to_hand_in","done","modules","files","announcements","announcements_new"},"files_hidden","scout":
  {"state":"done|exploring|waiting|failed|never","files","when","report"}}]`. `suggested` (only when a class isn't
  linked) is `CourseMatch.Suggest`'s best guess from the school's course list: a shared number ("CS 101" ~
  "COMP 101") or a shared word, one a prefix of the other ("CALC" ~ "Calculus"); it's shown, never linked, by
  itself. `announcements_new` is currently just unread-on-Canvas (T6's per-student "opened in Study Stash" tracking,
  `CanvasSeen`, narrows this further once announcements are populated).
- `GET /api/v2/canvas/due` → `{"synced","to_hand_in","next":item|null,"groups":[{"key","label","items":[item]}]}`
  across every class, groups in order **Overdue** (not done, due before now) / **This week** (due before the eighth
  day from now) / **Later** / **No due date** / **Handed in** (submitted, graded or excused, most recently
  submitted-or-graded first, only the last 7 days). `to_hand_in` is the size of the first four groups combined;
  `next` is the soonest of them that isn't overdue.
- `GET /api/v2/canvas/assignments?class=` → `{"class","to_hand_in":[item],"done":[item]}` (soonest due first / most
  recently due first). `item` = `{"class","id","name","kind","due","due_at","points","status","label","score",
  "grade","score_text","late","missing","excused","submitted","graded_at","marked_done","url","folder"}`.

### One assignment

- `GET /api/v2/canvas/assignment?class=&id=` → `item` plus `{"instructions","unlock_at","lock_at",
  "submission_types","allowed_attempts","grading_type","files":[file],"rubric":[{"id","criterion","description",
  "points","ratings":[{"label","points"}],"mark":{"points","rating","comment"}|null}],"submission":{"state",
  "attempt","submitted_at","graded_at","score","grade","late","points_deducted","body","files":[file],"attempts":
  [{"attempt","submitted_at","late","files":[file]}]}|null,"comments":[{"author","at","text","files":[file],
  "media_url"}],"quiz":{…}|null (only once T6 fills `CourseIndex.Quizzes`),"spec","feedback"}` — `spec`/`feedback`
  are the assignment's own `spec.md`/`feedback.md`, relative to the class's folder, for `/api/v2/files/raw`. `file` =
  `{"id","name","size","content_type","format","local","skipped","url"}`; `format` is derived from the content type
  or the name's extension when nothing more specific is saved (T5 will save a real one on `CourseFileInfo`).
  A 404 for an unknown class or assignment id.

### Modules, files, announcements, pages

- `GET /api/v2/canvas/modules?class=` → `{"count","modules":[{"id","name","position","state","unlock_at",
  "items_count","items":[{"id","type","kind","title","indent","format","size","local","saved","source","url",
  "external_url","assignment_id","locked","skipped"}]}]}`, straight off `CourseIndex.Modules`.
- `GET /api/v2/canvas/files?class=` → `{"allowed","count","files":[{"id","folder","name","size","content_type",
  "format","updated_at","local","skipped"}]}`; `allowed` is false only when Canvas hides the Files area
  (`CourseIndex.FilesHidden`).
- `GET /api/v2/canvas/announcements?class=` → `{"count","new","items":[{"id","title","posted_at","author","new",
  "read_on_canvas","body","files":[file],"url"}]}`, newest first; `new` is unread on Canvas and not marked seen in
  Study Stash. `POST /api/v2/canvas/announcements/seen {"class","ids":[…]}` marks announcements opened here
  (`Core/Canvas/CanvasSeen.cs`, `home/canvas_seen.json`) so they stop counting as new even before Canvas itself
  shows them read.
- `GET /api/v2/canvas/pages?class=` → `{"syllabus":path|null,"front_page":page|null,"pages":[page],"quizzes":[…],
  "discussions":[…]}`; `page` = `{"title","url","updated_at","local","in_module"}`.

### Notifications

- `GET /api/v2/canvas/notifications?after=<id>` → `{"last","items":[{"id","kind","title","text","class",
  "assignment_id","announcement_id","at","seen"}]}`. `Core/Canvas/CanvasNotifications.cs`
  (`home/canvas_notifications.json`, ids always increasing, capped at 200) turns a finished sync's `CanvasChange`s
  into notifications (`new` → `new_assignment`/"New assignment", `moved` → `due_moved`, `graded` → `new_score`,
  `feedback` → `new_feedback`, `missing` → `missing`; `removed` isn't news). A class's first sync makes no changes at
  all, so it makes no notifications either. Reading this endpoint also adds one `due_soon`/"Due soon" per assignment
  the first time it's open and due within 24 hours, never repeated for the same assignment.
- `POST /api/v2/canvas/notifications/seen {"up_to":id}` marks every notification up to that id seen.

### Raw files

- `GET /api/v2/files/raw?class=&path=` → the bytes of any file already saved inside that class's folder (any size,
  any type — for opening `ps4-answers.pdf` on a laptop, say); 404 for a path that leaves the folder or doesn't
  exist. `GET /api/v2/files?class=&path=` (unchanged) still only serves small `.md`/`.txt` files, as text, for the
  library's own pages.

### Unchanged

`GET /api/v2/canvas` gains `state` and `course_info` but keeps every key the app already reads (`url`, `available`,
`courses`, `error`, `extension_seen`, `syncing`, `left`, `last_sync`, …); `GET /api/v2/assignments` keeps its flat
list and all its keys. The extension's door (`/api/v2/canvas/work`, `/results`, `/status`) and the AI reads
(`/api/v2/canvas/fetch`, `/agent-courses`, `/courses`, `/api/v2/canvas/scout`) are untouched.

## Claude's Canvas tools

_Filled in by T8._

## Testing

Nothing here ever contacts a real Canvas. `engine/tests/StudyStash.Core.Tests/FakeCanvas.cs` stands in for Canvas
and the extension together: routes by URL path (the query is ignored except `page`), `Json`, `Pages` (with Link
headers), `Status`, `Bytes`, `FailTimes`, `On`, Canvas's own 404 for anything else, a `Requested` list for "never
asked for" checks, and `Run(sync)`, which plays the extension until the sync is done. Fixtures are in
`Fixtures/canvas/`: COMP 101 (course 4201) with the design's data on the 2025 calendar (Lab 3 due Tue 30 Sep,
Problem set 4 graded 18/20 by Dr. Okafor with the rubric comment "The frame for n = 1 is missing in 3b.", Week 3 and
Week 4 modules, four announcements with one unread). `ExtensionScriptTests` runs the real `background.js` (the copy
the engine carries) in Jint, with `importScripts`, `chrome.*`, `fetch`, `btoa`, `URL` and `setTimeout` stood in for;
`CanvasExtensionTests` covers the folder, the handshake and a 40 MB file through real Kestrel. `CanvasApiTests` syncs
a library over all four of the design's classes (adding minimal fixtures for BIO 110, CALC II and HIST 210 alongside
COMP 101's) and reads the JSON API in this document back through a real `TestSite`: state's priority order, the
classes list (including a suggested, unlinked course), the cross-class Due list, one class's to-hand-in/done split,
one assignment's rubric marks and the grader's comment, modules/files/announcements' shape, notifications (due soon,
marking seen), a raw file download, and the old keys `GET /api/v2/canvas` and `GET /api/v2/assignments` still
answer. Tests fix the clock at the design's "now", Thu 25 Sep 2025,
10:24 in California (`2025-09-25T17:24:00Z`), and never assert times in the machine's own zone. Made-up people only.
