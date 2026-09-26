using Avalonia.Controls;

namespace StudyStash.App.Views;

public partial class WinAssignment : UserControl
{
    public WinAssignment()
    {
        InitializeComponent();
        Fades.Under(BottomFade, "Mica", 1);
    }
}
