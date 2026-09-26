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

    /// <summary>The Connected fixtures Settings → Canvas reads for design 06: state, overview, classes, and the
    /// POST routes its buttons hit (never actually called by a shot, but harmless to answer).</summary>
    static FakeLibrary ConnectedLibrary(string state = "state-connected") => new FakeLibrary()
        .Json(HttpMethod.Get, "/api/v2/canvas/state", state)
        .Json(HttpMethod.Get, "/api/v2/canvas", "canvas")
        .Json(HttpMethod.Get, "/api/v2/canvas/classes", "classes")
        .Json(HttpMethod.Post, "/api/v2/canvas/courses", "canvas")
        .Json(HttpMethod.Post, "/api/v2/canvas/scout", "canvas")
        .Json(HttpMethod.Post, "/api/v2/canvas", "canvas");

    static async Task<CanvasSettingsModel> Settings(string state = "state-connected")
    {
        var model = new CanvasSettingsModel(CanvasFixtures.Context(ConnectedLibrary(state)));
        await model.LoadAsync();
        return model;
    }

    [AvaloniaFact]
    public async Task Mac_settings()
    {
        var model = await Settings();
        foreach (var t in Themes)
        {
            Control? built = null;
            Shot.Take("mac-06-canvas-settings", SkinKind.Mac, t, () => built = CanvasFrames.MacSettings(new MacCanvasSettings { DataContext = model }));
            AssertIcons(built!);
        }
        var signedOut = await Settings("state-signed-out");
        foreach (var t in Themes)
            Shot.Take("mac-06-canvas-settings-signed-out", SkinKind.Mac, t, () => CanvasFrames.MacSettings(new MacCanvasSettings { DataContext = signedOut }));
    }

    [AvaloniaFact]
    public async Task Win_settings()
    {
        var model = await Settings();
        foreach (var t in Themes)
        {
            Control? built = null;
            Shot.Take("win-06-canvas-settings", SkinKind.Win, t, () => built = CanvasFrames.WinSettings(new WinCanvasSettings { DataContext = model }));
            AssertIcons(built!);
        }
        var signedOut = await Settings("state-signed-out");
        foreach (var t in Themes)
            Shot.Take("win-06-canvas-settings-signed-out", SkinKind.Win, t, () => CanvasFrames.WinSettings(new WinCanvasSettings { DataContext = signedOut }));
    }
}
