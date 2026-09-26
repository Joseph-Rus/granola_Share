using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using StudyStash.App.Controls;

namespace StudyStash.App.Tests;

/// <summary>
/// The real windows the Canvas screens sit in belong to WS2/WS6, so shots wrap our panes in a close copy of the
/// design's frame, built here from tokens: a design-08 panel (<see cref="MacStates"/>/<see cref="WinStates"/>).
/// Kept simple and token-based — they're here so a pane lines up with its ref picture, not to be reviewed as UI.
/// </summary>
public static class CanvasFrames
{
    /// <summary>Design 08: a 1000-wide rounded card, two columns, one grey label over each state's card.</summary>
    public static Control MacStates(params (string Label, Control Card)[] cards) => StatesPanel(cards, mac: true);

    public static Control WinStates(params (string Label, Control Card)[] cards) => StatesPanel(cards, mac: false);

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

    /// <summary>Design 07: the setup wizard's own frame — a sidebar of already-done steps (Microphone, Library,
    /// Transcription model, Classes) with Canvas current (and, on Windows, Taskbar still to do) — around whatever
    /// step content the test hands in.</summary>
    public static Control MacSetup(Control content) => SetupWindow(content, mac: true, winTaskbar: false);

    public static Control WinSetup(Control content) => SetupWindow(content, mac: false, winTaskbar: true);

    static Control SetupWindow(Control content, bool mac, bool winTaskbar)
    {
        var sidebar = new StackPanel { Width = 220, Spacing = 4 };
        if (mac)
        {
            var lights = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 20) };
            foreach (string colour in new[] { "#FF5F57", "#FEBC2E", "#28C840" })
                lights.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.Parse(colour)) });
            sidebar.Children.Add(lights);
        }
        string[] before = ["Microphone", "Library", "Transcription model", "Classes"];
        for (int i = 0; i < before.Length; i++) sidebar.Children.Add(SidebarRow(i + 1, before[i], done: true, current: false));
        sidebar.Children.Add(SidebarRow(before.Length + 1, "Canvas", done: false, current: true, trailing: "Optional"));
        if (winTaskbar) sidebar.Children.Add(SidebarRow(before.Length + 2, "Taskbar", done: false, current: false));

        var body = new Border { Padding = new Thickness(40, 40, 40, 32), Child = content, ClipToBounds = false };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(sidebar, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(sidebar);
        grid.Children.Add(body);

        var frame = new StackPanel { Spacing = 0 };
        if (!mac)
        {
            var titleBar = new Border { Height = 32, Padding = new Thickness(12, 0) };
            titleBar.Bind(Border.BackgroundProperty, titleBar.GetResourceObservable("Layer"));
            var title = new TextBlock { Text = "Set up Study Stash", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("Fg2"));
            titleBar.Child = title;
            frame.Children.Add(titleBar);
        }
        frame.Children.Add(grid);

        var window = new Border { Width = 900, Height = mac ? 760 : 800, Child = frame, ClipToBounds = false };
        if (mac)
        {
            window.CornerRadius = new CornerRadius(26);
            window.Bind(Border.BackgroundProperty, window.GetResourceObservable("Win"));
            window.Bind(Border.BoxShadowProperty, window.GetResourceObservable("GShadow"));
            window.Padding = new Thickness(32, 24, 0, 24);
        }
        else
        {
            window.CornerRadius = new CornerRadius(8);
            window.BorderThickness = new Thickness(1);
            window.Bind(Border.BackgroundProperty, window.GetResourceObservable("Mica"));
            window.Bind(Border.BorderBrushProperty, window.GetResourceObservable("FlyStroke"));
            window.Bind(Border.BoxShadowProperty, window.GetResourceObservable("ShadowLg"));
        }
        return window;
    }

    static Control SidebarRow(int number, string title, bool done, bool current, string? trailing = null)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 32, Margin = new Thickness(0, 2) };
        Control mark = done
            ? new Icon { Glyph = "check_circle", Size = 16, Filled = true }
            : new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.2) };
        if (mark is Icon markIcon) markIcon.Bind(Icon.ForegroundProperty, markIcon.GetResourceObservable("AccentText"));
        if (mark is Border markBorder)
        {
            markBorder.Bind(Border.BorderBrushProperty, markBorder.GetResourceObservable(current ? "Fg" : "Fg3"));
            var num = new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), FontSize = 9, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            num.Bind(TextBlock.ForegroundProperty, num.GetResourceObservable(current ? "Fg" : "Fg3"));
            markBorder.Child = num;
        }
        Grid.SetColumn(mark, 0);
        var label = new TextBlock { Text = title, FontSize = 13, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable(current ? "Fg" : done ? "Fg2" : "Fg3"));
        Grid.SetColumn(label, 1);
        row.Children.Add(mark);
        row.Children.Add(label);
        if (trailing is not null)
        {
            var tag = new TextBlock { Text = trailing, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            tag.Bind(TextBlock.ForegroundProperty, tag.GetResourceObservable("Fg3"));
            Grid.SetColumn(tag, 2);
            row.Children.Add(tag);
        }
        return row;
    }
}
