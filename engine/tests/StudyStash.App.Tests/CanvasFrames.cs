using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using StudyStash.App.Controls;

namespace StudyStash.App.Tests;

/// <summary>
/// The real windows the Canvas screens sit in belong to WS2/WS6, so shots wrap our panes in a close copy of the
/// design's frame, built here from tokens: a design-08 panel (<see cref="MacStates"/>/<see cref="WinStates"/>) and a
/// design-06 settings window with its General/Recording/Library/Canvas/Shortcuts sidebar
/// (<see cref="MacSettings"/>/<see cref="WinSettings"/>). Kept simple and token-based — they're here so a pane lines
/// up with its ref picture, not to be reviewed as UI.
/// </summary>
public static class CanvasFrames
{
    /// <summary>Design 08: a 1000-wide rounded card, two columns, one grey label over each state's card.</summary>
    public static Control MacStates(params (string Label, Control Card)[] cards) => StatesPanel(cards, mac: true);

    public static Control WinStates(params (string Label, Control Card)[] cards) => StatesPanel(cards, mac: false);

    static readonly (string Glyph, string Label)[] SettingsNav =
    [
        ("tune", "General"), ("mic", "Recording"), ("dns", "Library"), ("school", "Canvas"), ("keyboard", "Shortcuts"),
    ];

