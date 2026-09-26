using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using StudyStash.App.Services;
using StudyStash.App.Views;

namespace StudyStash.App.Tests;

/// <summary>The colour themes: worked out in OKLCH the way the design does, drawn the way Chrome draws them, every
/// token there for every theme, look and variant, kept in app.json, and swapped while the app runs.</summary>
public partial class ThemeTests
{
    static readonly SkinKind[] Looks = [SkinKind.Mac, SkinKind.Win];
    static readonly ThemeVariant[] Variants = [ThemeVariant.Light, ThemeVariant.Dark];

    static bool Close(Color a, Color b) => Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1;

    [Fact]
    public void Oklch_matches_Chrome()
    {
        foreach (var row in ThemeExpected.Rows)
        {
            var got = Oklch.ToColor(row.L, row.C, row.H, row.A);
            Assert.True(Close(got, Color.Parse(row.Rgb)), $"oklch({row.L} {row.C} {row.H}) is {got}; Chrome drew {row.Rgb}");
            Assert.Equal(row.Alpha, got.A);
        }
    }

    [Fact]
    public void Oklch_clips_like_Chrome_and_the_class_dots()
    {
        // Out of sRGB: each channel clipped, no gamut mapping (headless Chrome draws #B2E71E).
        Assert.Equal(Color.Parse("#B2E71E"), Oklch.ToColor(0.86, 0.21, 125));
        foreach (var dot in StudyStash.Core.ClassColors.Palette)
            Assert.Equal(Color.Parse(StudyStash.Core.ClassColors.Hex(dot)), Oklch.ToColor(dot.L, dot.C, dot.H));
        Assert.Equal(0x80, Oklch.ToColor(0.5, 0, 0, 0.5).A);
    }

    /// <summary>The theme's tokens in the skin are the design's: renderVals()'s formulas, as Chrome draws them.</summary>
    [AvaloniaFact]
    public void Every_theme_token_matches_the_design()
    {
        var built = new Dictionary<(string, SkinKind), ResourceDictionary>();
        foreach (var row in ThemeExpected.Rows)
        {
            var theme = ColourThemes.Find(row.Theme);
            Assert.Equal(row.Theme, theme.Name);
            var kind = row.Look.StartsWith("mac") ? SkinKind.Mac : SkinKind.Win;
            var variant = row.Look.EndsWith('D') ? ThemeVariant.Dark : ThemeVariant.Light;
            if (!built.TryGetValue((row.Theme, kind), out var d)) built[(row.Theme, kind)] = d = Skin.Build(kind, theme);
            string key = row.Token == "EdgeTintGlow" ? "EdgeTint" : row.Token;
            Assert.True(d.TryGetResource(key, variant, out var v), $"{row.Theme} {row.Look}: no {key}");
            // EdgeTint's glow is its last shadow: 0 2px 8px o(L, c, .3).
            Color got = v is BoxShadows s ? s[s.Count - 1].Color : ((ISolidColorBrush)v!).Color;
            string what = $"{row.Theme} {row.Look} {row.Token}: {got}, the design's {row.Rgb} at {row.Alpha}";
            Assert.True(Close(got, Color.Parse(row.Rgb)), what);
            Assert.True(got.A == row.Alpha, what);
        }
    }

    [AvaloniaFact]
    public void Every_theme_builds_every_token()
    {
        foreach (var theme in ColourThemes.All)
            foreach (var kind in Looks)
            {
                var d = Skin.Build(kind, theme);
                foreach (var variant in Variants)
                    foreach (var (key, type) in Skin.Keys(kind))
                    {
                        Assert.True(d.TryGetResource(key, variant, out var v), $"{theme.Name} {kind} {variant}: no {key}");
                        Assert.True(type.IsInstanceOfType(v), $"{theme.Name} {kind} {variant}: {key} is {v?.GetType().Name}, not {type.Name}");
                    }
            }
    }

    /// <summary>A light accent (Chalkboard, Highlighter) takes dark ink; so does every accent on dark Windows.</summary>
    [AvaloniaFact]
    public void Text_on_the_accent_reads()
    {
        var ink = Color.Parse("#16171A");
        foreach (var theme in ColourThemes.All)
        {
            Color OnAccent(SkinKind kind, ThemeVariant variant) =>
                Skin.Build(kind, theme).TryGetResource("OnAccent", variant, out var v) ? ((ISolidColorBrush)v!).Color : default;
            var light = theme.IsLight ? ink : Colors.White;
            Assert.Equal(light, OnAccent(SkinKind.Mac, ThemeVariant.Light));
            Assert.Equal(light, OnAccent(SkinKind.Mac, ThemeVariant.Dark));
            Assert.Equal(light, OnAccent(SkinKind.Win, ThemeVariant.Light));
            Assert.Equal(ink, OnAccent(SkinKind.Win, ThemeVariant.Dark));
        }
        Assert.Equal(new[] { "Chalkboard", "Highlighter" }, ColourThemes.All.Where(t => t.IsLight).Select(t => t.Name));
    }

