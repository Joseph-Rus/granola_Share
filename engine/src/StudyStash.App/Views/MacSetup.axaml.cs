using Avalonia.Controls;

namespace StudyStash.App.Views;

public partial class MacSetup : UserControl
{
    public MacSetup() => InitializeComponent();

    /// <summary>A real window has the system's traffic lights; screenshots draw their own.</summary>
    public bool DrawChrome
    {
        get => Lights.IsVisible;
        set => Lights.IsVisible = value;
    }
}
