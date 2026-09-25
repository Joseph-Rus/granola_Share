using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace StudyStash.App;

/// <summary>Which look: the Mac's (SF Pro, vibrancy, 12–14 pt corners) or Windows 11's (Segoe UI, Mica and Acrylic,
/// 4–8 px corners). Both are the same design; each follows its system's conventions.</summary>
public enum SkinKind
{
    Mac,
    Win,
}

/// <summary>
/// The design's tokens (Study Stash Surfaces: light and dark for each look), as resources the views ask for by
/// name: <c>{DynamicResource Fg2}</c>. Colors are the design's CSS values exactly (rgba alpha to #AARRGGBB).
/// </summary>
public static class Skin
{
    public static SkinKind Current { get; set; } = OperatingSystem.IsWindows() ? SkinKind.Win : SkinKind.Mac;

    /// <summary>STUDYSTASH_SKIN=mac|win shows the other system's look (for screenshots, or to compare).</summary>
    public static SkinKind FromEnvironment() =>
        Environment.GetEnvironmentVariable("STUDYSTASH_SKIN")?.ToLowerInvariant() switch
        {
            "win" or "windows" => SkinKind.Win,
            "mac" or "macos" => SkinKind.Mac,
            _ => OperatingSystem.IsWindows() ? SkinKind.Win : SkinKind.Mac,
        };

