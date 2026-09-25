using Avalonia.Controls;
using Avalonia.Media;

namespace StudyStash.App.Views;

/// <summary>What every Study Stash window shares.</summary>
public static class Look
{
    /// <summary>Grayscale antialiasing: text as macOS draws it now (and as the design does), not the older, heavier
    /// subpixel smoothing. Call after the skin is chosen.</summary>
    public static void Apply(TopLevel window)
    {
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.Antialias);
        // A Mac doesn't hint type (hinting also snaps letters to whole pixels, so words run wide); Windows does.
        TextOptions.SetTextHintingMode(window, Skin.Current == SkinKind.Mac ? TextHintingMode.None : TextHintingMode.Light);
        RenderOptions.SetBitmapInterpolationMode(window, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }
}
