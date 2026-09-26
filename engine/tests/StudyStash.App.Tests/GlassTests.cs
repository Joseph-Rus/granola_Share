using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using StudyStash.App.Controls;
using StudyStash.App.Windows;

namespace StudyStash.App.Tests;

/// <summary>The Mac's glass: in the screenshots it blurs, saturates and brightens what's behind it the way the design
/// does; in the app it never blurs, and a floating window's glass is solid.</summary>
public class GlassTests
{
    static void Mac()
    {
        Skin.UseTheme(ColourThemes.Default);
        ((App)Application.Current!).UseSkin(SkinKind.Mac);
    }

    /// <summary>Draws <paramref name="content"/> in a plain light window and hands back the picture.</summary>
    internal static WriteableBitmap Draw(Control content, double width, double height)
    {
        var window = new Window { Width = width, Height = height, RequestedThemeVariant = ThemeVariant.Light, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("nothing rendered");
        window.Close();
        return frame;
    }

    internal static Color Pixel(WriteableBitmap bitmap, int x, int y)
    {
        using var fb = bitmap.Lock();
        int v = Marshal.ReadInt32(fb.Address, y * fb.RowBytes + x * 4);
        byte c0 = (byte)v, c1 = (byte)(v >> 8), c2 = (byte)(v >> 16), a = (byte)(v >> 24);
        return fb.Format == PixelFormat.Rgba8888 ? Color.FromArgb(a, c0, c1, c2) : Color.FromArgb(a, c2, c1, c0);
    }

    static double Spread(IEnumerable<double> values)
    {
        var v = values.ToList();
        double mean = v.Average();
        return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Count);
    }

    static Glass Pane(double left, double top, double width, double height) => new()
    {
        Width = width, Height = height, CornerRadius = new CornerRadius(24), Margin = new Thickness(left, top, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
    };

    /// <summary>Black and blue stripes under two panes: through the one that blurs they melt into one colour; through
    /// the other they still show.</summary>
    [AvaloniaFact]
    public void Glass_blurs_what_is_behind_it_in_shots()
    {
        Mac();
        var stripes = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 50; i++)
            stripes.Children.Add(new Border { Width = 10, Height = 320, Background = i % 2 == 0 ? Brushes.Black : new SolidColorBrush(Color.Parse("#6FA8F0")) });
        var blurred = Pane(40, 60, 180, 200);
        var clear = Pane(280, 60, 180, 200);
        Glass.SetBlurBackdrop(blurred, true);
        var frame = Draw(new Panel { Children = { stripes, blurred, clear } }, 500, 320);

        Assert.True(Glass.GetBlurBackdrop(blurred));
        Assert.False(Glass.GetBlurBackdrop(clear));
        double Luma(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
        double through = Spread(Enumerable.Range(80, 100).Select(x => Luma(Pixel(frame, x, 160))));
        double beside = Spread(Enumerable.Range(320, 100).Select(x => Luma(Pixel(frame, x, 160))));
        Assert.True(through * 5 < beside, $"stripes through the blurring glass vary by {through:F1}, through the plain one by {beside:F1}");
    }

    /// <summary>Over one flat colour a blur changes nothing, so what shows is the design's saturate(200%)
    /// brightness(1.04), worked out the way the CSS filter spec does.</summary>
    [AvaloniaFact]
    public void The_backdrop_is_saturated_and_brightened_like_the_design()
    {
        Mac();
        var ground = Color.Parse("#7F95B0");
        var pane = Pane(100, 100, 300, 200);
        pane.Background = Brushes.Transparent;
        pane.BoxShadow = default;
        pane.Filter = new GlassFilter(22, 2.0, 1.04);
        Glass.SetBlurBackdrop(pane, true);
        var frame = Draw(new Panel { Background = new SolidColorBrush(ground), Children = { pane } }, 500, 400);

        static byte Css(double r, double g, double b, double wr, double wg, double wb) =>
            (byte)Math.Round(Math.Clamp((wr * r + wg * g + wb * b) * 1.04, 0, 255));
        double s = 2.0, R = ground.R, G = ground.G, B = ground.B;
        var expected = Color.FromRgb(
            Css(R, G, B, 0.213 + 0.787 * s, 0.715 - 0.715 * s, 0.072 - 0.072 * s),
            Css(R, G, B, 0.213 - 0.213 * s, 0.715 + 0.285 * s, 0.072 - 0.072 * s),
            Css(R, G, B, 0.213 - 0.213 * s, 0.715 - 0.715 * s, 0.072 + 0.928 * s));
        var got = Pixel(frame, 250, 200);
        Assert.True(Math.Abs(got.R - expected.R) <= 2 && Math.Abs(got.G - expected.G) <= 2 && Math.Abs(got.B - expected.B) <= 2,
            $"the glass shows {got}; the design's filter makes {expected}");
        Assert.Equal(ground, Pixel(frame, 20, 20));
    }

    static Color Fill(Glass glass) => ((ISolidColorBrush)glass.Background!).Color;

    static Color Token(string key, ThemeVariant variant) =>
        Skin.Build(Skin.Current).TryGetResource(key, variant, out var v) ? ((ISolidColorBrush)v!).Color : default;

    /// <summary>The app's floating windows (the dropdown, the recorder, the quick panel) are clear, so on the Mac their
    /// glass is the solid kind, and nothing in the app blurs its backdrop; an ordinary window keeps the see-through glass.</summary>
    [AvaloniaFact]
    public void A_floating_window_on_the_Mac_has_solid_glass()
    {
        Mac();
        var floating = Pane(0, 0, 200, 100);
        var ordinary = Pane(0, 0, 200, 100);
        var popup = new Floating { Content = floating, RequestedThemeVariant = ThemeVariant.Light };
        var window = new Window { Content = ordinary, RequestedThemeVariant = ThemeVariant.Light };
        Views.Look.Apply(window);
        popup.Show();
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("solidglass", popup.Classes);
            Assert.DoesNotContain("solidglass", window.Classes);
            Assert.Equal(Token("GlassSolid", ThemeVariant.Light), Fill(floating));
            Assert.Equal(Token("Glass", ThemeVariant.Light), Fill(ordinary));
            Assert.False(Glass.GetBlurBackdrop(floating));
            Assert.Equal(22, floating.Filter?.Blur);
            Assert.True(floating.BoxShadow.Count > 0, "the glass has its edge highlights and shadow");
        }
        finally
        {
            popup.Close();
            window.Close();
        }
    }

    /// <summary>Windows draws no glass: its floating windows stay as they are.</summary>
    [AvaloniaFact]
    public void Windows_floating_windows_stay_as_they_are()
    {
        Skin.UseTheme(ColourThemes.Default);
        var app = (App)Application.Current!;
        app.UseSkin(SkinKind.Win);
        try
        {
            var popup = new Floating { Content = new Border() };
            Assert.DoesNotContain("solidglass", popup.Classes);
            popup.Close();
        }
        finally
        {
            app.UseSkin(SkinKind.Mac);
        }
    }
}
