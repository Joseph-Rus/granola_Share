using Avalonia.Controls;
using Avalonia.Input;
using StudyStash.App.ViewModels;

namespace StudyStash.App.Views;

public partial class MacLibrary : UserControl
{
    public MacLibrary()
    {
        InitializeComponent();
        Fades.Under(Fade, "Win", 0.7);
    }

    void OnNotesTab(object? sender, PointerPressedEventArgs e)
    {
        if ((DataContext as LibraryModel)?.Note is { } n) n.ShowTranscript = false;
    }

    void OnTranscriptTab(object? sender, PointerPressedEventArgs e)
    {
        if ((DataContext as LibraryModel)?.Note is { } n) n.ShowTranscript = true;
    }
}
