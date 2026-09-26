using Avalonia.Controls;
using StudyStash.App.ViewModels;

namespace StudyStash.App.Views;

public partial class MacAiAskChat : UserControl
{
    public MacAiAskChat()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AiAskModel m) m.CloseMenu = () => EngineChip.Flyout?.Hide();
        };
    }
}
