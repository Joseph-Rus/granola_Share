using Avalonia;
using Avalonia.Controls;
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
