using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace StudyStash.App.Controls;

/// <summary>
/// Letter spacing as each system sets it. A Mac spaces SF Pro by its size (Apple's tracking table: a touch looser
/// below 12 pt, tighter above); Avalonia doesn't, so text would run wide. The design's own letter-spacing, where it
/// has some, goes on top: <c>c:Typography.Extra="-0.15"</c>. Where SF isn't installed, Inter's recommended tracking
/// (its "dynamic metrics") stands in. Windows fonts need none.
/// </summary>
public static class Typography
{
    public static readonly AttachedProperty<double> ExtraProperty = AvaloniaProperty.RegisterAttached<TextBlock, double>("Extra", typeof(Typography));

    public static double GetExtra(TextBlock t) => t.GetValue(ExtraProperty);
    public static void SetExtra(TextBlock t, double v) => t.SetValue(ExtraProperty, v);

    // Apple's tracking for SF Pro Text, in points by size (6 to 19 pt). SF Pro Display (20 pt and up) sets its own.
    static readonly double[] SfText = [0.24, 0.23, 0.21, 0.17, 0.12, 0.06, 0, -0.08, -0.15, -0.23, -0.31, -0.43, -0.44, -0.45];

    public static bool SfInstalled { get; } = FontManager.Current.SystemFonts.Any(f => f.Name == "SF Pro Text");

    public static double Tracking(double size, bool sf)
    {
        if (!sf) return size * (-0.0223 + 0.185 * Math.Exp(-0.1745 * size)); // Inter
        if (size >= 20) return 0;
        int i = (int)Math.Round(size) - 6;
        return i < 0 ? SfText[0] : SfText[Math.Min(i, SfText.Length - 1)];
    }

    // A Mac's line of text is as tall as the system font's ascent and descent, each rounded to whole points (as AppKit
    // and the design's browser both do). By size from 6 pt: the text face's, which isn't a formula, and the display
    // face's, which is.
    static readonly byte[] TextLines =
        [7, 8, 10, 11, 12, 13, 15, 16, 17, 18, 18, 20, 21, 22, 23, 24, 26, 27, 28, 29, 30, 32, 33, 34, 35, 37, 38, 39, 40, 41, 43, 44, 45, 46, 47, 49, 50, 51, 52, 53, 54, 55, 56];

    /// <summary>
    /// The height of one line of the Mac's type at a size, where the design leaves it at "normal": SF Pro Text below
    /// the display sizes, SF Pro Display where a view asks for it. Avalonia would give the font's exact height, rounded
    /// up for the whole block, so a small line (11 pt: 13.1) takes 14 points where the design takes 13, and a list of
    /// them runs long. Other faces (the serif notes) keep their own.
    /// </summary>
    public static double LineHeight(double size, bool display)
    {
        if (display) return Math.Round(0.95215 * size) + Math.Round(0.2412 * size);
        int i = (int)Math.Round(size) - 6;
        return i >= 0 && i < TextLines.Length ? TextLines[i] : Math.Round(size * 1.17);
    }

    sealed class Converter(bool sf) : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            (values.Count > 0 && values[0] is double size ? Tracking(size, sf) : 0) + (values.Count > 1 && values[1] is double extra ? extra : 0);
    }

    sealed class LineConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (values.Count < 2 || values[0] is not double size || values[1] is not FontFamily family) return double.NaN;
            string name = family.Name;
            bool display = name.Contains("Display", StringComparison.Ordinal);
            return display || name.Contains("SF Pro", StringComparison.Ordinal) ? LineHeight(size, display) : double.NaN;
        }
    }

    /// <summary>The style that does it, for the Mac look: the tracking, and the line heights.</summary>
    public static Style MacStyle()
    {
        var style = new Style(x => x.OfType<TextBlock>());
        var binding = new MultiBinding { Converter = new Converter(SfInstalled) };
        binding.Bindings.Add(new Binding(nameof(TextBlock.FontSize)) { RelativeSource = new RelativeSource(RelativeSourceMode.Self) });
        binding.Bindings.Add(new Binding { Path = "(c:Typography.Extra)", RelativeSource = new RelativeSource(RelativeSourceMode.Self), TypeResolver = (ns, name) => typeof(Typography) });
        style.Setters.Add(new Setter(TextBlock.LetterSpacingProperty, binding));
        var lines = new MultiBinding { Converter = new LineConverter() };
        lines.Bindings.Add(new Binding(nameof(TextBlock.FontSize)) { RelativeSource = new RelativeSource(RelativeSourceMode.Self) });
        lines.Bindings.Add(new Binding(nameof(TextBlock.FontFamily)) { RelativeSource = new RelativeSource(RelativeSourceMode.Self) });
        style.Setters.Add(new Setter(TextBlock.LineHeightProperty, lines));
        return style;
    }
}
