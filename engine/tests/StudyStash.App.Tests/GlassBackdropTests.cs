using Avalonia;
using SkiaSharp;
using StudyStash.App.Controls;

namespace StudyStash.App.Tests;

/// <summary>The mask an <c>NSVisualEffectView</c> reads to show the OS blur only through a window's glass: a pure
/// function (rects and a size in, a PNG out), so it's tested without a window at all.</summary>
public class GlassBackdropTests
{
    static byte AlphaAt(byte[] png, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(png);
        return bitmap.GetPixel(x, y).Alpha;
    }

    [Fact]
    public void A_shapes_corner_is_transparent_and_its_centre_opaque()
    {
        var shapes = new[] { (new Rect(10, 10, 100, 100), new CornerRadius(24)) };
        var png = GlassBackdrop.BuildMask(shapes, new PixelSize(150, 150));

        Assert.Equal(0, AlphaAt(png, 11, 11)); // the round corner, just inside the rect
        Assert.Equal(255, AlphaAt(png, 60, 60)); // well inside the shape
    }

    [Fact]
    public void Outside_every_shape_is_transparent()
    {
        var shapes = new[] { (new Rect(20, 20, 40, 40), new CornerRadius(8)) };
        var png = GlassBackdrop.BuildMask(shapes, new PixelSize(100, 100));

        Assert.Equal(0, AlphaAt(png, 5, 5));
        Assert.Equal(0, AlphaAt(png, 90, 90));
    }

    [Fact]
    public void No_shapes_makes_a_wholly_transparent_mask()
    {
        var png = GlassBackdrop.BuildMask([], new PixelSize(40, 40));

        Assert.Equal(0, AlphaAt(png, 20, 20));
    }
}
