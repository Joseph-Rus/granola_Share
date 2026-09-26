using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace StudyStash.App;

/// <summary>Which look: the Mac's (SF Pro, Liquid Glass, big round corners) or Windows 11's (Segoe UI, Mica and
/// Acrylic, 4–8 px corners). Both are the same design; each follows its system's conventions.</summary>
public enum SkinKind
{
    Mac,
    Win,
}

/// <summary>How the Mac's glass treats what's behind it: the design's <c>blur(22px) saturate(200%) brightness(1.04)</c>
/// (a blur radius in pixels, then the saturation and brightness as multipliers).</summary>
public sealed record GlassFilter(double Blur, double Saturate, double Brightness);

/// <summary>
/// The design's tokens (Study Stash All Screens: the Mac in Liquid Glass, Windows 11 in Fluent, each light and dark),
/// as resources the views ask for by name: <c>{DynamicResource Fg2}</c>. Fixed colours are the design's CSS values
/// (rgba's alpha to #AARRGGBB); the accent and the surfaces' faint lean come from the colour theme, worked out in OKLCH
/// the way the design's <c>renderVals()</c> does. Where the design stacks several box-shadows on one element, one
/// token holds them all (GlassShadow = the glass's edge highlights + its shadow).
/// </summary>
public static class Skin
{
    public static SkinKind Current { get; set; } = OperatingSystem.IsWindows() ? SkinKind.Win : SkinKind.Mac;

    /// <summary>The colour theme the tokens are made from: Lagoon until the student picks another.</summary>
    public static ColourTheme Theme { get; private set; } = ColourThemes.Default;

    /// <summary>STUDYSTASH_SKIN=mac|win shows the other system's look (for screenshots, or to compare).</summary>
    public static SkinKind FromEnvironment() =>
        Environment.GetEnvironmentVariable("STUDYSTASH_SKIN")?.ToLowerInvariant() switch
        {
            "win" or "windows" => SkinKind.Win,
            "mac" or "macos" => SkinKind.Mac,
            _ => OperatingSystem.IsWindows() ? SkinKind.Win : SkinKind.Mac,
        };

    // The class dots: the design's OKLCH hues (StudyStash.Core.ClassColors), the same in light and dark.
    public static readonly IBrush[] ClassDots = [.. StudyStash.Core.ClassColors.Palette.Select(c => (IBrush)new SolidColorBrush(Color.Parse(StudyStash.Core.ClassColors.Hex(c))))];

    public static IBrush ClassDot(int index) => ClassDots[((index % ClassDots.Length) + ClassDots.Length) % ClassDots.Length];

    /// <summary>Switch colour themes while running: every open view follows at once (its colours are
    /// DynamicResources), no restart.</summary>
    public static void UseTheme(ColourTheme theme)
    {
        Theme = theme;
        if (Application.Current is App app) app.UseSkin(Current);
    }

    /// <summary>A look's tokens in the current colour theme.</summary>
    public static ResourceDictionary Build(SkinKind kind) => Build(kind, Theme);

    /// <summary>A look's tokens in a colour theme: the colours and shadows light and dark (theme dictionaries), the
    /// fonts and corner radii the same in both. Made again on every switch, so it stays cheap.</summary>
    public static ResourceDictionary Build(SkinKind kind, ColourTheme theme)
    {
        var d = new ResourceDictionary();
        ResourceDictionary light = new(), dark = new();
        if (kind == SkinKind.Mac)
        {
            MacTokens(light, theme, dark: false);
            MacTokens(dark, theme, dark: true);
            d["TextFont"] = new FontFamily("SF Pro Text, avares://StudyStash/Assets/Fonts#Inter");
            d["DisplayFont"] = new FontFamily("SF Pro Display, avares://StudyStash/Assets/Fonts#Inter Display");
            // The design reads notes in New York. Where it isn't installed, Charter (on every Mac) reads as well.
            d["SerifFont"] = new FontFamily("New York, Charter, Georgia, avares://StudyStash/Assets/Fonts#Inter");
            d["RadiusPanel"] = new CornerRadius(14);
            d["RadiusControl"] = new CornerRadius(6);
        }
        else
        {
            WinTokens(light, theme, dark: false);
            WinTokens(dark, theme, dark: true);
            // Segoe UI Variable is Windows 11's; Windows 10 has Segoe UI.
            d["TextFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI, avares://StudyStash/Assets/Fonts#Inter");
            d["DisplayFont"] = new FontFamily("Segoe UI Variable Display, Segoe UI, avares://StudyStash/Assets/Fonts#Inter Display");
            d["SerifFont"] = new FontFamily("Segoe UI Variable Text, Segoe UI, avares://StudyStash/Assets/Fonts#Inter");
            d["RadiusPanel"] = new CornerRadius(8);
            d["RadiusControl"] = new CornerRadius(4);
        }
        d.ThemeDictionaries[ThemeVariant.Light] = light;
        d.ThemeDictionaries[ThemeVariant.Dark] = dark;
        return d;
    }

