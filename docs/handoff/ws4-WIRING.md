# WS4 ai-ui: wiring notes (not committed — for whoever assembles the app shell)

## T3 · Settings → AI engines, and the library setup's AI step

### AI engines pane (design 13)

- Controls: `Views/MacAiEngines.axaml` + `.axaml.cs`, `Views/WinAiEngines.axaml` + `.axaml.cs`, over
  `ViewModels/AiEnginesModel.cs`. Styles: `Views/MacAiStyles.axaml` / `Views/WinAiStyles.axaml` (each
  `<StyleInclude>`d from its own view; never touch `Styles.axaml`).
- Make it: `var model = new AiEnginesModel(new AiRemote(cc.ServerUrl, cc.PoolKey));` (`AiRemote` from
  `Core/Ai/AiApi.cs`; `cc` is whatever holds the connected library's URL and pool key elsewhere in the app).
  Set `model.OpenUrl = url => /* the OS browser, e.g. Process.Start or a launcher service */;` before the
  first load so "Get it" rows can open an engine's site. Call `await model.Load()` when the pane becomes
  visible (each time it's shown again is fine — it's cheap and picks up anything changed on the library).
- Goes: replacing the old AI section of `SettingsView` (both `Mac` and `Win` variants) — the section's own
  row in the settings sidebar already says "AI engines"; this pane is everything to the right of it.
- Nothing to dispose (no timers, no open connections) — dropping the model when the pane is hidden is enough.
- Demo/preview data: `AiDemo.Engines()` (used by `AiShots`), not for the running app.

### Library setup's AI step (design 15)

- Controls: `Views/MacAiSetup.axaml` + `.axaml.cs`, `Views/WinAiSetup.axaml` + `.axaml.cs`, over
  `ViewModels/AiSetupModel.cs`. Same style includes as the engines pane.
- Make it the same way: `new AiSetupModel(new AiRemote(...))`, `await model.Load()` when the wizard reaches
  this step (step 3 of 4, library role only — the brief says don't build AI on the laptop, so this step only
  shows when the wizard is setting up *this computer as the library*; skip it entirely on a laptop-only setup).
- The step's body is only what's inside `MacAiSetup`/`WinAiSetup` — no "Library setup · step 3 of 4" caption,
  no Back/Continue footer, no sidebar: those are the setup wizard's own chrome (compare
  `Views/MacSetup.axaml`'s `Steps`/`StepItem` pattern for the numbered-step sidebar look, though this step's
  labels are the library wizard's own: "This is the library", "Transcription model", "AI engines", "AI tool
  access · Optional").
- Wizard's Continue/Next button: call `await model.SaveAsync()` before advancing; if it returns `false`,
  `model.Say` or `model.Offline`/`model.OlderLibrary` already say why — show that instead of moving on.
- Demo/preview data: `AiDemo.Setup()` — deliberately a *different* `AiOverview` from `AiDemo.Engines()`
  (`Ask` still equals `Notes`, so the "Who answers your questions" select shows "Same as notes", the design's
  first-run state) — see `AiDemo.SetupOverview()`.

### Shared

- Both models' `Offline`/`OlderLibrary` flags and their words (`AiWords.OlderLibraryWords`, and a plain
  "your library isn't answering" line) are already wired into both views; nothing extra needed from the host
  beyond constructing the model with a real `AiRemote`.
