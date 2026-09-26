namespace StudyStash.Core;

/// <summary>Hands a link to the system: a web page, or a settings pane on a Mac or Windows.</summary>
public static class Dialogs
{
    /// <summary>Open a web page or a System Settings pane with the system's default handler.</summary>
    public static void OpenUrl(string url, Runner? run = null, string? system = null)
    {
        run ??= Machine.Run;
        system ??= Machine.Platform;
        try
        {
            if (system == "Darwin") run("open", [url], TimeSpan.FromSeconds(30));
            else if (system == "Windows") Machine.Open(url);
            else run("xdg-open", [url], TimeSpan.FromSeconds(30));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
