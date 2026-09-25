using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using StudyStash.App.ViewModels;

namespace StudyStash.App.Views;

/// <summary>The quick panel's keys, the same in both looks: ↑↓ move, Return opens, ⌘Return (Ctrl+Enter) asks, Escape
/// closes, ⌘C (Ctrl+C) copies the answer.</summary>
public static class QuickKeys
{
    public static void Attach(UserControl view, TextBox box)
    {
        box.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
        {
            if (view.DataContext is not QuickModel m) return;
            bool command = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
            switch (e.Key)
            {
                case Key.Down:
                    m.Move(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    m.Move(-1);
                    e.Handled = true;
                    break;
                case Key.Enter when command:
                    e.Handled = true;
                    await m.AskCommand.ExecuteAsync(null);
                    break;
                case Key.Enter:
                    e.Handled = true;
                    m.OpenCommand.Execute(null);
                    break;
                case Key.Escape:
                    e.Handled = true;
                    m.CloseCommand.Execute(null);
                    break;
                case Key.C when command && m.Answering && string.IsNullOrEmpty(box.SelectedText):
                    e.Handled = true;
                    if (TopLevel.GetTopLevel(view)?.Clipboard is { } clip) await clip.SetTextAsync(m.Answer);
                    break;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
}
