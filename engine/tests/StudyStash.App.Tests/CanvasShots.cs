using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using StudyStash.App;
using StudyStash.App.Controls;
using StudyStash.App.Services;
using StudyStash.App.ViewModels;
using StudyStash.App.Views;

namespace StudyStash.App.Tests;

/// <summary>
/// Every Canvas surface (design 06 to 12) drawn the way the design shows it, light and dark, from the same fixtures
/// the client tests read: the ref names exactly (mac-06-canvas-settings, …), plus extras named "&lt;ref&gt;-&lt;what&gt;"
/// that aren't compared. Each shot also walks its control's logical tree and asserts every <see cref="Icon.Glyph"/>
/// is in <see cref="IconPaths.All"/> — a missing icon draws nothing and is easy to miss otherwise.
/// </summary>
public class CanvasShots
{
    static readonly ThemeVariant[] Themes = [ThemeVariant.Light, ThemeVariant.Dark];

    static CanvasShots() => Environment.SetEnvironmentVariable("STUDYSTASH_STILL", "1");

    static void AssertIcons(Control root)
    {
        foreach (var icon in root.GetLogicalDescendants().OfType<Icon>())
        {
            if (icon.Glyph.Length == 0) continue; // e.g. the syncing card, which draws a Spinner instead
            Assert.True(IconPaths.All.ContainsKey(icon.Glyph), $"Icon glyph '{icon.Glyph}' isn't in IconPaths.All");
        }
    }

    static CanvasStatusModel Status(string fixture)
    {
        var m = new CanvasStatusModel(CanvasFixtures.Context());
        m.Show(CanvasFixtures.Load<CanvasApi.State>(fixture));
        return m;
    }

    /// <summary>Design 08's eight cards, in the design's reading order (left column then right, top to bottom).</summary>
    static (string Label, Control Card)[] StateCards(Func<CanvasStatusModel, Control> view) =>
    [
        ("Not set up", view(Status("state-not-set-up"))),
        ("Extension not set up", view(Status("state-no-extension"))),
        ("Chrome not checking in", view(Status("state-chrome-away"))),
        ("Signed out", view(Status("state-signed-out"))),
        ("Syncing", view(Status("state-syncing"))),
        ("Connected", view(Status("state-connected"))),
        ("Extension updated", view(Status("state-updated"))),
        ("Error", view(Status("state-error"))),
    ];

    [AvaloniaFact]
    public void Mac_states()
    {
        foreach (var t in Themes)
        {
            Control? built = null;
            Shot.Take("mac-08-canvas-states", SkinKind.Mac, t, () => built = CanvasFrames.MacStates(StateCards(m => new MacCanvasStatus { DataContext = m })));
            AssertIcons(built!);
        }
    }

    [AvaloniaFact]
    public void Win_states()
    {
        foreach (var t in Themes)
        {
            Control? built = null;
            Shot.Take("win-08-canvas-states", SkinKind.Win, t, () => built = CanvasFrames.WinStates(StateCards(m => new WinCanvasStatus { DataContext = m })));
            AssertIcons(built!);
        }
    }

    // ---- design 07: connecting Canvas ----

    static CanvasConnectModel Connect(FakeLibrary handler) =>
        new(CanvasFixtures.Context(handler), new CanvasWatch(CanvasFixtures.Context(handler)));

    static async Task<CanvasConnectModel> Step2Async()
    {
        var handler = new FakeLibrary()
            .Json(HttpMethod.Get, "/api/v2/canvas/state", "state-no-extension")
            .Json(HttpMethod.Get, "/api/v2/canvas/extension", "extension");
        var m = Connect(handler);
        m.StepLabel = "Step 5 of 5 · Optional";
        m.ShowFooter = true;
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-no-extension"), []);
        return m;
    }

    static async Task<CanvasConnectModel> SchoolStepAsync()
    {
        var m = Connect(new FakeLibrary());
        m.ShowFooter = true;
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-not-set-up"), []);
        return m;
    }

    static async Task<CanvasConnectModel> SignedOutStepAsync()
    {
        var m = Connect(new FakeLibrary());
        m.ShowFooter = true;
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-signed-out"), []);
        return m;
    }

