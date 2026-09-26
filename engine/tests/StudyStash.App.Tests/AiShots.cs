using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Styling;
using StudyStash.App.Controls;
using StudyStash.App.ViewModels;
using StudyStash.App.Views;

namespace StudyStash.App.Tests;

/// <summary>
/// Shots of the AI screens (13–18): each control drawn inside a test-only settings or setup window frame — the
/// design's window chrome and sidebar, never app code, just enough scaffolding for the picture to line up with the
/// ref. <see cref="SettingsFrame"/> is Mac 900×780 / Windows 900×860 with the 7-section nav; <see cref="SetupFrame"/>
/// is the library setup window, Mac 900×640 / Windows 900×680.
/// </summary>
/// <summary>Binding a control's brush to a skin/theme resource, in code (the equivalent of a view's DynamicResource),
/// for the test-only frames <see cref="AiShots"/> draws.</summary>
static class AiShotBrushes
{
    public static Border Bg(this Border b, string key)
    {
        b.Bind(Border.BackgroundProperty, b.GetResourceObservable(key));
        return b;
    }

    public static Border Stroke(this Border b, string key)
    {
        b.Bind(Border.BorderBrushProperty, b.GetResourceObservable(key));
        return b;
    }

    public static Icon Res(this Icon icon, string key)
    {
        icon.Bind(Icon.ForegroundProperty, icon.GetResourceObservable(key));
        return icon;
    }

    public static TextBlock Res(this TextBlock t, string key)
    {
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(key));
        return t;
    }
}

public class AiShots
{
    static readonly ThemeVariant[] Themes = [ThemeVariant.Light, ThemeVariant.Dark];

    static AiShots() => Environment.SetEnvironmentVariable("STUDYSTASH_STILL", "1");

    // --- Settings window (13/14) --------------------------------------------------------------------------------

    static readonly (string Icon, string Label)[] Sections =
    [
        ("tune", "General"), ("mic", "Recording"), ("dns", "Library"), ("auto_awesome", "AI engines"),
        ("hub", "AI tool access"), ("school", "Canvas"), ("keyboard", "Shortcuts"),
    ];

