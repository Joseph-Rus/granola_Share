using Avalonia.Controls;
using Avalonia.Input;
using StudyStash.App.ViewModels;

namespace StudyStash.App.Views;

public partial class WinLibrary : UserControl
{
    public WinLibrary()
    {
        InitializeComponent();
        Fades.Under(Fade, "Mica", 0.75);
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