    /// <summary>The Mac in Liquid Glass: the design's macL / macD.</summary>
    static void MacTokens(ResourceDictionary r, ColourTheme t, bool dark)
    {
        void B(string key, Color c) => r[key] = new SolidColorBrush(c);
        double L = t.L, c = t.C;
        bool lt = t.IsLight;
        Color onAccent = lt ? Ink : Colors.White, sep, accentTint;
        BoxShadow[] edge, edgeSoft, edgeTint, gshadow, sideShadow, ctlShadow;
        if (!dark)
        {
            B("Glass", White(0.42));
            B("Win", t.Neutral(0.985, 1));
            B("Raised", t.Neutral(0.998, 0.5));
            B("Group", t.Neutral(0.4, 3, 0.05));
            B("Fg", Hex("#1D1D1F"));
            B("Fg2", Black(0.6));
            B("Fg3", Black(0.34));
            B("Sep", sep = Black(0.08));
            B("Fill", White(0.45));
            B("Fill2", Black(0.06));
            B("Mini", Colors.White);
            B("Accent", t.O(L, c));
            B("AccentText", lt ? t.O(0.45, c * 0.9) : t.O(Math.Min(L - 0.1, 0.48), c));
            B("Tint", lt ? t.O(L, c, 0.92) : t.O(L, c, 0.88));
            B("AccentTint", accentTint = t.O(L, c, 0.2));
            B("Hl", Oklch.ToColor(0.88, 0.16, t.Hl, 0.55));
            B("Warn", Oklch.ToColor(0.68, 0.15, 65));
            B("Ok", Oklch.ToColor(0.62, 0.14, 150));
            B("Hover", Black(0.04));
            B("Press", Black(0.08));
            // Not in the design: a floating window's glass when there's no blur behind it, solid enough to read.
            B("GlassSolid", t.Neutral(0.97, 2, 0.94));
            r["GlassFilter"] = new GlassFilter(22, 2.0, 1.04);
            edge = [Inset(0, 1, 0, 0, White(0.85)), Inset(0, 0, 0, 0.5, White(0.6)), Inset(0, -1, 1, 0, White(0.25))];
            edgeSoft = [Inset(0, 1, 0, 0, White(0.6)), Inset(0, 0, 0, 0.5, Black(0.04))];
            edgeTint = [Inset(0, 1, 0, 0, White(0.45)), Inset(0, 0, 0, 0.5, White(0.25)), Outer(0, 2, 8, 0, t.O(L, c, 0.3))];
            gshadow = [Outer(0, 18, 50, 0, Black(0.16)), Outer(0, 2, 8, 0, Black(0.06))];
            sideShadow = [Outer(0, 4, 16, 0, Black(0.08))];
            ctlShadow = [Outer(0, 4, 12, 0, Black(0.1))];
        }
        else
        {
            B("Glass", Rgba(40, 40, 44, 0.46));
            B("Win", t.Neutral(0.21, 1.5));
            B("Raised", t.Neutral(0.36, 1.5));
            B("Group", t.Neutral(0.9, 2, 0.06));
            B("Fg", Hex("#F5F5F7"));
            B("Fg2", White(0.62));
            B("Fg3", White(0.34));
            B("Sep", sep = White(0.09));
            B("Fill", White(0.08));
            B("Fill2", White(0.12));
            B("Mini", Hex("#2C2C2E"));
            B("Accent", lt ? t.O(L, c) : t.O(Math.Min(L + 0.08, 0.72), c));
            B("AccentText", lt ? t.O(L, c) : t.O(0.8, c * 0.8));
            B("Tint", lt ? t.O(L, c, 0.9) : t.O(Math.Min(L + 0.04, 0.68), c, 0.85));
            B("AccentTint", accentTint = t.O(0.7, c, 0.26));
            B("Hl", Oklch.ToColor(0.75, 0.15, t.Hl, 0.35));
            B("Warn", Oklch.ToColor(0.8, 0.14, 75));
            B("Ok", Oklch.ToColor(0.74, 0.15, 150));
            B("Hover", White(0.05));
            B("Press", White(0.1));
            B("GlassSolid", t.Neutral(0.26, 1.5, 0.94));
            r["GlassFilter"] = new GlassFilter(22, 1.8, 1.0);
            edge = [Inset(0, 1, 0, 0, White(0.2)), Inset(0, 0, 0, 0.5, White(0.16)), Inset(0, -1, 1, 0, White(0.05))];
            edgeSoft = [Inset(0, 1, 0, 0, White(0.1)), Inset(0, 0, 0, 0.5, White(0.06))];
            edgeTint = [Inset(0, 1, 0, 0, White(0.3)), Inset(0, 0, 0, 0.5, White(0.18)), Outer(0, 2, 10, 0, t.O(0.7, c, 0.3))];
            gshadow = [Outer(0, 18, 50, 0, Black(0.5)), Outer(0, 2, 8, 0, Black(0.3))];
            sideShadow = [Outer(0, 4, 16, 0, Black(0.3))];
            ctlShadow = [Outer(0, 4, 12, 0, Black(0.35))];
        }
        B("OnAccent", onAccent);
        B("Good", Good);
        r["Edge"] = Shadows(edge);
        r["EdgeSoft"] = Shadows(edgeSoft);
        r["EdgeTint"] = Shadows(edgeTint);
        r["GShadow"] = Shadows(gshadow);
        r["SideShadow"] = Shadows(sideShadow);
        r["CtlShadow"] = Shadows(ctlShadow);
        r["GlassShadow"] = Shadows([.. edge, .. gshadow]);
        r["SideGlassShadow"] = Shadows([.. edge, .. sideShadow]);
        r["CtlGlassShadow"] = Shadows([.. edge, .. ctlShadow]);
        r["RaisedShadow"] = Shadows([.. edge, Outer(0, 0, 0, 0.5, sep), Outer(0, 1, 2, 0, Black(0.08))]);
        r["SegShadow"] = Shadows([.. edge, Outer(0, 1, 3, 0, Black(0.12))]);
        r["WindowShadow"] = Shadows([.. gshadow, Outer(0, 0, 0, 0.5, sep)]);
        r["Ring3"] = Shadows([Outer(0, 0, 0, 3, accentTint)]);
        r["Ring4"] = Shadows([Outer(0, 0, 0, 4, accentTint)]);
        r["OnAccentRing"] = Shadows([Outer(0, 0, 0, 1.5, onAccent)]);

        // Old names, until every view uses the design's.
        r["Mat"] = r["GlassSolid"];
        r["Side"] = r["Page"] = r["Nav"] = r["Win"];
        r["Control"] = r["Raised"];
        r["Shadow"] = r["GlassShadow"];
        r["ShadowSmall"] = Shadows([Outer(0, 1, 2, 0, Black(dark ? 0.35 : 0.14))]);
        B("Ground", dark ? Hex("#161616") : Hex("#E4E2DF"));
    }

