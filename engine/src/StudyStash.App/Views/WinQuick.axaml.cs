using Avalonia.Controls;

namespace StudyStash.App.Views;

public partial class WinQuick : UserControl
{
    public WinQuick()
    {
        InitializeComponent();
        QuickKeys.Attach(this, QueryBox);
    }

    public void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }
}
