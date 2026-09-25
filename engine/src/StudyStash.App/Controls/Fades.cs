using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StudyStash.App.Views;

/// <summary>A gradient from nothing to the window's color: the page fading out under a floating bar.</summary>
public static class Fades
{
    public static void Under(Border fade, string colorKey, double solidFrom)
    {
        void Paint()
        {
            if (!fade.TryFindResource(colorKey, fade.ActualThemeVariant, out var v) || v is not ISolidColorBrush b) return;
            var c = b.Color;
            fade.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 0), new GradientStop(c, solidFrom) },
            };
        }
        fade.AttachedToVisualTree += (_, _) => Paint();
        fade.ActualThemeVariantChanged += (_, _) => Paint();
    }
}