    /// <summary>Windows 11 in Fluent: the design's winL / winD.</summary>
    static void WinTokens(ResourceDictionary r, ColourTheme t, bool dark)
    {
        void B(string key, Color c) => r[key] = new SolidColorBrush(c);
        double L = t.L, c = t.C;
        bool lt = t.IsLight;
        Color accentText, stroke, bottom;
        if (!dark)
        {
            B("Ground", Hex("#DADCE0"));
            B("Mica", t.Neutral(0.96, 1));
            B("Layer", White(0.55));
            B("LayerStroke", Black(0.06));
            B("Acrylic", t.Neutral(0.975, 1, 0.95));
            B("FlyStroke", Black(0.1));
            B("Footer", t.Neutral(0.94, 1));
            B("Card", White(0.75));
            B("CardStroke", Black(0.06));
            B("Fg", Black(0.9));
            B("Fg2", Black(0.62));
            B("Fg3", Black(0.45));
            B("Sep", Black(0.08));
            B("Subtle", Black(0.04));
            B("Subtle2", Black(0.06));
            B("Ctrl", White(0.8));
            B("CtrlStroke", stroke = Black(0.07));
            B("CtrlBottom", bottom = Black(0.18));
            B("Accent", lt ? t.O(L, c) : t.O(Math.Max(L - 0.06, 0.42), c));
            B("AccentText", accentText = lt ? t.O(0.42, c * 0.9) : t.O(Math.Max(L - 0.12, 0.4), c));
            B("OnAccent", lt ? Ink : Colors.White);
            B("Hl", Oklch.ToColor(0.88, 0.16, t.Hl, 0.5));
            B("Taskbar", t.Neutral(0.94, 1));
            r["Shadow"] = Shadows([Outer(0, 8, 16, 0, Black(0.14))]);
            r["ShadowLg"] = Shadows([Outer(0, 32, 64, 0, Black(0.18)), Outer(0, 2, 21, 0, Black(0.14))]);
            B("Mini", Colors.White);
            B("Ok", Hex("#0F7B0F"));
            B("Warn", Hex("#9D5D00"));
            B("IbInfo", Hex("#F6F6F6"));
            B("IbOk", Hex("#DFF6DD"));
            B("IbWarn", Hex("#FFF4CE"));
            B("IbErr", Hex("#FDE7E9"));
            B("IcOk", Hex("#0F7B0F"));
            B("IcWarn", Hex("#9D5D00"));
            B("IcErr", Hex("#C42B1C"));
            B("IbGlyph", Colors.White);
            B("Hover", Black(0.04));
            B("Press", Black(0.02));
            // Old names, until every view uses the design's.
            B("Fill", Black(0.04));
            B("Fill2", Black(0.06));
            B("Page", Hex("#F9F9F9"));
            B("Nav", Hex("#F3F3F3"));
            B("Control", Hex("#FDFDFD"));
        }
        else
        {
            B("Ground", Hex("#0E0F11"));
            B("Mica", t.Neutral(0.235, 1.5));
            B("Layer", Rgba(58, 58, 58, 0.3));
            B("LayerStroke", Black(0.25));
            B("Acrylic", t.Neutral(0.28, 1.5, 0.96));
            B("FlyStroke", White(0.09));
            B("Footer", t.Neutral(0.2, 1.5));
            B("Card", White(0.05));
            B("CardStroke", White(0.07));
            B("Fg", Colors.White);
            B("Fg2", White(0.79));
            B("Fg3", White(0.54));
            B("Sep", White(0.08));
            B("Subtle", White(0.06));
            B("Subtle2", White(0.09));
            B("Ctrl", White(0.06));
            B("CtrlStroke", stroke = White(0.07));
            B("CtrlBottom", bottom = White(0.12));
            B("Accent", lt ? t.O(L, c) : t.O(0.8, c * 0.6));
            B("AccentText", accentText = lt ? t.O(L, c) : t.O(0.8, c * 0.6));
            B("OnAccent", Ink);
            B("Hl", Oklch.ToColor(0.75, 0.15, t.Hl, 0.3));
            B("Taskbar", t.Neutral(0.2, 1.5));
            r["Shadow"] = Shadows([Outer(0, 8, 16, 0, Black(0.3))]);
            r["ShadowLg"] = Shadows([Outer(0, 32, 64, 0, Black(0.4)), Outer(0, 2, 21, 0, Black(0.3))]);
            B("Mini", Hex("#2B2B2B"));
            B("Ok", Hex("#6CCB5F"));
            B("Warn", Hex("#FCE100"));
            B("IbInfo", Hex("#2B2B2B"));
            B("IbOk", Hex("#393D1B"));
            B("IbWarn", Hex("#433519"));
            B("IbErr", Hex("#442726"));
            B("IcOk", Hex("#6CCB5F"));
            B("IcWarn", Hex("#FCE100"));
            B("IcErr", Hex("#FF99A4"));
            B("IbGlyph", Colors.Black);
            B("Hover", White(0.06));
            B("Press", White(0.03));
            // Old names, until every view uses the design's.
            B("Fill", White(0.06));
            B("Fill2", White(0.09));
            B("Page", Hex("#272727"));
            B("Nav", Hex("#202020"));
            B("Control", Hex("#2D2D2D"));
        }
        B("IcInfo", accentText);
        B("Good", Good);
        // Fluent's control border: a hairline all round, darker along the bottom edge.
        r["CtrlBorder"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(stroke, 0.94), new GradientStop(bottom, 0.97) },
        };
    }

    static readonly string[] MacBrushes =
    [
        "Glass", "GlassSolid", "Win", "Raised", "Group", "Fg", "Fg2", "Fg3", "Sep", "Fill", "Fill2", "Mini", "Accent", "AccentText",
        "Tint", "OnAccent", "AccentTint", "Hl", "Warn", "Ok", "Good", "Hover", "Press",
    ];
    static readonly string[] MacShadows =
    [
        "Edge", "EdgeSoft", "EdgeTint", "GShadow", "SideShadow", "CtlShadow", "GlassShadow", "SideGlassShadow", "CtlGlassShadow",
        "RaisedShadow", "SegShadow", "WindowShadow", "Ring3", "Ring4", "OnAccentRing",
    ];
    static readonly string[] WinBrushes =
    [
        "Ground", "Mica", "Layer", "LayerStroke", "Acrylic", "FlyStroke", "Footer", "Card", "CardStroke", "Fg", "Fg2", "Fg3", "Sep",
        "Subtle", "Subtle2", "Ctrl", "CtrlStroke", "CtrlBottom", "CtrlBorder", "Accent", "AccentText", "OnAccent", "Hl", "Taskbar",
        "Mini", "Ok", "Warn", "IbInfo", "IbOk", "IbWarn", "IbErr", "IcInfo", "IcOk", "IcWarn", "IcErr", "IbGlyph", "Good", "Hover", "Press",
    ];
    static readonly string[] WinShadows = ["Shadow", "ShadowLg"];

    /// <summary>Every token the design gives a look, and what it is (a brush, shadows, the glass's filter, a font, a
    /// radius): what <see cref="Build(SkinKind, ColourTheme)"/> must make, light and dark.</summary>
    public static IEnumerable<(string Key, Type Type)> Keys(SkinKind kind)
    {
        bool mac = kind == SkinKind.Mac;
        foreach (var k in mac ? MacBrushes : WinBrushes) yield return (k, typeof(IBrush));
        foreach (var k in mac ? MacShadows : WinShadows) yield return (k, typeof(BoxShadows));
        if (mac) yield return ("GlassFilter", typeof(GlassFilter));
        foreach (var k in new[] { "TextFont", "DisplayFont", "SerifFont" }) yield return (k, typeof(FontFamily));
        foreach (var k in new[] { "RadiusPanel", "RadiusControl" }) yield return (k, typeof(CornerRadius));
    }

    /// <summary>"Library connected" green, oklch(0.7 0.15 150): the same in both looks, light and dark.</summary>
    static readonly Color Good = Oklch.ToColor(0.7, 0.15, 150);
    /// <summary>The dark ink text takes on a light accent (Chalkboard, Highlighter).</summary>
    static readonly Color Ink = Color.FromRgb(0x16, 0x17, 0x1A);

    static Color Hex(string hex) => Color.Parse(hex);
    static Color White(double a) => Rgba(255, 255, 255, a);
    static Color Black(double a) => Rgba(0, 0, 0, a);
    static Color Rgba(byte r, byte g, byte b, double a) => Color.FromArgb(Oklch.Byte(a), r, g, b);

    static BoxShadow Outer(double x, double y, double blur, double spread, Color c) =>
        new() { OffsetX = x, OffsetY = y, Blur = blur, Spread = spread, Color = c };

    static BoxShadow Inset(double x, double y, double blur, double spread, Color c) =>
        new() { OffsetX = x, OffsetY = y, Blur = blur, Spread = spread, Color = c, IsInset = true };

    static BoxShadows Shadows(BoxShadow[] s) => s.Length == 1 ? new BoxShadows(s[0]) : new BoxShadows(s[0], s[1..]);
}
