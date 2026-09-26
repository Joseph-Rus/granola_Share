using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace StudyStash.App;

/// <summary>OKLCH, the colour space the design picks its colours in, to the sRGB a screen shows.</summary>
public static class Oklch
{
    /// <summary>
    /// <c>oklch(l c h / alpha)</c> as a browser draws it: each channel clipped to sRGB (no gamut mapping, the same as
    /// Chrome), then rounded to a byte. The matrices are the ones <see cref="StudyStash.Core.ClassColors.Hex"/> uses.
    /// </summary>
    public static Color ToColor(double l, double c, double h, double alpha = 1)
    {
        double hr = h * Math.PI / 180, a = c * Math.Cos(hr), b = c * Math.Sin(hr);
        double l_ = l + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = l - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = l - 0.0894841775 * a - 1.2914855480 * b;
        double lc = l_ * l_ * l_, mc = m_ * m_ * m_, sc = s_ * s_ * s_;
        double r = 4.0767416621 * lc - 3.3077115913 * mc + 0.2309699292 * sc;
        double g = -1.2684380046 * lc + 2.6097574011 * mc - 0.3413193965 * sc;
        double bl = -0.0041960863 * lc - 0.7034186147 * mc + 1.7076147010 * sc;
        return Color.FromArgb(Byte(alpha), Byte(Gamma(r)), Byte(Gamma(g)), Byte(Gamma(bl)));
    }

    static double Gamma(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
    }

    /// <summary>0..1 to 0..255, halves rounded up like CSS.</summary>
    internal static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
}

/// <summary>
/// A colour theme: the accent's hue, chroma and lightness (<see cref="H"/>, <see cref="C"/>, <see cref="L"/>), the
/// faint hue the neutral surfaces lean to (<see cref="Nh"/>, <see cref="Nc"/>), the design's wallpaper hues
/// (<see cref="Wall"/>) and the highlighter's hue (<see cref="Hl"/>). Every accent token is worked out from these,
/// the way the design's <c>renderVals()</c> does.
/// </summary>
public sealed record ColourTheme(string Name, double H, double C, double L, double Nh, double Nc, double[] Wall, double Hl)
{
    /// <summary>A light accent (Chalkboard, Highlighter): text on it is dark ink, and dark mode keeps it as it is.</summary>
    public bool IsLight => L >= 0.75;

    /// <summary>The design's <c>o(l, c, a)</c>: the accent's hue at another lightness and chroma.</summary>
    public Color O(double l, double c, double alpha = 1) => Oklch.ToColor(l, c, H, alpha);

    /// <summary>The design's <c>ok(l, nc·k, nh, a)</c>: a surface with the theme's faint lean.</summary>
    public Color Neutral(double l, double chroma, double alpha = 1) => Oklch.ToColor(l, Nc * chroma, Nh, alpha);
}

/// <summary>The ten colour themes the student can pick from, in the picker's order. Lagoon (teal) is the default.</summary>
public static class ColourThemes
{
    public static readonly IReadOnlyList<ColourTheme> All =
    [
        new("Lagoon", 195, 0.12, 0.58, 210, 0.012, [195, 160, 240], 95),
        new("Library", 160, 0.10, 0.50, 80, 0.014, [150, 70, 40], 95),
        new("Blueprint", 262, 0.19, 0.55, 250, 0.014, [255, 220, 290], 200),
        new("Marmalade", 50, 0.17, 0.64, 75, 0.018, [60, 25, 95], 95),
        new("Plum", 330, 0.14, 0.52, 320, 0.012, [330, 290, 20], 350),
        new("Chalkboard", 95, 0.14, 0.84, 180, 0.02, [180, 150, 220], 95),
        new("Highlighter", 125, 0.21, 0.86, 260, 0.004, [125, 300, 200], 125),
        new("Terracotta", 35, 0.13, 0.58, 60, 0.016, [35, 80, 300], 85),
        new("Graphite", 260, 0.01, 0.42, 260, 0.004, [250, 260, 240], 95),
        new("Original red", 22, 0.19, 0.6, 30, 0.003, [250, 25, 160], 22),
    ];

    public static ColourTheme Default => All[0];

    /// <summary>The theme with this name (as app.json keeps it), or Lagoon when there's none by that name.</summary>
    public static ColourTheme Find(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Default;
}

/// <summary>One colour theme in the picker: its name, a swatch of its accent, and whether it's the one in use.</summary>
public sealed partial class ThemeSwatch : ObservableObject
{
    public ThemeSwatch(ColourTheme theme)
    {
        Name = theme.Name;
        Swatch = new SolidColorBrush(theme.O(theme.L, theme.C));
    }

    public string Name { get; }
    public IBrush Swatch { get; }
    [ObservableProperty] public partial bool Chosen { get; set; }
}
