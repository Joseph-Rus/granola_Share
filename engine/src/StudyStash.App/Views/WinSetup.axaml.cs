using Avalonia.Controls;

namespace StudyStash.App.Views;

public partial class WinSetup : UserControl
{
    public WinSetup() => InitializeComponent();

    /// <summary>A real window has the system's caption buttons; screenshots draw their own.</summary>
    public bool DrawChrome
    {
        get => Captions.IsVisible;
        set => Captions.IsVisible = value;
    }
}
