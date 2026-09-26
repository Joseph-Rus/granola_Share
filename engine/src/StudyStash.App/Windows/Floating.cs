using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using StudyStash.App.Views;

namespace StudyStash.App.Windows;

/// <summary>
/// A window that is only its content: the dropdown, the recorder, the quick panel, a notification. The window is clear
/// and a little bigger than what it shows, so the content's own rounded corners and shadow (the design's) show round
/// it. It sits above other windows and out of the taskbar.
/// </summary>
public class Floating : Window
{
    /// <summary>Room for the content's shadow, all round.</summary>
    public const double ShadowRoom = 28;

    readonly Border holder = new() { Padding = new Thickness(ShadowRoom), ClipToBounds = false };

    public Floating()
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        base.Content = holder;
        Look.Apply(this);
        Deactivated += (_, _) =>
        {
            if (!CloseOnDeactivate || !IsVisible) return;
            Hide();
            LastDeactivateHide = DateTime.UtcNow;
        };
        Opened += (_, _) => JoinFullScreenSpaces();
    }

    /// <summary>A Mac: let this window show over a full-screen app's own Space (a lecture on a full-screen Zoom
    /// call), instead of being stuck behind it. No-op off a Mac, and in tests (no real NSWindow to ask).</summary>
    void JoinFullScreenSpaces()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            if (TryGetPlatformHandle() is not { HandleDescriptor: "NSView", Handle: var view } || view == IntPtr.Zero) return;
            IntPtr nsWindow = objc_msgSend(view, sel_registerName("window"));
            if (nsWindow == IntPtr.Zero) return;
            const nuint canJoinAllSpaces = 1 << 0, fullScreenAuxiliary = 1 << 8;
            objc_msgSend_setCollectionBehavior(nsWindow, sel_registerName("setCollectionBehavior:"), canJoinAllSpaces | fullScreenAuxiliary);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>When this window last hid itself because it lost focus (clicking elsewhere, or the icon that opened
    /// it): toggling it again within <see cref="ToggleDebounce"/> of that just closes it, rather than reopening it
    /// (the deactivate and the click that follows are two events for one gesture).</summary>
    public DateTime LastDeactivateHide { get; private set; } = DateTime.MinValue;

    public static readonly TimeSpan ToggleDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>What it shows (the window's real content is the padding round it).</summary>
    public new Control? Content
    {
        get => holder.Child;
        set => holder.Child = value;
    }

    /// <summary>Close when you click elsewhere (the dropdown, the quick panel).</summary>
    public bool CloseOnDeactivate { get; init; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && CloseOnDeactivate)
        {
            Hide();
            e.Handled = true;
        }
    }

    /// <summary>The screen's usable area in pixels, and its scale.</summary>
    public (PixelRect Area, double Scale) WorkArea(PixelPoint? near = null)
    {
        var screen = (near is { } p ? Screens.ScreenFromPoint(p) : null) ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        return screen is null ? (new PixelRect(0, 0, 1440, 900), 1) : (screen.WorkingArea, screen.Scaling);
    }

    /// <summary>Every display, as <see cref="Placement"/> needs them.</summary>
    public IReadOnlyList<ScreenGeometry> ScreenList() =>
        Screens.All.Select(s => new ScreenGeometry(s.Bounds, s.WorkingArea, s.Scaling, s.IsPrimary)).ToList();

    /// <summary>The size it will be, in pixels (measured before it's shown).</summary>
    public PixelSize Measured(double scale)
    {
        holder.Measure(Size.Infinity);
        var s = holder.DesiredSize;
        return new PixelSize((int)Math.Ceiling(s.Width * scale), (int)Math.Ceiling(s.Height * scale));
    }

    /// <summary>Where the pointer is on screen, in pixels: where the menu bar icon was clicked.</summary>
    public static PixelPoint? Pointer()
    {
        try
        {
            if (OperatingSystem.IsWindows() && GetCursorPos(out var p)) return new PixelPoint(p.X, p.Y);
            if (OperatingSystem.IsMacOS())
            {
                var at = MouseLocation(objc_getClass("NSEvent"), sel_registerName("mouseLocation"));
                return new PixelPoint((int)at.X, 0); // points from the bottom left; only across matters for the menu bar
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CGPoint
    {
        public double X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern CGPoint MouseLocation(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern void objc_msgSend_setCollectionBehavior(IntPtr receiver, IntPtr selector, nuint behavior);
}
