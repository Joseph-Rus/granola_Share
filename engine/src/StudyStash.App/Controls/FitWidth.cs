using Avalonia;
using Avalonia.Controls;

namespace StudyStash.App.Controls;

/// <summary>
/// Sizes a bubble the way the design's browser does: as wide as its text on one line, or, once the text has to wrap,
/// the whole width it may take. Avalonia would pull a wrapped bubble in to its longest line, so an answer in the
/// recorder's chat came out narrower than the design's and wrapped differently.
/// </summary>
public sealed class FitWidth : Decorator
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child) return default;
        var pad = Padding;
        var inner = availableSize.Deflate(pad);
        child.Measure(new Size(double.PositiveInfinity, inner.Height));
        if (child.DesiredSize.Width > inner.Width)
        {
            child.Measure(inner);
            return new Size(availableSize.Width, child.DesiredSize.Height + pad.Top + pad.Bottom);
        }
        return child.DesiredSize.Inflate(pad);
    }
}
