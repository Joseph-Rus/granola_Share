using Avalonia.Controls;

namespace StudyStash.App.Views;

public partial class MacSetup : UserControl
{
    public MacSetup() => InitializeComponent();

    /// <summary>A real window has the system's traffic lights, rounded corners and shadow; screenshots draw all three.</summary>
    public bool DrawChrome
    {
        get => Lights.IsVisible;
        set
        {
            Lights.IsVisible = value;
            Chrome.Classes.Set("chrome", value);
        }
    }
}
