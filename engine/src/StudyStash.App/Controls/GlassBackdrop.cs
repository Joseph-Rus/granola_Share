using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace StudyStash.App.Controls;

/// <summary>
/// The Mac's real desktop blur behind a floating window's glass (the dropdown, the recorder, the quick panel): the
/// system's own <c>NSVisualEffectView</c>, masked to the shapes of the <see cref="Glass"/> panes it holds, so the
/// blur only shows through the glass, not the transparent shadow room round it. When any step fails — an older
/// macOS, no OS blur, an interop call that isn't there — the window falls back to <c>solidglass</c> (the design's
/// glass colour painted solid), which always stays readable. <c>STUDYSTASH_GLASS=solid</c> skips the OS blur
/// entirely, to compare the two by eye.
/// </summary>
public static class GlassBackdrop
{
    static bool Enabled => OperatingSystem.IsMacOS() && Skin.Current == SkinKind.Mac &&
        Environment.GetEnvironmentVariable("STUDYSTASH_GLASS") != "solid";

    sealed class State
    {
        public List<(Rect Rect, CornerRadius Radius)>? Rects;
        public DispatcherTimer? Debounce;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, State> States = new();

    /// <summary>Try the OS blur for a floating window's glass; fall back to solid glass until (or unless) it works.
    /// Safe to call for every window: it does nothing off macOS, off the Mac skin, or with the opt-out set.</summary>
    public static void Attach(Window window)
    {
        if (!Enabled) return;
        window.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent];
        window.Opened += (_, _) => Refresh(window);
        window.LayoutUpdated += (_, _) =>
        {
            var state = States.GetOrCreateValue(window);
            state.Debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
            {
                state.Debounce!.Stop();
                Refresh(window);
            });
            state.Debounce.Stop();
            state.Debounce.Start();
        };
    }

    static void Refresh(Window window)
    {
        try
        {
            var level = window.ActualTransparencyLevel;
            if (level != WindowTransparencyLevel.AcrylicBlur && level != WindowTransparencyLevel.Blur)
            {
                Fallback(window);
                return;
            }
            var shapes = CollectGlass(window);
            var state = States.GetOrCreateValue(window);
            if (state.Rects is { } last && SameShapes(last, shapes)) return;
            state.Rects = shapes;

            double scale = window.RenderScaling;
            var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(window.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(window.Bounds.Height * scale)));
            var scaled = shapes.ConvertAll(s => (Scale(s.Rect, scale), Scale(s.Radius, scale)));
            byte[] png = BuildMask(scaled, size);
            if (!TrySetMask(window, png)) { Fallback(window); return; }
            window.Classes.Remove("solidglass");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Fallback(window);
        }
    }

    static void Fallback(Window window)
    {
        if (!window.Classes.Contains("solidglass")) window.Classes.Add("solidglass");
    }

    static Rect Scale(Rect r, double s) => new(r.X * s, r.Y * s, r.Width * s, r.Height * s);

    static CornerRadius Scale(CornerRadius r, double s) => new(r.TopLeft * s, r.TopRight * s, r.BottomRight * s, r.BottomLeft * s);

    static bool SameShapes(List<(Rect Rect, CornerRadius Radius)> a, List<(Rect Rect, CornerRadius Radius)> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Rect.Equals(b[i].Rect)) return false;
            if (a[i].Radius != b[i].Radius) return false;
        }
        return true;
    }

    /// <summary>Every visible Glass in the window, in window coordinates: what the mask should show through.</summary>
    static List<(Rect Rect, CornerRadius Radius)> CollectGlass(Window window)
    {
        var list = new List<(Rect, CornerRadius)>();
        foreach (var v in window.GetVisualDescendants())
        {
            if (v is not Glass glass || !glass.IsEffectivelyVisible) continue;
            if (glass.Bounds.Width <= 0 || glass.Bounds.Height <= 0) continue;
            if (glass.TranslatePoint(new Point(0, 0), window) is not { } topLeft) continue;
            list.Add((new Rect(topLeft, glass.Bounds.Size), glass.CornerRadius));
        }
        return list;
    }

    /// <summary>A window-sized alpha mask, opaque white over every shape and transparent elsewhere: an
    /// <c>NSVisualEffectView</c>'s <c>maskImage</c> only shows the blur where the mask is opaque. Pure, so it's
    /// tested without a window: corners of a shape come out transparent, its centre opaque.</summary>
    public static byte[] BuildMask(IReadOnlyList<(Rect Rect, CornerRadius Radius)> shapes, PixelSize size)
    {
        using var bitmap = new SKBitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            foreach (var (rect, radius) in shapes)
            {
                using var rr = new SKRoundRect();
                rr.SetRectRadii(new SKRect((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom),
                [
                    Corner(radius.TopLeft), Corner(radius.TopRight), Corner(radius.BottomRight), Corner(radius.BottomLeft),
                ]);
                canvas.DrawRoundRect(rr, paint);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static SKPoint Corner(double r) => new((float)r, (float)r);

    // --- talking to AppKit: find the window's own NSVisualEffectView and mask it ----------------------------------

    static bool TrySetMask(Window window, byte[] png)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero) return false;
        IntPtr nsWindow = ObjC.Send(handle.Handle, ObjC.Sel("window"));
        if (nsWindow == IntPtr.Zero) return false;
        IntPtr contentView = ObjC.Send(nsWindow, ObjC.Sel("contentView"));
        if (contentView == IntPtr.Zero) return false;
        IntPtr effectView = FindVisualEffectView(contentView, depth: 0);
        if (effectView == IntPtr.Zero) return false;
        IntPtr image = MakeImage(png);
        if (image == IntPtr.Zero) return false;
        try
        {
            ObjC.SendPtr(effectView, ObjC.Sel("setMaskImage:"), image);
        }
        finally
        {
            ObjC.Send(image, ObjC.Sel("release"));
        }
        return true;
    }

    /// <summary>Depth-first search of the content view's subviews for the blur Avalonia.Native already made
    /// (its <c>_blurBehind</c>, an <c>NSVisualEffectView</c> covering the whole window). Capped so a surprising
    /// view tree can't recurse forever.</summary>
    static IntPtr FindVisualEffectView(IntPtr view, int depth)
    {
        if (!OperatingSystem.IsMacOS()) return IntPtr.Zero;
        if (view == IntPtr.Zero || depth > 12) return IntPtr.Zero;
        IntPtr effectClass = ObjC.objc_getClass("NSVisualEffectView");
        if (effectClass != IntPtr.Zero && ObjC.SendBoolArg(view, ObjC.Sel("isKindOfClass:"), effectClass)) return view;
        IntPtr subviews = ObjC.Send(view, ObjC.Sel("subviews"));
        if (subviews == IntPtr.Zero) return IntPtr.Zero;
        nuint count = ObjC.SendCount(subviews, ObjC.Sel("count"));
        for (nuint i = 0; i < count; i++)
        {
            IntPtr child = ObjC.SendIndex(subviews, ObjC.Sel("objectAtIndex:"), i);
            IntPtr found = FindVisualEffectView(child, depth + 1);
            if (found != IntPtr.Zero) return found;
        }
        return IntPtr.Zero;
    }

    /// <summary>An <c>NSImage</c> made from PNG bytes, retained (the caller releases it once it's handed to
    /// <c>setMaskImage:</c>, which retains its own copy).</summary>
    static unsafe IntPtr MakeImage(byte[] png)
    {
        if (!OperatingSystem.IsMacOS()) return IntPtr.Zero;
        fixed (byte* p = png)
        {
            IntPtr dataClass = ObjC.objc_getClass("NSData");
            IntPtr data = ObjC.SendPtrBytes(dataClass, ObjC.Sel("dataWithBytes:length:"), (IntPtr)p, (nuint)png.Length);
            if (data == IntPtr.Zero) return IntPtr.Zero;
            IntPtr imageClass = ObjC.objc_getClass("NSImage");
            IntPtr alloc = ObjC.Send(imageClass, ObjC.Sel("alloc"));
            if (alloc == IntPtr.Zero) return IntPtr.Zero;
            return ObjC.SendPtr(alloc, ObjC.Sel("initWithData:"), data);
        }
    }

    [SupportedOSPlatform("macos")]
    static class ObjC
    {
        const string Lib = "/usr/lib/libobjc.A.dylib";

        [DllImport(Lib)]
        public static extern IntPtr objc_getClass(string name);

        [DllImport(Lib)]
        static extern IntPtr sel_registerName(string name);

        public static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr SendPtrBytes(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr SendIndex(IntPtr receiver, IntPtr selector, nuint index);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern nuint SendCount(IntPtr receiver, IntPtr selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern bool SendBoolArg(IntPtr receiver, IntPtr selector, IntPtr arg);
    }
}
