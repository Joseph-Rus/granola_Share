using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

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
}
