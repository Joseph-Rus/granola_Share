using Avalonia.Controls;
using Avalonia.Interactivity;
using StudyStash.App.Services;

namespace StudyStash.App.Views;

/// <summary>Settings for both looks in one view: <see cref="StudyStash.App.Skin.Current"/> decides once, at
/// construction, which of the "mac"/"win" root classes every look-specific style in the markup keys off.</summary>
public partial class SettingsView : UserControl
{
    bool drawChrome;

    public SettingsView()
    {
        InitializeComponent();
        bool mac = Skin.Current == SkinKind.Mac;
        // A plain if, not a "Mac ? ... : ..." ternary: ThemeTests.Every_token_a_view_asks_for_exists reads that
        // shape as a by-look DynamicResource pair and would otherwise treat "mac"/"win" as token names.
        if (mac) Classes.Add("mac"); else Classes.Add("win");
        Cols.ColumnDefinitions[0].Width = new GridLength(mac ? 220 : 240);
        // Fixed, like the design's canvas: set here (not by a style on the root matching itself) so it always wins.
        Width = 900;
        Height = mac ? 780 : 860;
    }

    /// <summary>A real window has the system's traffic lights (Mac) or caption buttons (Windows); screenshots draw
    /// their own mockups instead.</summary>
    public bool DrawChrome
    {
        get => drawChrome;
        set
        {
            drawChrome = value;
            Lights.IsVisible = value && Skin.Current == SkinKind.Mac;
            Captions.IsVisible = value && Skin.Current == SkinKind.Win;
            // The Mac's own radius/shadow are for a screenshot only (a real window is already rounded by the OS).
            Chrome.Classes.Set("chrome", value);
        }
    }

    void OnTimesLostFocus(object? sender, RoutedEventArgs e) => (DataContext as SettingsModel)?.SaveTimetableCommand.Execute(null);
}