    static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));

    static ResourceDictionary Colors(params (string Key, string Hex)[] colors)
    {
        var d = new ResourceDictionary();
        foreach (var (k, h) in colors) d[k] = B(h);
        return d;
    }

    // The class dots: the design's OKLCH hues (StudyStash.Core.ClassColors), the same in light and dark.
    public static readonly IBrush[] ClassDots = [.. StudyStash.Core.ClassColors.Palette.Select(c => (IBrush)new SolidColorBrush(Color.Parse(StudyStash.Core.ClassColors.Hex(c))))];

    public static IBrush ClassDot(int index) => ClassDots[((index % ClassDots.Length) + ClassDots.Length) % ClassDots.Length];

    /// <summary>"Library connected" green: oklch(0.7 0.15 150).</summary>
    public static readonly IBrush Good = B("#4CB86A");
    /// <summary>A problem: the accent's amber cousin, readable on both grounds.</summary>
    public static readonly IBrush Warn = B("#D18E35");

    public static ResourceDictionary Build(SkinKind kind)
    {
        var d = new ResourceDictionary();
        var light = new ResourceDictionary();
        var dark = new ResourceDictionary();
        if (kind == SkinKind.Mac)
        {
            Merge(light, Colors(("Ground", "#E4E2DF"), ("Mat", "#E6F4F4F4"), ("Win", "#FFFFFF"), ("Side", "#F0EFEE"), ("Fg", "#1D1D1F"),
                ("Fg2", "#8F000000"), ("Fg3", "#57000000"), ("Sep", "#1A000000"), ("Fill", "#0D000000"), ("Fill2", "#17000000"),
                ("Raised", "#FFFFFF"), ("Accent", "#E5484D"), ("OnAccent", "#FFFFFF"), ("AccentTint", "#29E5484D"), ("Hl", "#38E5484D"),
                ("AccentText", "#E5484D"), ("Hover", "#0A000000"), ("Press", "#14000000")));
            light["Shadow"] = BoxShadows.Parse("0 12 40 0 #29000000, 0 0 0 0.5 #24000000");
            light["ShadowSmall"] = BoxShadows.Parse("0 1 2 0 #24000000");
            light["RaisedShadow"] = BoxShadows.Parse("0 0 0 0.5 #1A000000, 0 1 2 0 #1F000000");
            light["Ring3"] = BoxShadows.Parse("0 0 0 3 #29E5484D");
            light["Ring4"] = BoxShadows.Parse("0 0 0 4 #29E5484D");
            Merge(dark, Colors(("Ground", "#161616"), ("Mat", "#EB2A2A2C"), ("Win", "#1E1E1E"), ("Side", "#29292B"), ("Fg", "#F5F5F7"),
                ("Fg2", "#94FFFFFF"), ("Fg3", "#52FFFFFF"), ("Sep", "#1AFFFFFF"), ("Fill", "#12FFFFFF"), ("Fill2", "#1FFFFFFF"),
                ("Raised", "#48484A"), ("Accent", "#EC5D5E"), ("OnAccent", "#FFFFFF"), ("AccentTint", "#38EC5D5E"), ("Hl", "#57EC5D5E"),
                ("AccentText", "#EC5D5E"), ("Hover", "#0DFFFFFF"), ("Press", "#1AFFFFFF")));
            dark["Shadow"] = BoxShadows.Parse("0 12 40 0 #8C000000, 0 0 0 0.5 #24FFFFFF");
            dark["ShadowSmall"] = BoxShadows.Parse("0 1 2 0 #59000000");
            dark["RaisedShadow"] = BoxShadows.Parse("0 0 0 0.5 #1AFFFFFF, 0 1 2 0 #59000000");
            dark["Ring3"] = BoxShadows.Parse("0 0 0 3 #38EC5D5E");
            dark["Ring4"] = BoxShadows.Parse("0 0 0 4 #38EC5D5E");
            d["TextFont"] = new FontFamily("SF Pro Text, avares://StudyStash/Assets/Fonts#Inter");
            d["DisplayFont"] = new FontFamily("SF Pro Display, avares://StudyStash/Assets/Fonts#Inter Display");
            // The design reads notes in New York. Where it isn't installed, Charter (on every Mac) reads as well.
            d["SerifFont"] = new FontFamily("New York, Charter, Georgia, avares://StudyStash/Assets/Fonts#Inter");
            d["RadiusPanel"] = new CornerRadius(14);
        }
        else
        {
            Merge(light, Colors(("Ground", "#DADCE0"), ("Mica", "#F3F3F3"), ("Layer", "#8CFFFFFF"), ("LayerStroke", "#0F000000"),
                ("Acrylic", "#F2F9F9F9"), ("FlyStroke", "#1A000000"), ("Footer", "#EEEEEE"), ("Card", "#BFFFFFFF"), ("CardStroke", "#0F000000"),
                ("Fg", "#E6000000"), ("Fg2", "#9E000000"), ("Fg3", "#73000000"), ("Sep", "#14000000"), ("Subtle", "#0A000000"),
                ("Subtle2", "#0F000000"), ("Ctrl", "#CCFFFFFF"), ("CtrlStroke", "#12000000"), ("CtrlBottom", "#2E000000"),
                ("Accent", "#C4383D"), ("OnAccent", "#FFFFFF"), ("AccentText", "#A92E33"), ("Hl", "#29C4383D"), ("Taskbar", "#EEEEEE"),
                ("Fill", "#0A000000"), ("Fill2", "#0F000000"), ("Hover", "#0A000000"), ("Press", "#05000000")));
            light["Shadow"] = BoxShadows.Parse("0 8 16 0 #24000000");
            light["ShadowLg"] = BoxShadows.Parse("0 32 64 0 #2E000000, 0 2 21 0 #24000000");
            Merge(dark, Colors(("Ground", "#0E0F11"), ("Mica", "#202020"), ("Layer", "#4D3A3A3A"), ("LayerStroke", "#40000000"),
                ("Acrylic", "#F52C2C2C"), ("FlyStroke", "#17FFFFFF"), ("Footer", "#1C1C1C"), ("Card", "#0DFFFFFF"), ("CardStroke", "#12FFFFFF"),
                ("Fg", "#FFFFFF"), ("Fg2", "#C9FFFFFF"), ("Fg3", "#8AFFFFFF"), ("Sep", "#14FFFFFF"), ("Subtle", "#0FFFFFFF"),
                ("Subtle2", "#17FFFFFF"), ("Ctrl", "#0FFFFFFF"), ("CtrlStroke", "#12FFFFFF"), ("CtrlBottom", "#1FFFFFFF"),
                ("Accent", "#F4979A"), ("OnAccent", "#1B0A0B"), ("AccentText", "#F4979A"), ("Hl", "#3DF4979A"), ("Taskbar", "#1C1C1C"),
                ("Fill", "#0FFFFFFF"), ("Fill2", "#17FFFFFF"), ("Hover", "#0FFFFFFF"), ("Press", "#08FFFFFF")));
            dark["Shadow"] = BoxShadows.Parse("0 8 16 0 #4D000000");
            dark["ShadowLg"] = BoxShadows.Parse("0 32 64 0 #66000000, 0 2 21 0 #4D000000");
            // Segoe UI Variable is Windows 11's; Windows 10 has Segoe UI.
            d["TextFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI, avares://StudyStash/Assets/Fonts#Inter");
            d["DisplayFont"] = new FontFamily("Segoe UI Variable Display, Segoe UI, avares://StudyStash/Assets/Fonts#Inter Display");
            d["SerifFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI, avares://StudyStash/Assets/Fonts#Inter");
            d["RadiusPanel"] = new CornerRadius(8);
        }
        if (kind == SkinKind.Win)
        {
            // Fluent's control border: a hairline all round, darker along the bottom edge.
            foreach (var (theme, stroke, bottom) in new[] { (light, "#12000000", "#2E000000"), (dark, "#12FFFFFF", "#1FFFFFFF") })
                theme["CtrlBorder"] = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse(stroke), 0.94), new GradientStop(Color.Parse(bottom), 0.97) },
                };
        }
        d.ThemeDictionaries[ThemeVariant.Light] = light;
        d.ThemeDictionaries[ThemeVariant.Dark] = dark;
        return d;
    }

    static void Merge(ResourceDictionary into, ResourceDictionary from)
    {
        foreach (var (k, v) in from) into[k] = v;
    }
}
