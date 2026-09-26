# WS3 canvas-ui: wiring the controls into the app

One section per control, filled in as each task builds it. The integration step at the end of the night reads this
and does the edits it lists in files WS3 couldn't touch (WS2/WS6's lane) — WS3 never edits those files itself.

---------------------------------------------------------------------------------------------------------------------

## T2 · Canvas status (design 08) and Settings → Canvas (design 06)

### Control: CanvasStatusModel + `Views/{Mac,Win}CanvasStatus.axaml`

**Where**: two places share the one model/view pair —
1. Settings → Canvas's header (`Views/{Mac,Win}CanvasSettings.axaml` already hosts its own `Status` — see the
   Settings control below; nothing extra to wire there).
2. The library window's Due page, at the top of the list column, whenever Canvas isn't `connected` or `syncing` (i.e.
   the student hasn't finished connecting, or something's wrong) — design 09 doesn't draw this, but the brief's
   "Due page's top when the state isn't connected or syncing" is the rule; the card links to the Settings → Canvas
   section (`OnShowMeHow`/`Connect` should open Settings and select "Canvas") or the connect flow (see T5's wiring).

**Create**: one `CanvasContext.For(host)` for the whole app (build it once, e.g. in `AppHost` or wherever
`SettingsModel`/`ShellModel` are constructed, and hand the same instance to every Canvas view model — the client,
clock and actions are the same everywhere). One `CanvasWatch(context)` shared the same way, so Settings and the Due
page see the same poll rather than each running their own.

**Bind/Call**: `new CanvasStatusModel(context)`; `Show(state)` whenever the watch reports a new `CanvasApi.State`
(from `LoadAsync`/`RefreshAsync`, not fetched by the status model itself). `Act` runs whatever the state's button
does; `OnConnect`/`OnShowMeHow` are callbacks the host sets to open the connect flow (T5) — until T5 is wired in,
point them at opening Settings' Canvas section. `Close` (Windows' × / nothing on the two states without a button)
just hides the card for the session — no call needed.

**Refresh**: subscribes to `CanvasWatch.Changed` (see `CanvasSettingsModel`'s `OnWatchChanged` for the pattern —
Settings' status field does this already); the Due page's copy should do the same. The watch is `Start()`ed once
the app has a library (host.Client() answers) and `Stop()`ed when the app quits — not per-view, since it's shared.

**Words the shell must change**: none — every string comes from `CanvasWords`/`CanvasStatusModel` already.

---------------------------------------------------------------------------------------------------------------------

### Control: CanvasSettingsModel + `Views/{Mac,Win}CanvasSettings.axaml`

**Where**: `SettingsView.axaml`'s Canvas section (`<StackPanel IsVisible="{Binding OnCanvas}">`, the block that
currently holds the plain "Your school's Canvas" field and the three push buttons — `SettingsView.axaml` lines
~250–281 as of this branch).

**Create**: `SettingsModel` (in `Services/Settings.cs`) gets one new member:
```csharp
public CanvasSettingsModel Canvas { get; }
```
built in the constructor as `new CanvasSettingsModel(CanvasContext.For(host), canvasWatch)` — reuse the single
shared `CanvasWatch` from the status control's wiring above, don't make a second one.

**Bind/Call**: replace the whole `<StackPanel IsVisible="{Binding OnCanvas}">` block in `SettingsView.axaml` with:
```xml
<v:MacCanvasSettings IsVisible="{Binding OnCanvas}" DataContext="{Binding Canvas}" />
<!-- or v:WinCanvasSettings on Windows -->
```
(however `SettingsView` already picks Mac vs. Windows chrome elsewhere — follow that pattern). Call
`await Canvas.LoadAsync()` when `Section` becomes `"Canvas"` (in `OnSectionChanged`, guard so it only loads the
first time the section is opened, or every time — `LoadAsync` is idempotent and cheap, so every time is simplest)
and call `canvasWatch.Start()`/`Stop()` on entering/leaving the section (or just run the watch for the app's whole
life once a library exists, matching the status control's wiring above — simplest, and Settings' own
`OnWatchChanged` handler already exists to pick up the resulting `Changed` events).

**Refresh**: `CanvasSettingsModel.OnWatchChanged` already refreshes `Status` from the watch; nothing else needed.

**Edits needed in files WS3 couldn't touch**:
- `Services/Settings.cs` (`SettingsModel`): add the `Canvas` property (built once, in the constructor, from
  `CanvasContext.For(host)` and the shared watch); delete `CanvasUrl`, `CanvasSay`, `CanvasLine`, `CanvasBusy`,
  `CanvasLinks`, `HasCanvasLinks`, `LoadCanvasAsync`, `SetUpExtensionCommand`, `FindCoursesCommand`,
  `SyncCanvasCommand`, `SaveCanvasUrlCommand`, and the `_ = LoadCanvasAsync();` call in the constructor. Keep
  `OnCanvas`/`Section == "Canvas"` — the nav button and section switch stay exactly as they are.