    static string Source([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "StudyStash.App"));

    [GeneratedRegex(@"DynamicResource\s+(\w+)")] private static partial Regex Dynamic();
    [GeneratedRegex(@"GetResourceObservable\(""(\w+)""")] private static partial Regex Observed();
    [GeneratedRegex(@"(?:Fades\.Under\(\w+, |Bind\(\w+, \w+\.\w+Property, |Text\()""(\w+)""")] private static partial Regex Bound();
    [GeneratedRegex(@"\b[Mm]ac \? ""(\w+)"" : ""(\w+)""")] private static partial Regex ByLook();

    /// <summary>Every token the views, the styles and the controls ask for, and the look it's asked in (null: the
    /// file's own look, or either one for a file both looks share).</summary>
    static IEnumerable<(string File, string Key, SkinKind? Look)> Asks(string src)
    {
        var markup = Directory.GetFiles(Path.Combine(src, "Views"), "*.axaml").Append(Path.Combine(src, "Styles.axaml"));
        foreach (var file in markup)
            foreach (Match m in Dynamic().Matches(File.ReadAllText(file))) yield return (file, m.Groups[1].Value, null);
        var code = Directory.GetFiles(Path.Combine(src, "Views"), "*.cs").Concat(Directory.GetFiles(Path.Combine(src, "Controls"), "*.cs"));
        foreach (var file in code)
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Observed().Matches(text)) yield return (file, m.Groups[1].Value, null);
            foreach (Match m in Bound().Matches(text)) yield return (file, m.Groups[1].Value, null);
            foreach (Match m in ByLook().Matches(text))
            {
                yield return (file, m.Groups[1].Value, SkinKind.Mac);
                yield return (file, m.Groups[2].Value, SkinKind.Win);
            }
        }
    }

    [AvaloniaFact]
    public void Every_token_a_view_asks_for_exists()
    {
        string src = Source();
        Assert.True(File.Exists(Path.Combine(src, "Skin.cs")), $"no app source at {src}");
        var mac = Skin.Build(SkinKind.Mac, ColourThemes.Default);
        var win = Skin.Build(SkinKind.Win, ColourThemes.Default);
        bool In(ResourceDictionary d, string key) => Variants.All(v => d.TryGetResource(key, v, out _));
        var asks = Asks(src).ToList();
        Assert.Contains(asks, a => a.Key == "Accent");
        Assert.Contains(asks, a => a.Key == "Acrylic" && a.Look == SkinKind.Win);
        var missing = new List<string>();
        foreach (var (file, key, look) in asks)
        {
            string name = Path.GetFileName(file);
            SkinKind? own = look ?? (name.StartsWith("Mac") ? SkinKind.Mac : name.StartsWith("Win") ? SkinKind.Win : null);
            bool there = own switch
            {
                SkinKind.Mac => In(mac, key),
                SkinKind.Win => In(win, key),
                _ => In(mac, key) || In(win, key),
            };
            if (!there) missing.Add($"{name}: {key}{(own is { } k ? $" ({k})" : "")}");
        }
        Assert.Empty(missing.Distinct());
    }

    [Fact]
    public void Unknown_theme_is_Lagoon()
    {
        Assert.Equal("Lagoon", ColourThemes.Default.Name);
        Assert.Same(ColourThemes.Default, ColourThemes.Find("Neon"));
        Assert.Same(ColourThemes.Default, ColourThemes.Find(""));
        Assert.Same(ColourThemes.Default, ColourThemes.Find(null));
        Assert.Equal("Original red", ColourThemes.Find("original red").Name);
        Assert.Equal(new[] { "Lagoon", "Library", "Blueprint", "Marmalade", "Plum", "Chalkboard", "Highlighter", "Terracotta", "Graphite", "Original red" },
            ColourThemes.All.Select(t => t.Name));
    }

    [Fact]
    public void The_theme_is_kept_in_app_json()
    {
        string home = Path.Combine(Path.GetTempPath(), "studystash-theme-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Equal("Lagoon", AppSettings.Load(home).Theme);
            new AppSettings { Theme = "Plum" }.Save(home);
            Assert.Contains("\"theme\": \"Plum\"", File.ReadAllText(AppSettings.PathIn(home)));
            Assert.Equal("Plum", AppSettings.Load(home).Theme);
            // An app.json from before colour themes: Lagoon.
            File.WriteAllText(AppSettings.PathIn(home), "{\"setup_done\": true}");
            Assert.Equal("Lagoon", AppSettings.Load(home).Theme);
        }
        finally
        {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>The status dot and the setup's result line take their colours from the look, by class.</summary>
    [AvaloniaFact]
    public void Status_colours_follow_the_look()
    {
        var app = (App)Application.Current!;
        Skin.UseTheme(ColourThemes.Default);
        foreach (var kind in Looks)
        {
            app.UseSkin(kind);
            var d = Skin.Build(kind);
            Color Token(string key) => d.TryGetResource(key, ThemeVariant.Light, out var v) ? ((ISolidColorBrush)v!).Color : default;
            var good = new Avalonia.Controls.Shapes.Ellipse { Classes = { "status" } };
            var warn = new Avalonia.Controls.Shapes.Ellipse { Classes = { "status", "warn" } };
            var worked = new TextBlock { Classes = { "result" } };
            var failed = new TextBlock { Classes = { "result", "bad" } };
            var window = new Window { RequestedThemeVariant = ThemeVariant.Light, Content = new StackPanel { Children = { good, warn, worked, failed } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Token("Good"), ((ISolidColorBrush)good.Fill!).Color);
            Assert.Equal(Token("Warn"), ((ISolidColorBrush)warn.Fill!).Color);
            Assert.Equal(Token("Ok"), ((ISolidColorBrush)worked.Foreground!).Color);
            Assert.Equal(Token("Warn"), ((ISolidColorBrush)failed.Foreground!).Color);
            window.Close();
        }
    }

    /// <summary>Picking a theme repaints what's open: DynamicResources, and the fades under the library's bars.</summary>
    [AvaloniaFact]
    public void A_theme_swaps_live()
    {
        var app = (App)Application.Current!;
        app.UseSkin(SkinKind.Mac);
        Skin.UseTheme(ColourThemes.Default);
        var swatch = new Border { Width = 20, Height = 20, [!Border.BackgroundProperty] = new DynamicResourceExtension("Accent") };
        var fade = new Border { Width = 20, Height = 20 };
        Fades.Under(fade, "Win", 0.7);
        var window = new Window { Width = 100, Height = 100, RequestedThemeVariant = ThemeVariant.Light, Content = new StackPanel { Children = { swatch, fade } } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var plum = ColourThemes.Find("Plum");
            Assert.Equal(ColourThemes.Default.O(0.58, 0.12), ((ISolidColorBrush)swatch.Background!).Color);
            Assert.Equal(ColourThemes.Default.Neutral(0.985, 1), ((LinearGradientBrush)fade.Background!).GradientStops[1].Color);

            Skin.UseTheme(plum);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(plum, Skin.Theme);
            Assert.Equal(plum.O(plum.L, plum.C), ((ISolidColorBrush)swatch.Background!).Color);
            Assert.Equal(plum.Neutral(0.985, 1), ((LinearGradientBrush)fade.Background!).GradientStops[1].Color);

            window.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(plum.O(Math.Min(plum.L + 0.08, 0.72), plum.C), ((ISolidColorBrush)swatch.Background!).Color);
            Assert.Equal(plum.Neutral(0.21, 1.5), ((LinearGradientBrush)fade.Background!).GradientStops[1].Color);
        }
        finally
        {
            window.Close();
            Skin.UseTheme(ColourThemes.Default);
        }
    }

    /// <summary>Settings → Appearance: picking a swatch swaps the live accent and saves the choice, no restart.</summary>
    [AvaloniaFact]
    public void Picking_a_theme_in_settings_saves_it_and_repaints()
    {
        var app = (App)Application.Current!;
        app.UseSkin(SkinKind.Mac);
        Skin.UseTheme(ColourThemes.Default);
        string home = Path.Combine(Path.GetTempPath(), "studystash-picktheme-" + Guid.NewGuid().ToString("N"));
        var host = new AppHost(home);
        var model = SettingsModel.Make(host);
        try
        {
            var swatch = new Border { Width = 20, Height = 20, [!Border.BackgroundProperty] = new DynamicResourceExtension("Accent") };
            var window = new Window { Width = 40, Height = 40, RequestedThemeVariant = ThemeVariant.Light, Content = swatch };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(ColourThemes.Default.O(0.58, 0.12), ((ISolidColorBrush)swatch.Background!).Color);

                model.PickThemeCommand.Execute("Plum");
                Dispatcher.UIThread.RunJobs();
                var plum = ColourThemes.Find("Plum");
                Assert.Same(plum, Skin.Theme);
                Assert.Equal("Plum", model.ColourTheme);
                Assert.True(model.Themes.Single(t => t.Name == "Plum").Chosen);
                Assert.False(model.Themes.Single(t => t.Name == "Lagoon").Chosen);
                Assert.Equal(plum.O(plum.L, plum.C), ((ISolidColorBrush)swatch.Background!).Color);
                Assert.Contains("\"theme\": \"Plum\"", File.ReadAllText(AppSettings.PathIn(home)));
                Assert.Equal("Plum", AppSettings.Load(home).Theme);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            model.Dispose();
            host.Dispose();
            Skin.UseTheme(ColourThemes.Default);
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }
}
