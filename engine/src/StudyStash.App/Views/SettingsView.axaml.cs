using Avalonia.Controls;
using Avalonia.Interactivity;
using StudyStash.App.Services;

namespace StudyStash.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    void OnTimesLostFocus(object? sender, RoutedEventArgs e) => (DataContext as SettingsModel)?.SaveTimetableCommand.Execute(null);
}