    static Control MacNavRow(string icon, string label, bool selected)
    {
        var row = new Border { Height = 32, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0) };
        if (selected) row.Bg("Fill2");
        var i = new Icon { Glyph = icon, Size = 16 };
        if (selected) i.Res("AccentText");
        var text = new TextBlock { Text = label, FontSize = 13, FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
        row.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { i, text } };
        return row;
    }

    static Control MacSidebar(string selected)
    {
        var lights = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Height = 44, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.Parse("#FF5F57")) },
                new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.Parse("#FEBC2E")) },
                new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.Parse("#28C840")) },
            },
        };
        var nav = new StackPanel { Spacing = 2 };
        nav.Children.Add(lights);
        foreach (var (icon, label) in Sections) nav.Children.Add(MacNavRow(icon, label, label == selected));
        return new Border { Width = 212, CornerRadius = new CornerRadius(18), Padding = new Thickness(10, 0, 10, 14), Child = nav }
            .Bg("Glass");
    }

    static Control WinNavRow(string icon, string label, bool selected)
    {
        var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,14,*"), Height = 40, Margin = new Thickness(4, 0) };
        if (selected)
        {
            var fill = new Border { CornerRadius = new CornerRadius(4) }.Bg("Subtle");
            row.Children.Add(fill);
            var bar = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0) };
            bar.Bind(Border.BackgroundProperty, bar.GetResourceObservable("Accent"));
            row.Children.Add(bar);
        }
        var i = new Icon { Glyph = icon, Size = 16, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(i, 0);
        var text = new TextBlock { Text = label, FontSize = 14, FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 2);
        row.Children.Add(i);
        row.Children.Add(text);
        return row;
    }

    static Control WinNav(string selected)
    {
        var nav = new StackPanel { Width = 240, Spacing = 4, Margin = new Thickness(4, 4, 4, 8) };
        foreach (var (icon, label) in Sections) nav.Children.Add(WinNavRow(icon, label, label == selected));
        return nav;
    }

    static Control WinTitleBar()
    {
        var tile = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center }.Bg("Accent");
        tile.Child = new Icon { Glyph = "graphic_eq", Size = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.Res("OnAccent");
        var title = new TextBlock { Text = "Study Stash settings", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Children =
            {
                new Border { Width = 46, Height = 32, Child = new Icon { Glyph = "remove", Size = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                new Border { Width = 46, Height = 32, Child = new Icon { Glyph = "crop_square", Size = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
                new Border { Width = 46, Height = 32, Child = new Icon { Glyph = "close", Size = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        var grid = new Grid { Height = 32, ColumnDefinitions = ColumnDefinitions.Parse("Auto,10,*,Auto") };
        Grid.SetColumn(tile, 0);
        Grid.SetColumn(title, 2);
        Grid.SetColumn(buttons, 3);
        grid.Margin = new Thickness(16, 0, 0, 0);
        grid.Children.Add(tile);
        grid.Children.Add(title);
        grid.Children.Add(buttons);
        return grid;
    }

    /// <summary>The settings window scaffolding: Mac 900×780 r26 with the inset glass sidebar; Windows 900×860 r8
    /// Mica with the 32px title bar and 240-wide nav. <paramref name="selected"/> names the highlighted section
    /// ("AI engines", "AI tool access", …). Test-only: no production view draws this frame.</summary>
    public static Control SettingsFrame(SkinKind skin, string selected, Control pane)
    {
        if (skin == SkinKind.Mac)
        {
            var body = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("220,*") };
            var sidebarSlot = new Border { Padding = new Thickness(8, 8, 0, 8), Child = MacSidebar(selected) };
            Grid.SetColumn(sidebarSlot, 0);
            Grid.SetColumn(pane, 1);
            body.Children.Add(sidebarSlot);
            body.Children.Add(pane);
            return new Border { Width = 900, Height = 780, CornerRadius = new CornerRadius(26), ClipToBounds = true, Child = body }.Bg("Win");
        }
        else
        {
            var content = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("240,*") };
            var nav = WinNav(selected);
            Grid.SetColumn(nav, 0);
            var layer = new Border { CornerRadius = new CornerRadius(8, 0, 0, 0), BorderThickness = new Thickness(1, 1, 0, 0), Child = pane }.Bg("Layer").Stroke("LayerStroke");
            Grid.SetColumn(layer, 1);
            content.Children.Add(nav);
            content.Children.Add(layer);
            var root = new DockPanel();
            var titleBar = WinTitleBar();
            DockPanel.SetDock(titleBar, Dock.Top);
            root.Children.Add(titleBar);
            root.Children.Add(content);
            return new Border { Width = 900, Height = 860, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true, Child = root }
                .Bg("Mica").Stroke("FlyStroke");
        }
    }

    /// <summary>The library setup window scaffolding: Mac 900×640, Windows 900×680, "Set up your library" with a
    /// Back/Next footer. Test-only.</summary>
    public static Control SetupFrame(SkinKind skin, Control pane)
    {
        double height = skin == SkinKind.Mac ? 640 : 680;
        var footer = new Border { Height = 56, Padding = new Thickness(24, 0) };
        var footerRow = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto") };
        var back = new TextBlock { Text = "Back", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var next = new Border { CornerRadius = new CornerRadius(15), Padding = new Thickness(16, 0), Height = 30, Child = new TextBlock { Text = "Next", FontSize = 13, VerticalAlignment = VerticalAlignment.Center } }.Bg("Accent");
        Grid.SetColumn(back, 1);
        Grid.SetColumn(next, 2);
        footerRow.Children.Add(back);
        footerRow.Children.Add(next);
        footer.Child = footerRow;
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        var title = new TextBlock { Text = "Library setup · step 3 of 4", FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(28, 24, 28, 0) };
        var body = new DockPanel();
        DockPanel.SetDock(title, Dock.Top);
        body.Children.Add(title);
        body.Children.Add(pane);
        root.Children.Add(body);
        return new Border
        {
            Width = 900, Height = height, CornerRadius = new CornerRadius(skin == SkinKind.Mac ? 26 : 8), ClipToBounds = true,
            BorderThickness = skin == SkinKind.Mac ? default : new Thickness(1), Child = root,
        }.Bg(skin == SkinKind.Mac ? "Win" : "Mica").Stroke("FlyStroke");
    }

    // --- 14: AI tool access --------------------------------------------------------------------------------------

    static AiAccessModel AccessModel()
    {
        var m = new AiAccessModel(new FakeAiLibrary());
        m.On = true;
        m.ReadLectures = true;
        m.ReadNotes = true;
        m.ReadCanvas = true;
        m.ReadAudio = false;
        m.Connected.Add(new AiConnectionRow { Id = "1", Name = "Claude Code", Detail = "Signed in from the web", UsedWords = "Used 10:40", CanRemove = true, First = true });
        m.Connected.Add(new AiConnectionRow { Id = "2", Name = "Codex", Detail = "Token", UsedWords = "Used Tue", CanRemove = true });
        return m;
    }

    [AvaloniaFact]
    public void Mac_ai_tool_access()
    {
        foreach (var t in Themes)
            Shot.Take("mac-14-ai-tool-access", SkinKind.Mac, t, () => SettingsFrame(SkinKind.Mac, "AI tool access", new MacAiAccess { DataContext = AccessModel() }));
    }

    [AvaloniaFact]
    public void Win_ai_tool_access()
    {
        foreach (var t in Themes)
            Shot.Take("win-14-ai-tool-access", SkinKind.Win, t, () => SettingsFrame(SkinKind.Win, "AI tool access", new WinAiAccess { DataContext = AccessModel() }));
    }
}