- `Views/SettingsView.axaml`: replace the Canvas `StackPanel` (see above) with the new control; the "Canvas" nav
  button (`Classes.on="{Binding OnCanvas}"`) is untouched.
- `Services/CanvasApi.cs` has an unrelated `ReadOnCanvas` field on some other record (`Assignment`?) — don't confuse
  it with `SettingsModel.OnCanvas`; nothing to change there.

**Words the shell must change**: none — `SettingsView`'s old Canvas copy ("Bring in each class's assignments…") is
replaced wholesale by the control's own intro line, already worded from the design.

---------------------------------------------------------------------------------------------------------------------

*(T3–T6 append their own sections below as they land.)*

---------------------------------------------------------------------------------------------------------------------

## T5 · Connecting Canvas (design 07)

### Control: CanvasConnectModel + `Views/{Mac,Win}CanvasConnect.axaml`

**Where**: two places host the same control/model pair —
1. First-run setup, as its own step after Classes (Mac "5 of 5 · Optional"; Windows "5 of 6 · Optional", before the
   Taskbar step). `ShowFooter = false` here — setup's own footer supplies Next/Finish (gated by `CanFinish`) and
   "Skip for now" (call `Skip()`, which just invokes `OnSkip`).
2. A small window of its own, opened from the status card's Connect (`CanvasStatusModel.OnConnect`) or Show me how
   (`OnShowMeHow`). `ShowFooter = true` here — the control draws its own Skip for now / Back / Finish and the host's
   window closes on `OnFinish`/`OnSkip` (Back can also just close the window, or be omitted by hiding the button if
   there's nowhere to go back to — the setup case is the one that really uses it).

**Create**: `new CanvasConnectModel(context, watch)` — the same `CanvasContext.For(host)` and shared `CanvasWatch`
already wired for T2 (don't build a second watch; the extension-waiting and sync-progress steps poll through it).

**Bind/Call**:
- `StepLabel` — the host sets this text ("Step 5 of 5 · Optional" / "Step 5 of 6 · Optional"); the control just
  shows it above the title.
- `await StartAsync(state, classes)` once, when the step/window opens, with the watch's current `State` (call
  `watch.RefreshAsync()` first if it might be stale) and the classes list (`client.ClassesAsync()`, or whatever the
  host already has handy) — this is what decides which of the five steps opens.
- `FinishLabel` — "Finish" in the small dialog and on Mac's last setup step; "Next" on Windows setup (there's a
  Taskbar step after). Set once when the control is created; it doesn't change itself.
- `CanFinish` drives the host's own Next/Finish button's `IsEnabled` when `ShowFooter=false` (setup); when
  `ShowFooter=true` the control's own Finish button already reads it.
- `OnSkip`/`OnBack`/`OnFinish` — callbacks the host sets: Skip and Finish both move setup on (Skip without saving
  anything further; Finish the same, since every step already saved its own progress as it went — there's no extra
  "commit" step). Back returns to the Classes step in setup, or closes the small dialog.

**Refresh**: the control needs no external refresh call beyond `StartAsync` — it listens to `CanvasWatch.Changed`
itself (extension turning up, signing in, the syncing progress bar) via the same watch T2 already starts. **Stop the
watch when the control unloads** only if nothing else is still using it — since Settings and the status card share
the same watch instance, the host should simply not stop it here; only stop the watch on app shutdown or when the
student has no library at all (T2's wiring already covers start/stop for the whole app's life). If a future control
turns out to need a *second*, private watch (e.g. the small dialog outliving Settings), stop that one specifically
in the window's closed handler.

**Edits needed in files WS3 couldn't touch**:
- Setup's step list/model (WS2/WS6's `Setup.cs` and `Views/{Mac,Win}Setup.axaml`, once they're rebuilt in the new
  Liquid Glass/Fluent look): add a step after Classes that hosts `MacCanvasConnect`/
  `WinCanvasConnect` with `DataContext` = a `CanvasConnectModel` built as above, `ShowFooter=false`, and the step's
  own Skip/Back/Next/Finish wired to the model's callbacks and `CanFinish`.
- The status card's `CanvasStatusModel.OnConnect`/`OnShowMeHow` (wherever the status card is finally hosted per T2's
  wiring): open a small window (title "Canvas", roughly 640×640 is plenty) containing the same control with
  `ShowFooter=true`; `OnShowMeHow` should additionally nudge the model to open at step 2 if it doesn't already
  (`StartAsync` already does this from a `no_extension` state, so passing the real current state is enough).

**Words the shell must change**: none — every string on this screen comes from the control/model already, using the
design's exact copy (`CanvasConnectModel.Title`/`Intro`, the step titles, and `CanvasWords` for the rest).

**Test-only frames**: `CanvasFrames.MacSetup`/`WinSetup(content)` recreate just enough of the setup window's chrome
(the sidebar, its checkmarks, and — Windows only — the title bar) for the design-07 shots to line up with their
refs; they are not the real setup window and integration should not read them for layout guidance beyond that.