    /// <summary>Design 06: a 900×820 Mac settings window, its glass sidebar showing the same sections as the real
    /// app with Canvas selected, the content pane holding whatever's given.</summary>
    public static Control MacSettings(Control content)
    {
        var nav = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 14) };
        nav.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Height = 44, Spacing = 8, Margin = new Thickness(10, 0, 0, 0),
            Children =
            {
                Dot("#FF5F57"), Dot("#FEBC2E"), Dot("#28C840"),
            },
        });
        foreach (var (glyph, label) in SettingsNav)
        {
            bool on = label == "Canvas";
            var row = new Border { Height = 32, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 0) };
            if (on) row.Bind(Border.BackgroundProperty, row.GetResourceObservable("Fill2"));
            var icon = new Icon { Glyph = glyph, Size = 16 };
            icon.Bind(Icon.ForegroundProperty, icon.GetResourceObservable(on ? "AccentText" : "Fg2"));
            var text = new TextBlock { Text = label, FontSize = 13, FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
            row.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, text } };
            nav.Children.Add(row);
        }
        var sidebar = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(0, 0, 0, 0), Child = nav };
        sidebar.Bind(Border.BackgroundProperty, sidebar.GetResourceObservable("Glass"));
        var sidebarPad = new Border { Padding = new Thickness(8, 8, 0, 8), Child = sidebar };

        var pane = new Border { Width = 220, Child = sidebarPad };
        var body = new Border { Height = 820, ClipToBounds = true, Padding = new Thickness(40, 40, 40, 32), Child = content };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), RowDefinitions = new RowDefinitions("*"), Children = { pane, body } };
        Grid.SetColumn(body, 1);

        var window = new Border { Width = 900, Height = 820, CornerRadius = new CornerRadius(26), ClipToBounds = true, Child = grid };
        window.Bind(Border.BackgroundProperty, window.GetResourceObservable("Win"));
        window.Bind(Border.BoxShadowProperty, window.GetResourceObservable("GShadow"));
        return window;
    }

    static Border Dot(string hex) => new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = Brush.Parse(hex) };

    /// <summary>Design 06: a 900×990 Windows settings window, its title bar and nav column matching the real app
    /// with Canvas selected, the content pane on a Layer background holding whatever's given.</summary>
    public static Control WinSettings(Control content)
    {
        var mark = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Margin = new Thickness(16, 0, 10, 0) };
        mark.Bind(Border.BackgroundProperty, mark.GetResourceObservable("Accent"));
        var markIcon = new Icon { Glyph = "graphic_eq", Size = 11 };
        markIcon.Bind(Icon.ForegroundProperty, markIcon.GetResourceObservable("OnAccent"));
        mark.Child = markIcon;
        var titleText = new TextBlock { Text = "Study Stash settings", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var titleBar = new Grid { Height = 32, ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Children = { mark, titleText } };
        Grid.SetColumn(titleText, 1);
        var caps = new StackPanel { Orientation = Orientation.Horizontal, Children = { CapBtn("remove"), CapBtn("crop_square"), CapBtn("close") } };
        Grid.SetColumn(caps, 2);
        titleBar.Children.Add(caps);

        var nav = new StackPanel { Spacing = 4, Margin = new Thickness(4, 4, 4, 8) };
        foreach (var (glyph, label) in SettingsNav)
        {
            bool on = label == "Canvas";
            var row = new Border { Height = 40, Margin = new Thickness(0, 0, 0, 0), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 0) };
            if (on) row.Bind(Border.BackgroundProperty, row.GetResourceObservable("Subtle"));
            var icon = new Icon { Glyph = glyph, Size = 16, Width = 16 };
            var text = new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            row.Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { icon, text } };
            nav.Children.Add(row);
        }
        var navPane = new Border { Width = 240, Child = nav };

        var layer = new Border { Height = 956, ClipToBounds = true, Padding = new Thickness(28, 28, 36, 28), BorderThickness = new Thickness(1, 1, 0, 0), Child = content };
        layer.Bind(Border.BackgroundProperty, layer.GetResourceObservable("Layer"));
        layer.Bind(Border.BorderBrushProperty, layer.GetResourceObservable("LayerStroke"));

        var below = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), RowDefinitions = new RowDefinitions("*"), Children = { navPane, layer } };
        Grid.SetColumn(layer, 1);
        var stack = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        stack.Children.Add(titleBar);
        stack.Children.Add(below);

        var window = new Border { Width = 900, Height = 990, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true, Child = stack };
        window.Bind(Border.BackgroundProperty, window.GetResourceObservable("Mica"));
        window.Bind(Border.BorderBrushProperty, window.GetResourceObservable("FlyStroke"));
        window.Bind(Border.BoxShadowProperty, window.GetResourceObservable("ShadowLg"));
        return window;
    }

    static Border CapBtn(string glyph)
    {
        var icon = new Icon { Glyph = glyph, Size = 14 };
        return new Border { Width = 46, Child = icon, HorizontalAlignment = HorizontalAlignment.Center };
    }

    static readonly (string Class, int Count)[] SidebarClasses = [("CS 101", 12), ("BIO 110", 9), ("CALC II", 11), ("HIST 210", 7)];

    static IBrush ClassDot(int i) => new SolidColorBrush(Color.Parse(StudyStash.Core.ClassColors.Hex(StudyStash.Core.ClassColors.For(i))));

    static StackPanel SidebarRow(string glyph, string label, string count, bool on, double height, double fontSize, bool bold = false)
    {
        var icon = new Icon { Glyph = glyph, Size = 16 };
        if (bold) icon.Bind(Icon.ForegroundProperty, icon.GetResourceObservable("AccentText"));
        var text = new TextBlock { Text = label, FontSize = fontSize, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
        var countText = new TextBlock { Text = count, FontSize = fontSize - 1, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
        countText.Bind(TextBlock.ForegroundProperty, countText.GetResourceObservable("Fg2"));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*,Auto"), Height = height, Margin = new Thickness(12, 0) };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(countText, 2);
        row.Children.Add(icon);
        row.Children.Add(text);
        row.Children.Add(countText);
        var wrap = new StackPanel { Children = { row } };
        return wrap;
    }

    static Border SidebarRowBorder(string glyph, string label, string count, bool on, bool mac)
    {
        var content = SidebarRow(glyph, label, count, on, mac ? 32 : 40, 13, on);
        var border = new Border { CornerRadius = new CornerRadius(mac ? 10 : 4), Child = content, Margin = new Thickness(mac ? 0 : 4, 0) };
        if (on) border.Bind(Border.BackgroundProperty, border.GetResourceObservable(mac ? "Fill2" : "Subtle"));
        return border;
    }

    static Border SidebarDot(string cls, int i, int count, bool mac)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = ClassDot(i), VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = cls, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var countText = new TextBlock { Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
        countText.Bind(TextBlock.ForegroundProperty, countText.GetResourceObservable("Fg2"));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = mac ? 32 : 40, Margin = new Thickness(12, 0) };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(countText, 2);
        row.Children.Add(dot);
        row.Children.Add(text);
        row.Children.Add(countText);
        return new Border { Child = row, Margin = new Thickness(mac ? 0 : 4, 0) };
    }

    static Control SidebarBody(string selected, bool mac)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(SidebarRowBorder("event_upcoming", "Due", "3", selected == "Due", mac));
        var classesLabel = new TextBlock { Text = "Classes", FontSize = mac ? 11 : 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(mac ? 10 : 16, 10, 0, 4) };
        stack.Children.Add(classesLabel);
        for (int i = 0; i < SidebarClasses.Length; i++)
            stack.Children.Add(SidebarDot(SidebarClasses[i].Class, i, SidebarClasses[i].Count, mac));
        stack.Children.Add(new Border { Height = 8 });
        stack.Children.Add(SidebarRowBorder("inbox", "Unsorted", "2", selected == "Unsorted", mac));
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(mac ? 10 : 16, 12, 0, mac ? 0 : 8) };
        var goodDot = new Ellipse { Width = 6, Height = 6 };
        goodDot.Bind(Ellipse.FillProperty, goodDot.GetResourceObservable("Good"));
        var statusText = new TextBlock { Text = "Library connected · Canvas synced 10:24", FontSize = mac ? 11 : 12 };
        statusText.Bind(TextBlock.ForegroundProperty, statusText.GetResourceObservable("Fg2"));
        status.Children.Add(goodDot);
        status.Children.Add(statusText);
        var dock = new DockPanel();
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);
        var scroller = new StackPanel { Spacing = 2, Children = { stack } };
        dock.Children.Add(scroller);
        return dock;
    }

    /// <summary>Design 09/10: a 1280×800 Mac library window (grid 248 | 340 | *), the Due sidebar item (or a class)
    /// selected, the given list and (optionally) detail panes filling the rest.</summary>
    public static Control MacApp(string selected, Control list, Control? detail = null)
    {
        var trafficLights = new StackPanel
        {
            Orientation = Orientation.Horizontal, Height = 44, Spacing = 8, Margin = new Thickness(10, 0, 0, 0),
            Children = { Dot("#FF5F57"), Dot("#FEBC2E"), Dot("#28C840") },
        };
        var nav = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 0), Children = { trafficLights, SidebarBody(selected, mac: true) } };
        var sidebar = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(0, 0, 0, 10), Child = nav };
        sidebar.Bind(Border.BackgroundProperty, sidebar.GetResourceObservable("Glass"));
        var sidebarPad = new Border { Padding = new Thickness(8, 8, 0, 8), Child = sidebar };
        var sidebarPane = new Border { Width = 248, Child = sidebarPad };

        var listPane = new Border { Width = 340, BorderThickness = new Thickness(0, 0, 1, 0), Child = list };
        listPane.Bind(Border.BorderBrushProperty, listPane.GetResourceObservable("Sep"));

        var columns = detail is null ? "248,*" : "248,340,*";
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Height = 800 };
        Grid.SetColumn(sidebarPane, 0);
        grid.Children.Add(sidebarPane);
        if (detail is null)
        {
            var only = new Border { Child = list };
            Grid.SetColumn(only, 1);
            grid.Children.Add(only);
        }
        else
        {
            Grid.SetColumn(listPane, 1);
            grid.Children.Add(listPane);
            var detailPane = new Border { ClipToBounds = true, Child = detail };
            Grid.SetColumn(detailPane, 2);
            grid.Children.Add(detailPane);
        }

        var window = new Border { Width = 1280, Height = 800, CornerRadius = new CornerRadius(26), ClipToBounds = true, Child = grid };
        window.Bind(Border.BackgroundProperty, window.GetResourceObservable("Win"));
        window.Bind(Border.BoxShadowProperty, window.GetResourceObservable("GShadow"));
        return window;
    }

    /// <summary>Design 09/10: a 1280×800 Windows library window (title bar, nav 280, list 340, detail *).</summary>
    public static Control WinApp(string selected, Control list, Control? detail = null)
    {
        var mark = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Margin = new Thickness(16, 0, 12, 0) };
        mark.Bind(Border.BackgroundProperty, mark.GetResourceObservable("Accent"));
        var markIcon = new Icon { Glyph = "graphic_eq", Size = 12 };
        markIcon.Bind(Icon.ForegroundProperty, markIcon.GetResourceObservable("OnAccent"));
        mark.Child = markIcon;
        var appName = new TextBlock { Text = "Study Stash", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Children = { mark, appName } };

        var search = new Border { Width = 400, Height = 32, CornerRadius = new CornerRadius(4) };
        search.Bind(Border.BackgroundProperty, search.GetResourceObservable("Ctrl"));
        search.Bind(Border.BorderBrushProperty, search.GetResourceObservable("CtrlBorder"));
        search.BorderThickness = new Thickness(1);
        var searchText = new TextBlock { Text = "Search notes and lectures", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0) };
        searchText.Bind(TextBlock.ForegroundProperty, searchText.GetResourceObservable("Fg3"));
        search.Child = searchText;

        var caps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { CapBtn("remove"), CapBtn("crop_square"), CapBtn("close") } };
        var titleBar = new Grid { Height = 48, ColumnDefinitions = new ColumnDefinitions("*,400,*") };
        Grid.SetColumn(search, 1);
        Grid.SetColumn(caps, 2);
        titleBar.Children.Add(left);
        titleBar.Children.Add(search);
        titleBar.Children.Add(caps);

        var navBody = SidebarBody(selected, mac: false);
        var settingsRow = SidebarRowBorder("settings", "Settings", "", false, mac: false);
        var navDock = new DockPanel();
        DockPanel.SetDock(settingsRow, Dock.Bottom);
        navDock.Children.Add(settingsRow);
        navDock.Children.Add(navBody);
        var navPane = new Border { Width = 280, Padding = new Thickness(4, 4, 4, 8), Child = navDock };

        var listPane = new Border { Width = 340, BorderThickness = new Thickness(0, 0, 1, 0), Child = list };
        listPane.Bind(Border.BorderBrushProperty, listPane.GetResourceObservable("Sep"));

        Control layerContent;
        if (detail is null)
        {
            layerContent = list;
        }
        else
        {
            var layerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("340,*") };
            Grid.SetColumn(listPane, 0);
            var detailPane = new Border { ClipToBounds = true, Child = detail };
            Grid.SetColumn(detailPane, 1);
            layerGrid.Children.Add(listPane);
            layerGrid.Children.Add(detailPane);
            layerContent = layerGrid;
        }
        var layer = new Border
        {
            ClipToBounds = true, BorderThickness = new Thickness(1, 1, 0, 0), Child = layerContent,
        };
        layer.Bind(Border.BackgroundProperty, layer.GetResourceObservable("Layer"));
        layer.Bind(Border.BorderBrushProperty, layer.GetResourceObservable("LayerStroke"));

        var below = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*"), Height = 752 };
        Grid.SetColumn(navPane, 0);
        Grid.SetColumn(layer, 1);
        below.Children.Add(navPane);
        below.Children.Add(layer);

        var stack = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        stack.Children.Add(titleBar);
        stack.Children.Add(below);

        var window = new Border { Width = 1280, Height = 800, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true, Child = stack };
        window.Bind(Border.BackgroundProperty, window.GetResourceObservable("Mica"));
        window.Bind(Border.BorderBrushProperty, window.GetResourceObservable("FlyStroke"));
        window.Bind(Border.BoxShadowProperty, window.GetResourceObservable("ShadowLg"));
        return window;
    }

    static Control StatesPanel((string Label, Control Card)[] cards, bool mac)
    {
        int rows = (cards.Length + 1) / 2;
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", rows))),
            ColumnSpacing = 28,
            RowSpacing = 20,
        };
        for (int i = 0; i < cards.Length; i++)
        {
            var (label, card) = cards[i];
            var lbl = new TextBlock { Text = label, FontSize = mac ? 11 : 12 };
            lbl.Bind(TextBlock.ForegroundProperty, lbl.GetResourceObservable("Fg3"));
            var column = new StackPanel { Spacing = 8, MinWidth = 0, Children = { lbl, card } };
            Grid.SetColumn(column, i % 2);
            Grid.SetRow(column, i / 2);
            grid.Children.Add(column);
        }
        var border = new Border { Width = 1000, Padding = new Thickness(32), Child = grid, ClipToBounds = false };
        if (mac)
        {
            border.CornerRadius = new CornerRadius(26);
            border.Bind(Border.BackgroundProperty, border.GetResourceObservable("Win"));
            border.Bind(Border.BoxShadowProperty, border.GetResourceObservable("GShadow"));
        }
        else
        {
            border.CornerRadius = new CornerRadius(8);
            border.BorderThickness = new Thickness(1);
            border.Bind(Border.BackgroundProperty, border.GetResourceObservable("Mica"));
            border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("FlyStroke"));
            border.Bind(Border.BoxShadowProperty, border.GetResourceObservable("ShadowLg"));
        }
        return border;
    }
}
