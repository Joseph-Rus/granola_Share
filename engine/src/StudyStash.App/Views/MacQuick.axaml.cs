using Avalonia.Controls;

namespace StudyStash.App.Views;

public partial class MacQuick : UserControl
{
    public MacQuick()
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
