using Avalonia.Controls.ApplicationLifetimes;

namespace StudyStash.App;

/// <summary>The running app: the menu bar or tray icon, and the windows it opens.</summary>
public static partial class Shell
{
    public static void Start(App app, IClassicDesktopStyleApplicationLifetime desktop)
    {
        desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
    }
}