    static async Task<CanvasConnectModel> MatchStepAsync()
    {
        var unmatched = new List<CanvasApi.ClassRow>
        {
            new() { Class = "CS 101", Linked = false, Suggested = "4201" },
            new() { Class = "BIO 110", Linked = false, Suggested = "4202" },
        };
        var handler = new FakeLibrary()
            .Json(HttpMethod.Get, "/api/v2/canvas/state", "state-connected")
            .Json(HttpMethod.Post, "/api/v2/canvas/courses", "canvas")
            .Json(HttpMethod.Get, "/api/v2/canvas/classes", JsonSerializer.Serialize(unmatched, CanvasApi.Json));
        var m = Connect(handler);
        m.ShowFooter = true;
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-connected"), unmatched); // finds courses and lands on step 4 itself
        return m;
    }

    static async Task<CanvasConnectModel> SyncingStepAsync()
    {
        var handler = new FakeLibrary()
            .Json(HttpMethod.Get, "/api/v2/canvas/state", "state-syncing")
            .Json(HttpMethod.Post, "/api/v2/canvas", "canvas");
        var m = Connect(handler);
        m.ShowFooter = true;
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-connected"), [new() { Class = "CS 101", Linked = true, Canvas = new() { Id = "4201" } }]);
        await m.SyncNowCommand.ExecuteAsync(null);
        m.Syncing = true;
        m.SyncProgress = 0.4;
        return m;
    }

    [AvaloniaFact]
    public async Task Mac_connect()
    {
        foreach (var t in Themes)
        {
            var m = await Step2Async();
            Control? built = null;
            Shot.Take("mac-07-canvas-connect", SkinKind.Mac, t, () => built = CanvasFrames.MacSetup(new MacCanvasConnect { DataContext = m }));
            AssertIcons(built!);
        }
        var school = await SchoolStepAsync();
        var signedOut = await SignedOutStepAsync();
        var match = await MatchStepAsync();
        var syncing = await SyncingStepAsync();
        Shot.Take("mac-07-canvas-connect-school", SkinKind.Mac, ThemeVariant.Light, () => CanvasFrames.MacSetup(new MacCanvasConnect { DataContext = school }));
        Shot.Take("mac-07-canvas-connect-signed-out", SkinKind.Mac, ThemeVariant.Light, () => CanvasFrames.MacSetup(new MacCanvasConnect { DataContext = signedOut }));
        Shot.Take("mac-07-canvas-connect-match", SkinKind.Mac, ThemeVariant.Light, () => CanvasFrames.MacSetup(new MacCanvasConnect { DataContext = match }));
        Shot.Take("mac-07-canvas-connect-syncing", SkinKind.Mac, ThemeVariant.Light, () => CanvasFrames.MacSetup(new MacCanvasConnect { DataContext = syncing }));
    }

    [AvaloniaFact]
    public async Task Win_connect()
    {
        foreach (var t in Themes)
        {
            var m = await Step2Async();
            Control? built = null;
            Shot.Take("win-07-canvas-connect", SkinKind.Win, t, () => built = CanvasFrames.WinSetup(new WinCanvasConnect { DataContext = m }));
            AssertIcons(built!);
        }
        var school = await SchoolStepAsync();
        var signedOut = await SignedOutStepAsync();
        var match = await MatchStepAsync();
        var syncing = await SyncingStepAsync();
        Shot.Take("win-07-canvas-connect-school", SkinKind.Win, ThemeVariant.Light, () => CanvasFrames.WinSetup(new WinCanvasConnect { DataContext = school }));
        Shot.Take("win-07-canvas-connect-signed-out", SkinKind.Win, ThemeVariant.Light, () => CanvasFrames.WinSetup(new WinCanvasConnect { DataContext = signedOut }));
        Shot.Take("win-07-canvas-connect-match", SkinKind.Win, ThemeVariant.Light, () => CanvasFrames.WinSetup(new WinCanvasConnect { DataContext = match }));
        Shot.Take("win-07-canvas-connect-syncing", SkinKind.Win, ThemeVariant.Light, () => CanvasFrames.WinSetup(new WinCanvasConnect { DataContext = syncing }));
    }
}