- Icons the pane/step draw: `edit_note`, `forum`, `memory`, `terminal`, `code`, `auto_awesome`, `swap_horiz`
  (all in `IconPaths.cs` already, added in this task's first commit).
- Two small shared helpers added to `Controls/Converters.cs` (WS2's file, additive): `FirstCardMargin` (a
  Windows card list's first row sits flush) and `OnOff` (the fallback toggle's Windows label). Neither is AI-
  specific; later panes with the same card-list pattern can reuse them.

### Test-only scaffolding (never app code)

- `engine/tests/StudyStash.App.Tests/AiShots.cs` draws a settings-window frame (`AiShots.SettingsFrame`) and
  a setup-wizard frame (`AiShots.SetupFrame`) purely for screenshots: the real settings window is WS2's, the
  real setup wizard is WS6's. Don't reuse these frame builders from app code — build the real host windows
  instead and drop our pane/step into them as described above.

## T6 · AI tool access (design 14)

Built on `night/ws4b-ai-ui` (lane b), from the integrated T1/T2 base. If this lands after T3/T4/T5 (`night/ws4-ai-ui`)
merge, expect a duplicate `AiShots.cs` (both lanes wrote `SettingsFrame`/`SetupFrame` test-only scaffolding
independently, per the plan) — keep one, fold in the other's shot methods.

### The pane

- Controls: `Views/MacAiAccess.axaml` + `.axaml.cs`, `Views/WinAiAccess.axaml` + `.axaml.cs`, over
  `ViewModels/AiAccessModel.cs`. Styles: reuses `MacAiStyles.axaml`/`WinAiStyles.axaml` (added `ai-check`, a
  checkbox to match the design's "What they can read" rows).
- Make it: `var model = new AiAccessModel(new AiRemote(cc.ServerUrl, cc.PoolKey));` then, before `Load()`:
  ```csharp
  model.Copy = text => TopLevel.GetTopLevel(view)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask; // write-only
  var setup = new ClaudeSetup(); // App/Services/ClaudeSetup.cs — Program/Home default to this computer
  model.ClaudeCodeCommand = setup.ClaudeCodeCommand;
  model.CodexSetup = setup.CodexSetup;
  model.McpJson = setup.McpJson;
  model.CheckInClaudeCode = setup.InClaudeCode;
  model.CheckInClaudeDesktop = setup.InClaudeDesktop;
  model.AddToClaudeDesktop = () => Task.FromResult(setup.AddToClaudeDesktop());
  model.RemoveFromClaudeDesktopHook = () => Task.FromResult(setup.RemoveFromClaudeDesktop());
  model.RevokeConnection = async id => { await remote's underlying RemoteLibrary/HttpClient .../claude/connections/{id} (DELETE); return true; }; // AiRemote itself has no DELETE — reuse whatever client already talks to /api/v2/claude (RemoteLibrary.ClaudeAsync)
  model.TurnOnWeb = async () => { var r = await claudeClient.ClaudeAsync(HttpMethod.Post, "/reach", new JsonObject{["internet"]=true,["on"]=true}); return r?["public_url"]?.GetValue<string>(); };
  await model.Load();
  ```
  `RevokeConnection`/`TurnOnWeb` deliberately go through the existing `/api/v2/claude/*` routes (already built,
  pre-T6) rather than `IAiLibrary`, since those aren't AI-job concerns — whatever object the host already uses
  for the Settings → "Connect Claude" page (probably `RemoteLibrary`) is the one to reuse here too.
- Goes: replacing the old AI section of `SettingsView`'s Claude/connections page — Mac: the toggle sits in the
  pane's own title row (no host chrome needed); Windows: the "Let AI tools read your library" card is the
  pane's first element, plus an **Other…** text button after Codex in "Connect a tool" (not in the Windows
  design — added so Claude Desktop/web stay reachable there too; see the pane's own doc-comment).
- Nothing to dispose. Reload (`Load()`) whenever the pane is shown again, same as the engines pane.
- Demo/preview data: none yet (`AiShots.AccessModel()` in the test project only) — a `AiDemo.Access()`-style
  helper could be added the same way as `AiDemo.Engines()` if a designer/preview needs one.

### The library side (already merged with this task)

- `ClaudeAccess` (Core) keeps `ToolsOn` (bool, default true) and `Reading` (`ReadingScopes`) in `claude.json`.
  `Check(bearer)` answers null while `ToolsOn` is false — that alone stops every connected tool at once.
- `Core/Ai/ToolAccess.cs`: `Scope(tool)` maps each MCP tool name to `"lectures"`/`"notes"`/`"canvas"`/null
  (always allowed); `Guard(tools, () => Task<(bool On, ReadingScopes Reading)>)` wraps a tool list so a call
  refuses (never removes the tool from `tools/list`) when access is off or its scope is off.
- `ClaudeWeb.cs` (the HTTP MCP door): wraps `.WithTools(...)` in the guard reading `access.ToolsOn`/`access.Reading`
  directly (same-process, no round trip), and answers 403 "AI tool access is off in Study Stash." before the
  bearer check while off.
- `Cli.cs`'s `mcp` command (the stdio door, laptops and this computer's own Claude Code/Desktop): wraps its
  tools via `ClaudeTools.RunStdioAsync(..., tools => ToolAccess.Guard(tools, Access))`, where `Access` calls
  `AiRemote.AccessAsync()` over HTTP, cached ~30 s (an older library without the route just means no limits —
  `(true, new ReadingScopes())`). `RunStdioAsync` grew an optional `wrapTools` parameter for this (Core, additive).
- `GET`/`POST /api/v2/ai/access` in `LibraryWeb.Ai.cs`: `{on?, reading?}` → `ToolAccessInfo` (connections come
  from the existing `ClaudeAccess.Grants()`, same list `/api/v2/claude` already shows).
- `IAiLibrary`/`AiRemote` (Core/Ai/AiApi.cs) grew `AccessAsync()`/`SetAccessAsync(on?, reading?)`.

### What isn't done

- Access requests (design 18's "Codex wants to read your library" banner) have no server source: nothing
  today asks another computer's Claude/Codex to request approval first — every connection today is either an
  OAuth sign-in (the student allows it once, at sign-in time) or a token made in Settings. The model and
  banner (T5's job) can support the words; there's nothing to wire them to yet.
- The design's connected-tool subtitle ("Mac mini (your library)", "Eli's MacBook" — which computer a tool
  runs on) isn't tracked anywhere: `ToolConnection` only carries `Kind` (`signin`/`token`), so the pane shows
  "Signed in from the web" / "Token" instead. Adding a device name would mean Claude (and any token-making
  client) sending one at connect time — out of scope here.
- `AiAccessModel.CopyOther` / the "Another tool: copy the JSON" menu item aren't covered by their own shot
  (the design doesn't draw that state); the words match the plan ("Copied. Paste it into the tool's settings.").

## T4 · Ask with any engine: the ask bar, its "Answer with" menu, and the recorder's compact chat (design 16)

### The shared engine menu

- `Views/{Mac,Win}AiEngineMenu.axaml` + `.axaml.cs`, over `ViewModels/AiAskModel.cs`'s `EngineMenuModel` (a
  header, `ObservableCollection<EngineMenuItem>`, an optional footer text + command, and a `Width`). Nobody
  makes an `EngineMenuModel` directly — `AiAskModel.Menu` builds and keeps it in sync with the picked engine.
  T5's `AiNotesModel` (rewrite) should expose its own `Menu` the same way ("Wrote the current notes" instead
  of "Default for questions" as the checked row's subtitle, no footer) and reuse these same two views.
- The control is a plain `UserControl`, not a `Popup` itself: the host places it. In the app, put it inside a
  `Button.Flyout` (`<Flyout Placement="Top"><ContentControl Content="{Binding Menu}"><ContentControl.ContentTemplate><DataTemplate x:DataType="vm:EngineMenuModel"><v:MacAiEngineMenu/></DataTemplate></ContentControl.ContentTemplate></ContentControl></Flyout>`)
  on the engine chip button — see `MacAiAskBar.axaml`/`WinAiAskBar.axaml` for the exact pattern (already
  wired that way, working, in both bars and both compact chats). Because Avalonia popups don't render into a
  screenshot, `AiShots.cs` draws `MacAiEngineMenu`/`WinAiEngineMenu` inline instead, positioned at the design's
  offsets — that inline drawing is picture scaffolding only, never a pattern for the real app.
- `AiAskModel.CloseMenu` is a plain `Action?` the *view's* code-behind sets (`m.CloseMenu = () =>
  EngineChip.Flyout?.Hide();` in `DataContextChanged`) so picking a row closes its own popup; the model never
  reaches for a control itself.

### The ask bar (under a lecture's notes)

- Controls: `Views/{Mac,Win}AiAskBar.axaml` + `.axaml.cs`, over `ViewModels/AiAskModel.cs`.
- Make it: `var ask = new AiAskModel(new AiRemote(cc.ServerUrl, cc.PoolKey));` then, once the host knows which
  lecture (or class) is open: `ask.LectureId = lecture.Id; ask.ClassName = lecture.ClassName; ask.OpenSettings
  = () => /* open Settings → AI engines */; ask.OnSource = s => /* jump to s.At in the open lecture, same as
  the old SourceChip click */;` and `await ask.Load()` before showing the bar (loads the engine menu from
  `GET /api/v2/ai/engines`; call it again each time the bar reappears — cheap, and it's how the menu picks up
  a defaults change made in Settings meanwhile).
- Goes: replaces the inline ask bar + answer card at the bottom of `MacLibrary.axaml`/`WinLibrary.axaml`'s
  lecture page (the `Border Name="Fade"` + the `Border Width="540" Height="44" ...` bar right after it, and
  the answer card above them — everything the old `LibraryModel.Question`/`Scope`/`AskCommand`/`Answer`/
  `Sources`/`Thinking` properties drove). `AiAskModel.Turns`/`Latest`/`HasLatest` replace `LibraryModel`'s
  single-answer fields; `AiAskModel` doesn't touch `LibraryModel` itself — the host swaps the view and starts
  handing it its own model instead. `OpenTurnSourceCommand` replaces the old `OpenSourceCommand` (same idea:
  jump to a moment in the transcript).
- Dispose: nothing to unsubscribe; dropping the model when the lecture closes is enough.
- Demo/preview data: `AiDemo.Ask()` (the menu-open picture: Claude Code is the library's default — checked —
  with Ollama picked for this one question, so the row's tinted) and `AiDemo.Chat()` (one answered turn) —
  both for `AiShots` only, never the running app.

### The recorder's compact chat and the quick panel's ask

- Controls: `Views/{Mac,Win}AiAskChat.axaml` + `.axaml.cs`, same `AiAskModel` shape as the bar, but no scope
  chip (it always asks about "this lecture" — the one recording, or live).
- Make it the same way, but for the recorder set `ask.Live = () => host.Recorder.Current?.Transcript();
  ask.LiveTitle = $"{(host.Recorder.Current?.ClassName is { Length: > 0 } c ? c : "This lecture")}, now";` —
  this is exactly what `Shell.AskLive`/`RecorderModel` do today with the plain `/api/v2/ask` route; the new
  model reaches the same place through `IAiLibrary.AskAsync` with an engine attached, so live asking gets the
  same engine-picking and fallback wording as everywhere else instead of always asking Ollama.
- Goes: replaces `RecorderModel`'s own `Chat`/`OnAsk`/`Question` chat list in the recorder's expanded view
  (`Views/MacRecorder.axaml`/`WinRecorder.axaml`'s chat panel) — `Shell.AskLive`/`Shell.Answer` and
  `RecorderModel.SourceChip`/`ChatMessage` become dead code once this is wired in and can go. The quick panel
  (`QuickModel`) can use a second `AiAskModel` the same way, with the ask default engine and no `Live` set, in
  place of whatever ask affordance it has today (check `Views/MacQuick.axaml`/`WinQuick.axaml` — if the quick
  panel doesn't already ask a question, this is additive there, not a replacement).
- Demo/preview data: `AiDemo.Chat()` again (same one-turn sample fits the compact thread too).

### Shared notes for both

- `AiAskModel.Engine` starts at the library's ask default (`AiOverview.Ask`) every time `Load()` runs and
  then holds whatever's picked until the model is dropped — it's a per-conversation choice, not saved back to
  Settings (only the "Answer with" menu's footer, "Change defaults in Settings", changes the actual default).
- `Scope`/`ScopeChoices` only matter for the ask bar (This lecture/This class/All classes → `AskRequest.Lecture`/
  `.Class`); the compact chat never shows the scope chip and always answers about the lecture it's attached to
  (or live, when `Live` is set).
- Icons the bar/chat/menu draw: `auto_awesome`, `unfold_more`/`expand_more`, `arrow_upward`/`send`, `check` —
  all already in `IconPaths.cs` (added by T3, none new here).
- New shared style classes, additive, in `Views/{Mac,Win}AiStyles.axaml` (`ai-menu-*`, `ai-chip*`, `ai-send`,
  `ai-bubble-*`, `ai-answer`, `ai-byline`, `ai-thinking`, `ai-field*`) and one new shared converter,
  `Converters.NotEmpty` (`Controls/Converters.cs`, WS2's file, additive) — a row/line that only shows when it
  has something to say (an engine's menu subtitle, a footer, an answer's byline).

### Test-only scaffolding (never app code)

- `AiShots.cs`'s `AskPage`/`AskChatPanel`/`AskComposition` build a plain 760×460 "page of notes" purely for
  the screenshot (mac-16/win-16): the real notes page is `MacLibrary`/`WinLibrary`'s, not ours. The page
  reuses WS2's `Fades.Under` helper for the bottom fade, same as the real lecture page does.

## Still to come from a later WS4 task (T5)

- The lecture notes' "Rewrite notes with" menu and the AI problem banners (designs 17, 18) — T5 should reuse
  `{Mac,Win}AiEngineMenu`/`EngineMenuModel` from T4 rather than building its own menu control (see the note
  under "The shared engine menu" above).
