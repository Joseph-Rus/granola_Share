using Avalonia;
using StudyStash.App.Windows;

namespace StudyStash.App.Tests;

public class PlacementTests
{
    static readonly PixelSize Small = new(320, 200);

    // Two 1920x1080 displays side by side, the second at 2x scale (as if plugged into a laptop's own Retina panel).
    static readonly ScreenGeometry Left = new(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 25, 1920, 1055), 1, IsPrimary: true);
    static readonly ScreenGeometry Right = new(new PixelRect(1920, 0, 3840, 2160), new PixelRect(1920, 50, 3840, 2110), 2);
    static readonly List<ScreenGeometry> SideBySide = [Left, Right];

    // A laptop screen with a display stacked above it (a monitor arm).
    static readonly ScreenGeometry Bottom = new(new PixelRect(0, 1080, 1440, 900), new PixelRect(0, 1080, 1440, 864), 1, IsPrimary: true);
    static readonly ScreenGeometry Top = new(new PixelRect(0, 0, 1440, 1080), new PixelRect(0, 0, 1440, 1080), 1);
    static readonly List<ScreenGeometry> Stacked = [Bottom, Top];

    [Fact]
    public void The_dropdown_lands_on_the_display_the_icon_is_on_not_the_primary_one()
    {
        // The icon sits well into the second, scaled display.
        var p = Placement.MacDropdown(new PixelPoint(2500, 0), SideBySide, Small);
        Assert.InRange(p.X, Right.WorkingArea.X, Right.WorkingArea.Right - Small.Width);
        Assert.Equal(Right.WorkingArea.Y + (int)(6 * Right.Scaling), p.Y);
    }

    [Fact]
    public void The_dropdown_near_a_displays_right_edge_stays_inside_it()
    {
        var p = Placement.MacDropdown(new PixelPoint(Left.Bounds.Right - 4, 0), SideBySide, Small);
        Assert.True(p.X + Small.Width <= Left.WorkingArea.Right);
        Assert.True(p.X >= Left.WorkingArea.X);
    }

    [Fact]
    public void The_tray_flyout_sits_above_a_bottom_taskbar()
    {
        var s = new ScreenGeometry(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1032), 1, IsPrimary: true);
        var p = Placement.TrayFlyout(new PixelPoint(1800, 1000), [s], Small);
        Assert.Equal(s.WorkingArea.Bottom - Small.Height - 12, p.Y);
        Assert.Equal(s.WorkingArea.Right - Small.Width - 12, p.X);
    }

    [Fact]
    public void The_tray_flyout_sits_beside_a_left_taskbar()
    {
        var s = new ScreenGeometry(new PixelRect(0, 0, 1920, 1080), new PixelRect(48, 0, 1920, 1080), 1, IsPrimary: true);
        var p = Placement.TrayFlyout(new PixelPoint(60, 800), [s], Small);
        Assert.Equal(s.WorkingArea.X + 12, p.X);
        Assert.Equal(s.WorkingArea.Bottom - Small.Height - 12, p.Y);
    }

    [Fact]
    public void The_tray_flyout_sits_below_a_top_taskbar()
    {
        var s = new ScreenGeometry(new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 48, 1920, 1080), 1, IsPrimary: true);
        var p = Placement.TrayFlyout(new PixelPoint(1800, 60), [s], Small);
        Assert.Equal(s.WorkingArea.Y + 12, p.Y);
        Assert.Equal(s.WorkingArea.Right - Small.Width - 12, p.X);
    }

    [Fact]
    public void The_quick_panel_is_centred_a_fifth_down_the_screen_under_the_pointer()
    {
        var p = Placement.QuickPanel(new PixelPoint(2500, 100), SideBySide, Small);
        Assert.Equal(Right.WorkingArea.X + (Right.WorkingArea.Width - Small.Width) / 2, p.X);
        Assert.Equal(Right.WorkingArea.Y + Right.WorkingArea.Height / 5, p.Y);
    }

    [Fact]
    public void A_recorder_saved_on_a_display_thats_still_there_stays_put()
    {
        var saved = new PixelPoint(2500, 100); // a top-right corner on the second display
        var p = Placement.KeepOnScreen(saved, SideBySide, Small, mac: true);
        Assert.Equal(saved.X - Small.Width, p.X);
        Assert.Equal(saved.Y, p.Y);
    }

    [Fact]
    public void A_recorder_saved_on_a_display_thats_gone_goes_to_the_default_corner()
    {
        var saved = new PixelPoint(2500, 100);
        // Only the first display remains: the saved spot (on the second) no longer exists.
        var p = Placement.KeepOnScreen(saved, [Left], Small, mac: true);
        var expected = Placement.KeepOnScreen(null, [Left], Small, mac: true);
        Assert.Equal(expected, p);
        Assert.True(p.X + Small.Width <= Left.WorkingArea.Right);
    }

    [Fact]
    public void The_recorders_default_corner_differs_by_system()
    {
        var mac = Placement.KeepOnScreen(null, [Left], Small, mac: true);
        var win = Placement.KeepOnScreen(null, [Left], Small, mac: false);
        Assert.True(mac.Y < Left.WorkingArea.Bottom / 2); // near the top, under the menu bar
        Assert.True(win.Y > Left.WorkingArea.Bottom / 2); // near the bottom, above the taskbar
        Assert.Equal(mac.X, win.X); // both hug the right edge
    }

    [Fact]
    public void With_two_displays_stacked_a_point_picks_the_one_it_falls_in()
    {
        Assert.Equal(Bottom, Placement.Pick(Stacked, new PixelPoint(700, 1500)));
        Assert.Equal(Top, Placement.Pick(Stacked, new PixelPoint(700, 500)));
    }

    [Fact]
    public void With_no_hint_placement_falls_back_to_the_primary_display()
    {
        Assert.Equal(Left, Placement.Pick(SideBySide, null));
        Assert.Equal(Left, Placement.Pick([Right, Left], null)); // order doesn't matter, only IsPrimary
    }

    [Fact]
    public void Toasts_stack_downward_on_a_mac_and_upward_on_windows()
    {
        var s = Left;
        var first = Placement.ToastSpot([s], 0, Small, mac: true);
        var second = Placement.ToastSpot([s], 1, Small, mac: true);
        Assert.True(second.Y > first.Y);

        var firstWin = Placement.ToastSpot([s], 0, Small, mac: false);
        var secondWin = Placement.ToastSpot([s], 1, Small, mac: false);
        Assert.True(secondWin.Y < firstWin.Y);
    }
}
