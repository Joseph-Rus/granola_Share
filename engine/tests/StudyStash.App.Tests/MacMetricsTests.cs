using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using StudyStash.App.Controls;

namespace StudyStash.App.Tests;

/// <summary>Where the Mac look measures things the way the design's browser does: a shadow's blur spreads as far as
/// CSS spreads it, and a line of SF Pro is as tall as a Mac makes it.</summary>
public class MacMetricsTests
{
    static void Mac()
    {
        Skin.UseTheme(ColourThemes.Default);
        ((App)Application.Current!).UseSkin(SkinKind.Mac);
    }

    // The normal distribution's tail, from erf (Abramowitz and Stegun 7.1.26, good to 1.5e-7).
    static double Tail(double z)
    {
        double x = Math.Abs(z) / Math.Sqrt(2), t = 1 / (1 + 0.3275911 * x);
        double erf = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return z >= 0 ? (1 - erf) / 2 : (1 + erf) / 2;
    }

    /// <summary>A CSS shadow of blur 20 fades out as a Gaussian of σ 10 past the box's edge; a black one drawn with
    /// <see cref="Skin.Blur"/> darkens a white ground by that much at each distance.</summary>
    [AvaloniaFact]
    public void A_design_shadow_spreads_as_far_as_in_CSS()
    {
        Mac();
        var box = new Border
        {
            Width = 100, Height = 100, Margin = new Thickness(100), Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = Skin.Blur(20), Color = Colors.Black }),
        };
        var frame = GlassTests.Draw(new Panel { Background = Brushes.White, Children = { box } }, 400, 300);

        foreach (int d in new[] { 0, 4, 9, 14, 19 })
        {
            double expected = Tail((d + 0.5) / 10);
            double got = 1 - GlassTests.Pixel(frame, 200 + d, 150).R / 255.0;
            Assert.True(Math.Abs(got - expected) < 0.03, $"{d + 0.5} px past the edge the shadow is {got:F3}; CSS draws {expected:F3}");
        }
        Assert.Equal(0.0, Skin.Blur(0));
        Assert.Equal(0.0, Skin.Blur(1));
    }

    /// <summary>A chat bubble is as wide as its text on one line, and once the text wraps it takes all the width it
    /// may, as in the design's browser (Avalonia alone would shrink it to its longest line).</summary>
    [AvaloniaFact]
    public void A_bubble_that_wraps_takes_its_whole_width()
    {
        Mac();
        Border Bubble(string text) => new()
        {
            MaxWidth = 200, Padding = new Thickness(14, 8), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new FitWidth { Child = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap } },
        };
        var shortOne = Bubble("Big-O?");
        var longOne = Bubble("Recursion traces and call-stack diagrams, like last week's. Big-O proofs won't be on it.");
        var window = new Window { Width = 300, Height = 300, Content = new StackPanel { Children = { shortOne, longOne } } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(shortOne.Bounds.Width, 30, 100);
            Assert.Equal(200, longOne.Bounds.Width, 3);
            Assert.True(longOne.Bounds.Height > 40, "the long answer wraps");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The heights a Mac (and the design's browser) gives one line of SF Pro Text and SF Pro Display.</summary>
    [Theory]
    [InlineData(11, false, 13)]
    [InlineData(12, false, 15)]
    [InlineData(13, false, 16)]
    [InlineData(15, false, 18)]
    [InlineData(17, false, 20)]
    [InlineData(17, true, 20)]
    [InlineData(26, true, 31)]
    [InlineData(34, true, 40)]
    public void A_line_of_SF_is_as_tall_as_on_a_Mac(double size, bool display, double height) =>
        Assert.Equal(height, Typography.LineHeight(size, display));

    /// <summary>In the Mac look a line of the system text takes the Mac's height, a line height a view sets itself
    /// stays, and the serif notes keep their own.</summary>
    [AvaloniaFact]
    public void Mac_text_takes_the_Macs_line_height()
    {
        Mac();
        TextBlock Line(string fontKey, double size, double? lineHeight = null)
        {
            var t = new TextBlock { Text = "Recent", FontSize = size, HorizontalAlignment = HorizontalAlignment.Left };
            t.Bind(TextBlock.FontFamilyProperty, t.GetResourceObservable(fontKey));
            if (lineHeight is double h) t.LineHeight = h;
            return t;
        }
        var small = Line("TextFont", 11);
        var title = Line("DisplayFont", 34);
        var own = Line("TextFont", 13, 18.85);
        var serif = Line("SerifFont", 16);
        var window = new Window { Width = 300, Height = 300, Content = new StackPanel { Children = { small, title, own, serif } } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(13, small.Bounds.Height, 3);
            Assert.Equal(40, title.Bounds.Height, 3);
            Assert.Equal(18.85, own.LineHeight);
            Assert.Equal(19, own.Bounds.Height, 3); // laid out to whole points
            Assert.True(double.IsNaN(serif.LineHeight), $"the serif line was given {serif.LineHeight}");
        }
        finally
        {
            window.Close();
        }
    }
}
