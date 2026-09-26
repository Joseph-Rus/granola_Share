using Avalonia;

namespace StudyStash.App.Windows;

/// <summary>A display, as much as placing a window on it needs: its full bounds, its usable area (without the menu
/// bar, the Dock, or the taskbar), and its scale. A stand-in for Avalonia's own <c>Screen</c> (which can't be built
/// outside Avalonia), so placement can be tested without opening a real display.</summary>
public readonly record struct ScreenGeometry(PixelRect Bounds, PixelRect WorkingArea, double Scaling, bool IsPrimary = false);

/// <summary>
/// Where the app's floating windows land: the dropdown under the menu bar icon, the tray flyout above the taskbar,
/// the quick panel, the recorder where it was left, a toast — each clamped to whichever display it's near. Pure: a
/// test hands it a list of screens instead of opening any, so multi-display and taskbar-side cases can be checked
/// without a real window.
/// </summary>
public static class Placement
{
    /// <summary>Room, in points, a floating window keeps from the edge it hugs.</summary>
    public const double Gap = 12;

    /// <summary>A plain screen to fall back on when there's truly nothing to ask (no displays at all).</summary>
    static readonly ScreenGeometry NoScreens = new(new PixelRect(0, 0, 1440, 900), new PixelRect(0, 0, 1440, 864), 1, true);

    /// <summary>The display <paramref name="near"/> is on; else the primary one; else the first; else a plain
    /// 1440×900 stand-in.</summary>
    public static ScreenGeometry Pick(IReadOnlyList<ScreenGeometry> screens, PixelPoint? near = null)
    {
        if (near is { } p)
            foreach (var s in screens)
                if (s.Bounds.Contains(p)) return s;
        foreach (var s in screens)
            if (s.IsPrimary) return s;
        return screens.Count > 0 ? screens[0] : NoScreens;
    }

    /// <summary>Keeps a window of <paramref name="size"/> inside <paramref name="area"/>, allowing <paramref
    /// name="room"/> of it (the window's own invisible shadow padding) to spill past the edge.</summary>
    static PixelPoint Clamp(PixelRect area, PixelSize size, int x, int y, int room) => new(
        Math.Clamp(x, area.X - room, Math.Max(area.X - room, area.Right - size.Width + room)),
        Math.Clamp(y, area.Y - room, Math.Max(area.Y - room, area.Bottom - size.Height + room)));

    /// <summary>Centred under the menu bar icon (Mac), just below the menu bar, on whichever display the icon is
    /// on. <paramref name="anchor"/> is the icon's position in pixels.</summary>
    public static PixelPoint MacDropdown(PixelPoint anchor, IReadOnlyList<ScreenGeometry> screens, PixelSize size, int room = 0)
    {
        var s = Pick(screens, anchor);
        return Clamp(s.WorkingArea, size, anchor.X - size.Width / 2, s.WorkingArea.Y + (int)(6 * s.Scaling) - room, room);
    }

    /// <summary>Above the taskbar, on the display under the cursor: 12 px off the taskbar's own edge (wherever
    /// that is — bottom on Windows 11, any side on Windows 10) and off the screen's facing edge.</summary>
    public static PixelPoint TrayFlyout(PixelPoint cursor, IReadOnlyList<ScreenGeometry> screens, PixelSize size, int room = 0)
    {
        var s = Pick(screens, cursor);
        int gap = (int)(Gap * s.Scaling);
        bool taskbarTop = s.WorkingArea.Y > s.Bounds.Y, taskbarLeft = s.WorkingArea.X > s.Bounds.X;
        int x = taskbarLeft ? s.WorkingArea.X + gap - room : s.WorkingArea.Right - size.Width - gap + room;
        int y = taskbarTop ? s.WorkingArea.Y + gap - room : s.WorkingArea.Bottom - size.Height - gap + room;
        return Clamp(s.WorkingArea, size, x, y, room);
    }

    /// <summary>Centred, a fifth down the display under the pointer.</summary>
    public static PixelPoint QuickPanel(PixelPoint? pointer, IReadOnlyList<ScreenGeometry> screens, PixelSize size, int room = 0)
    {
        var s = Pick(screens, pointer);
        int x = s.WorkingArea.X + (s.WorkingArea.Width - size.Width) / 2;
        int y = s.WorkingArea.Y + s.WorkingArea.Height / 5 - room;
        return Clamp(s.WorkingArea, size, x, y, room);
    }

    /// <summary>The recorder's spot: where it was left (its saved top-right corner), if that display still exists;
    /// otherwise the default corner (top right, under the menu bar, on a Mac; bottom right, above the taskbar, on
    /// Windows) of the primary display.</summary>
    public static PixelPoint KeepOnScreen(PixelPoint? savedTopRight, IReadOnlyList<ScreenGeometry> screens, PixelSize size, bool mac, int room = 0)
    {
        if (savedTopRight is { } tr)
            foreach (var s in screens)
                if (s.Bounds.Contains(tr)) return Clamp(s.WorkingArea, size, tr.X - size.Width, tr.Y, room);
        var d = Pick(screens);
        int gap = (int)(Gap * d.Scaling);
        return mac
            ? new PixelPoint(d.WorkingArea.Right - size.Width - gap + room, d.WorkingArea.Y + (int)(10 * d.Scaling) - room)
            : new PixelPoint(d.WorkingArea.Right - size.Width - gap + room, d.WorkingArea.Bottom - size.Height - gap + room);
    }

    /// <summary>Where the nth toast stacks on the primary display: top right, downward on a Mac; bottom right,
    /// upward on Windows.</summary>
    public static PixelPoint ToastSpot(IReadOnlyList<ScreenGeometry> screens, int index, PixelSize size, bool mac, int room = 0)
    {
        var s = Pick(screens);
        int gap = (int)(Gap * s.Scaling);
        int x = s.WorkingArea.Right - size.Width - gap + room;
        int step = (int)(size.Height + Gap * s.Scaling) * index;
        int y = mac ? s.WorkingArea.Y + gap - room + step : s.WorkingArea.Bottom - size.Height - gap + room - step;
        return new PixelPoint(x, y);
    }
}
