using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace StudyStash.App.ViewModels;

/// <summary>The syncing card's progress bar: a 0..1 fraction as a Grid column's star width, so the filled part
/// sizes itself over a flexible track the way the design's <c>width:40%</c> does.</summary>
public static class CanvasConverters
{
    public static readonly IValueConverter FillStar = new FuncValueConverter<double?, GridLength>(p => new GridLength(Math.Clamp(p ?? 0, 0, 1), GridUnitType.Star));
    public static readonly IValueConverter RestStar = new FuncValueConverter<double?, GridLength>(p => new GridLength(1 - Math.Clamp(p ?? 0, 0, 1), GridUnitType.Star));

    /// <summary>Windows' InfoBar dot: a plain character, not a Material glyph — "i", "!", a check or a cross.</summary>
    public static readonly IValueConverter InfoBarGlyph = new FuncValueConverter<CanvasTone, string>(t => t switch
    {
        CanvasTone.Ok => "✓",
        CanvasTone.Warn => "!",
        CanvasTone.Error => "✕",
        _ => "i",
    });

    /// <summary>Windows' expander card chevron (design 07): pointing up while the step is open, down otherwise.</summary>
    public static readonly IValueConverter Chevron = new FuncValueConverter<bool, string>(current => current ? "expand_less" : "expand_more");
}
