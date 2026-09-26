using System.Text;
using System.Text.Json;
using Avalonia.Media;
using StudyStash.App.Services;

namespace StudyStash.App.Tests;

/// <summary>Fixture JSON for the Canvas client's and words' tests: the design's data, at the design's "now".</summary>
public static class CanvasFixtures
{
    /// <summary>Thu 25 Sep 2025, 10:24 in America/Los_Angeles.</summary>
    public static readonly DateTimeOffset Now = new(2025, 9, 25, 17, 24, 0, TimeSpan.Zero);
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
    static readonly string[] Classes = ["CS 101", "BIO 110", "CALC II", "HIST 210"];

    public static string Text(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "canvas-api", name + ".json"), Encoding.UTF8);

    public static T Load<T>(string name) => JsonSerializer.Deserialize<T>(Text(name), CanvasApi.Json)!;

    /// <summary>A context wired to a fake library (or none, for a view model with nothing to load from), the
    /// design's clock, class dots CS 101 = dot 0, BIO 110 = 1, CALC II = 2, HIST 210 = 3, and actions that only
    /// record what they were asked (never launch anything). <paramref name="log"/>, when given, collects each
    /// action's name and argument in order.</summary>
    public static CanvasContext Context(FakeLibrary? handler = null, string? home = null, List<(string What, string Arg)>? log = null)
    {
        var client = handler is null ? null : new CanvasClient("https://library.test", "test-key", handler.Client());
        void Log(string what, string arg = "") => log?.Add((what, arg));
        var actions = new CanvasActions(
            OpenUrl: url => Log("OpenUrl", url),
            OpenInChrome: url => Log("OpenInChrome", url),
            OpenChrome: () => Log("OpenChrome"),
            OpenChromeExtensions: () => Log("OpenChromeExtensions"),
            RevealFolder: dir => Log("RevealFolder", dir),
            OpenFile: path => Log("OpenFile", path),
            PrepareExtension: (key, canvasUrl) =>
            {
                Log("PrepareExtension", $"{key} {canvasUrl}");
                return home is null ? "" : Path.Combine(home, "chrome-extension");
            });
        IBrush DotOf(string cls) => Skin.ClassDot(Array.IndexOf(Classes, cls) is var i && i >= 0 ? i : 0);
        return new CanvasContext(client, new CanvasClock(() => Now, Zone), DotOf, actions, home ?? "");
    }
}
