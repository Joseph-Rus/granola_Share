using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StudyStash.App.Views;

/// <summary>A gradient from nothing to the window's color: the page fading out under a floating bar.</summary>
public static class Fades
{
    /// <summary>Paints the fade from the <paramref name="colorKey"/> token, and again whenever that token changes
    /// (light to dark, another colour theme).</summary>
    public static void Under(Border fade, string colorKey, double solidFrom) =>
        fade.Bind(Border.BackgroundProperty, fade.GetResourceObservable(colorKey, v => v is ISolidColorBrush b ? Gradient(b.Color, solidFrom) : null));

    static LinearGradientBrush Gradient(Color c, double solidFrom) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0), new GradientStop(c, solidFrom) },
    };
}
