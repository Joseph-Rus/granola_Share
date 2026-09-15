# Granola Share — research and design

Status: research complete, nothing built yet. Written 2026-09-15.

## The idea

A shared "notes pool" for a group of friends.

1. Each friend records lectures with Granola on their own laptop (Mac or Windows).
2. When a note finishes (transcript + AI summary generated), it is sent automatically to a
   home server (Joey's Mac mini, or a Windows box).
3. A local model on the server reads the note, decides which class it belongs to (using the
   user's tag if they set one, otherwise inferring from content), and files it in the right
   folder.
4. Friends log in to the pool over Tailscale (or ngrok) to browse and download notes.

The result is a growing library of lecture notes and transcripts, sourced from everyone.

## Research findings

### 1. Getting notes out of Granola (ranked)

Granola's local cache is now encrypted. On Joey's machine (Granola 7.559.2) the data dir
contains `cache-v6.json.enc`, `supabase.json.enc`, `stored-accounts.json.enc`, and a
SQLCipher `granola.db`, with no plaintext copy. Every community tool that read
`cache-v3.json` is dead or archived. That leaves these routes:

| Route | Plan needed | Transcript? | Push or poll | Verdict |
|---|---|---|---|---|
| A. Official public API + webhooks | Business ($14/user/mo) | Yes | Push (webhook) or poll | **Recommended** |
| B. Official remote MCP | Any (free = 30 days, no transcripts; paid = full + transcripts) | Paid only | Poll from a user-present client | Fallback for free users |
| C. Zapier "Note added to folder" | Paid + Zapier | Yes | Push | Redundant once A exists |
| D. Decrypt local encrypted cache | Free | Yes | Watch file | Fragile, undocumented, keychain prompts |
| E. Reverse-engineered private API | Free | Yes | Poll | Archived by its own author; do not build on it |

**A. Official public API** (released Feb 2026, at v1.5 as of Sept 2026)

- Base URL `https://public-api.granola.ai/v1`, header `Authorization: Bearer grn_...`.
- Keys are created in the desktop app under Settings > Connectors > API keys.
  Business or Enterprise plan required. Two scopes: `personal` (notes you own, shared with
  you, or in private folders shared with you) and `public` (workspace-visible / Team space).
- Endpoints:
  - `GET /notes` — params `created_after`, `created_before`, `updated_after`, `folder_id`,
    `cursor`, `page_size` (1–30). Returns only id, title, owner, created_at, updated_at.
  - `GET /notes/{note_id}?include=transcript` — full note: `title`, `owner`, `web_url`,
    `calendar_event`, `attendees`, `folder_membership` (folder id, name, parent_folder_id),
    `summary_text`, `summary_markdown`, `private_notes_text`, `private_notes_markdown`,
    `transcript`. Returns 413 `TRANSCRIPT_TOO_LARGE` for big transcripts.
  - `GET /notes/{note_id}/transcript` — cursor-paginated transcript (page_size up to 100).
    Each item: `text`, `start_time`, `end_time`, `speaker{source: microphone|speaker,
    attribution: me|them, name, diarization_label}`.
  - `GET /folders` — accessible folders, with `parent_folder_id` nesting.
  - `POST /webhook-endpoints`, plus list/update/delete.
- The API only returns notes that already have a generated summary and transcript.
  A note that is still processing returns 404. So "the note shows up in the API" is
  exactly the "note is done" signal we want.
- Rate limits: 25-request burst, 5 req/s sustained, 429 on overflow.

**Webhooks** (Business/Enterprise)

- Events: `note.generated` (first AI summary created — our trigger), `note.edited`,
  `note.access_granted` (a note was shared with you).
- Payload has no content, only a reference:
  `{"event_id", "event_type", "note_id", "occurred_at"}`. The server then fetches the note
  via the API.
- Can be restricted to specific `folder_ids` and scopes (`personal`, `public`, `workspace`).
- Standard Webhooks signing: headers `webhook-id`, `webhook-timestamp`, `webhook-signature`
  (`v1,` + base64 HMAC-SHA256 over `{id}.{timestamp}.{body}` using the base64-decoded
  `whsec_` secret). 15-second response timeout, exponential retries for four days.
- The endpoint URL must be public HTTPS, so the Mac mini needs Tailscale Funnel, ngrok, or
  Cloudflare Tunnel for that one path.

**B. Official remote MCP** (`https://mcp.granola.ai/mcp`)

- OAuth 2.0 with dynamic client registration only. No API keys, no service accounts, and
  each connection acts as one human user. Granola's own docs say MCP is for interactive
  clients and the API is for headless backends.
- Tools: `list_meetings`, `get_meetings` (summary + private notes), `get_meeting_transcript`
  (paid plans only), `list_meeting_folders` (paid only), `query_granola_meetings`,
  `get_account_info`. About 100 requests/minute.
- Free plan: personal notes from the last 30 days, no transcripts. Business: personal +
  public notes with transcripts.
- Useful for a per-laptop agent that polls on behalf of a user who has logged in once.
  Not usable from the server on someone else's behalf.

**D. Local encrypted cache** (for completeness)

- `.enc` files are AES-256-GCM: 12-byte IV, ciphertext, 16-byte tag. The data-encryption
  key is wrapped with Electron `safeStorage` (Chromium v10 format, password held in the
  macOS Keychain item "Granola Safe Storage"; DPAPI on Windows). `granola.db` is SQLCipher
  keyed via a WebCrypto key in IndexedDB. No `storage.dek` file is present on Joey's install,
  so the recipe from community projects may already be stale.
- Reading it triggers Keychain prompts, breaks on every Granola update, and touches auth
  tokens. Not worth it when the official API exists.

### 2. Detecting "note is being taken / note is done"

We do not need to watch the Granola process. The cloud side tells us:

- Push: `note.generated` webhook fires when the first summary is created.
- Poll fallback: `GET /notes?updated_after=<last_sync>` every 60 seconds. Anything new is
  finished by definition (unfinished notes are not returned).
- Free-plan fallback: a small agent on the laptop calls MCP `list_meetings` periodically.

"Note is being taken" (in-progress) is not exposed by any official route. The live
indicator would require the local cache. Skip it; only "done" matters for the pool.

### 3. Moving data between machines

- **Tailscale** (recommended for the pool itself). Private mesh network, no public exposure,
  peer-to-peer. Friends install the client and either join Joey's tailnet or Joey uses
  node sharing to share just the Mac mini with their own Tailscale accounts. The pool web UI
  is then reachable at `http://mac-mini:8000` from anywhere.
- **Tailscale Funnel / ngrok / Cloudflare Tunnel** (for the webhook only). Granola's
  servers must reach an HTTPS URL. Expose only the `/webhooks/granola` path and verify
  signatures. ngrok is simplest to start; Funnel keeps everything in Tailscale.
- With the API route the friends' laptops never send anything. The server pulls from
  Granola's cloud directly, so there is no client-side daemon to install or keep running,
  and Windows vs Mac stops mattering.

### 4. AI sorting on the server

- Run **Ollama** on the Mac mini. Its structured-output mode constrains the model to a JSON
  schema, so classification can never return malformed output. Sensible models: Qwen 3.5 or
  Phi-4-mini (small, fast) up to Phi-4 14B or Gemma 3 12B on a 16 GB+ Mac mini.
- Inputs to the classifier: the note's `summary_markdown`, `title`, `calendar_event`
  title/time, `folder_membership` (the user's own tag), the first ~2000 words of transcript,
  and the current list of classes in the pool.
- Output schema: `{class_id | "new", confidence, suggested_class_name, lecture_title,
  topics[], week_or_date}`. If the user filed the note in a Granola folder that maps to a
  class, trust that and skip the model. Below a confidence threshold, put it in an
  "Unsorted" queue that anyone can fix in the UI.
- Optional later step: the same model writes a study-guide version or merges duplicate
  lectures recorded by two friends.

## Recommended architecture

```
 friend's laptop (Mac/Win)          Granola cloud                 Mac mini (server)
 ┌───────────────────┐   sync   ┌───────────────┐  webhook   ┌──────────────────────────┐
 │ Granola desktop   │────────▶ │ note.generated│──────────▶ │ /webhooks/granola        │
 │ (records lecture) │          │               │  (via      │  verify sig, enqueue     │
 └───────────────────┘          │  public API   │◀───────────│ fetch note + transcript  │
                                └───────────────┘  GET       │ Ollama classify → class  │
                                                             │ store: SQLite + md files │
 friend's browser  ── Tailscale ───────────────────────────▶ │ web UI: browse/download  │
                                                             └──────────────────────────┘
```

Server components (one Python service, FastAPI + SQLite + Ollama):

1. `ingest/webhook.py` — receives `note.generated`, verifies HMAC, enqueues `note_id`.
2. `ingest/poller.py` — every 60 s, `GET /notes?updated_after=...` per linked key. Backup
   for missed webhooks and the only path if webhooks are off.
3. `ingest/fetch.py` — gets the note with `include=transcript`, falls back to the paged
   transcript endpoint on 413. Stores raw JSON.
4. `sort/classify.py` — Ollama structured output; folder-tag override; confidence gate.
5. `store/` — SQLite (notes, classes, sources, versions) plus a folder tree on disk
   `pool/<Class>/<YYYY-MM-DD> <title>/{summary.md, transcript.md, raw.json}` so it is also
   just a folder you can rsync or open in Finder.
6. `web/` — login, class list, lecture list, note view, download (md/zip), "fix class"
   button, unsorted queue.

### Accounts and the "joint account" question

Our app has its own users (the friends). Each user links a way for the server to read
their Granola notes. Three ways to do that, cheapest first:

| Option | How it works | Cost | Trade-off |
|---|---|---|---|
| 1. One shared Granola login | Everyone signs into the same Granola account on their laptop. One Business seat, one API key, one webhook. | $14/mo total | Everyone sees everyone's notes inside Granola too, including non-lecture meetings. Likely against Granola's terms for account sharing. Simplest code. |
| 2. One Business workspace, shared folders | Each friend has their own Granola account in one workspace. They file lectures in a shared "Lectures" folder. A workspace-scoped key + folder-restricted webhook picks up everything. | $14/user/mo | Clean permissions, per-person keys not needed. Most expensive. |
| 3. Per-user API keys | Each friend on Business creates a personal key and pastes it into our app. Server polls/webhooks per key. | $14/user/mo | Same cost as 2, more moving parts. |
| 4. Free-plan agent | Friends on the free plan run a small tray app that does MCP OAuth once and pushes `get_meetings` output to the server. No transcripts. | $0 | Summaries only, 30-day window, needs a running client per laptop, Windows + Mac builds. |

Recommendation: start with option 1 or 2 depending on budget, and build the server so a
"source" is an abstract thing. Option 4 can be added later as a second source type for
friends who will not pay.

### Things to check before building

- Granola's terms on sharing one account across people (option 1).
- Your school's policy on recording lectures and sharing transcripts. Transcripts of a
  professor are more sensitive than your own notes. Consider storing summaries by default
  and transcripts only when the recorder opts in.
- Mac mini RAM. 8 GB limits you to ~4B models; 16 GB+ opens up 12–14B models.

## Build plan

1. **Spike (1 evening):** get a Business trial key, `curl` `GET /notes` and one note with
   transcript, save to disk. Confirms the data shape end to end.
2. **Server core:** FastAPI app, SQLite schema, fetch + store, poller. No AI yet. Notes land
   in `pool/Unsorted/`.
3. **Classifier:** Ollama structured output with the class list, folder-tag override,
   confidence gate, "fix class" endpoint.
4. **Webhook:** register an endpoint pointing at an ngrok or Funnel URL, verify signatures,
   keep the poller as backup.
5. **Web UI + auth:** simple login, browse by class, download md/zip, unsorted queue.
6. **Network:** Tailscale on the Mac mini, node-share to friends, launchd service so it
   survives reboots.
7. **Later:** free-plan MCP agent, duplicate-lecture merging, study-guide generation, search.

## Sources

- Granola API overview: https://docs.granola.ai/help-center/sharing/integrations/granola-api
- API reference: https://docs.granola.ai/introduction (list-notes, get-note, get-transcript,
  list-folders, create-webhook-endpoint under /api-reference/)
- Webhooks guide: https://docs.granola.ai/webhooks
- API changelog: https://docs.granola.ai/api-reference/changelog
- Granola MCP docs: https://docs.granola.ai/help-center/sharing/integrations/mcp
- Granola MCP launch post: https://www.granola.ai/blog/granola-mcp
- Zapier integration: https://docs.granola.ai/help-center/sharing/integrations/zapier
- Pricing (third-party summary): https://costbench.com/software/ai-meeting-assistants/granola/
- Encrypted-cache notes: https://github.com/openclaw/graincrawl (SPEC.md),
  https://github.com/openclaw/graincrawl/issues/15
- Archived cache/API tools: https://github.com/pedramamini/GranolaMCP,
  https://github.com/wassimk/granary, https://github.com/varadhjain/granola-claude-plugin,
  https://github.com/getprobo/reverse-engineering-granola-api
- Official-API exporter example: https://github.com/junxit/granola-exporter
- Tailscale vs ngrok: https://tailscale.com/compare/ngrok,
  https://chrisshennan.com/blog/tailscale-as-an-ngrok-local-tunnel-cloudflare-tunnel-alternative
- Ollama structured outputs: https://docs.ollama.com/capabilities/structured-outputs
- Granola for Windows: https://winstall.app/apps/Granola.Granola

## Decision (2026-09-15): build the free path on the official MCP server

Joey's call: no paid plans. That rules out the Obsidian plugins' and granola-exporter's
method (they call the public API, which needs Business). The free, official route is the
Granola MCP server with browser OAuth, so that is what `granola_share/` implements:

- One process on the Mac mini logs in as the pool account and polls `list_meetings`.
- Friends add lecture notes to a shared Granola folder; the pool account sees them under
  the free plan's personal-notes scope (own, shared with you, or in a shared private folder).
- No per-laptop agent, no local cache decryption, no reverse-engineered API.
- Transcripts come along automatically if the account is ever upgraded
  (`get_meeting_transcript` is paid-only; the sync tries it and ignores failure).

Verified against the live auth server without completing a login: resource discovery,
dynamic client registration (only `authorization_code` + `refresh_token` grants are
allowed, so no device flow), and the authorize URL is accepted. Classifier verified
against a local `qwen3:1.7b`. Still unverified until Joey logs in: the tool result
schemas (handled by runtime discovery plus a debug dump) and the shared-folder scope.

## Decision (2026-09-15, later): guided setup + per-laptop client with a Share/Skip popup

Joey asked for a setup that walks you through everything, and for friends to get a popup
asking whether to move each finished note to the pool. Two facts shaped the design:

- Granola's MCP tools and public API are read-only, so nothing can move a note into a
  shared Granola folder on the user's behalf. The friend's client therefore pushes the
  approved note directly to the pool server (`POST /api/ingest`, pool password as bearer).
- Every friend logs into Granola on their own laptop (browser OAuth), so no shared account,
  no shared folder, and each person decides note by note.

Built: `wizard.py` (server and client wizards, scripted-prompter tested), `client.py`
(poll → ask → push, with pending/skipped/shared state), `dialogs.py` (osascript,
user32 MessageBox, zenity/tkinter), `autostart.py`, `install.sh` / `install.ps1`, and the
ingest/health API. Verified end to end on macOS: real server + real Ollama + client push
over HTTP; the macOS dialog renders. The server's own Granola sync is now optional
(`server_sync`), off by default.

## Model choice (2026-09-15)

Joey's server Mac has 64 GB RAM and already has `qwen3.6:35b-a3b` in Ollama, plus MLX builds
in LM Studio. Default is now `qwen3.6:35b-a3b` (MoE, ~3B active, 23 GB). The wizard ranks
installed models (qwen3.6 35B > qwen3.6 > qwen3.8 > gemma4 > qwen3 > llama3). The classifier
sends up to 12k chars of notes with `num_ctx = 8192`. LM Studio's OpenAI-compatible server is
not wired in; Ollama has the same model family, so there is no need yet.
