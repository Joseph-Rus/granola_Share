using StudyStash.Core;

namespace StudyStash.Library;

/// <summary>`client open` (client_app.open_app): make sure the laptop's background service runs, then show its page, in
/// the Study Stash app when it's installed, else in its own browser window.</summary>
public static class LaptopApp
{
    /// <summary>The Study Stash app, when it's installed: the Mac app, or the Windows one.</summary>
    public static string? NativeApp(string system, AppPlaces at) => system switch
    {
        "Darwin" => Apps.NativeInstalled(at),
        "Windows" => Apps.WindowsAppExe(Apps.AppName, at),
        _ => null,
    };

    /// <summary>The icon file the menu entry shows, written into the laptop's folder: this engine carries it inside.</summary>
    static string? IconFile(string home, string system)
    {
        string resource = system == "Windows" ? "study-stash.ico" : "icon.png";
        if (system == "Darwin" || Icons.Bytes(resource) is not byte[] bytes) return null;
        string path = Path.Combine(home, resource);
        try
        {
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The first install on a Mac or PC also gets the Study Stash app from the newest release, if it has one.</summary>
    static async Task InstallNativeApp(string system, AppPlaces at, Action<string> log)
    {
        Release? rel;
        try
        {
            rel = await Updates.LatestAsync();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return;
        }
        if (rel is null) return;
        if (system == "Darwin" && rel.MacApp.Length > 0) await Apps.InstallNativeAsync(rel.MacApp, log, at: at);
        else if (system == "Windows" && rel.WindowsApp.Length > 0) await Apps.InstallWindowsAppAsync(rel.WindowsApp, log, Apps.WindowsAppDir(Apps.AppName, at));
    }

    /// <summary>`browser: false` only starts it: that's what the app itself runs. The page's address, or null when the
    /// service didn't start.</summary>
    public static async Task<string?> OpenAsync(string home, bool install = false, bool browser = true, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        string system = Machine.Platform;
        var at = AppPlaces.Default;
        if (install)
        {
            if (system is "Darwin" or "Windows" && NativeApp(system, at) is null) await InstallNativeApp(system, at, log);
            Autostart.Install("client", home, ServicePlaces.Default, Machine.Run);
            Launcher.Install(home, system, Machine.Run, at, icon: IconFile(home, system));
        }
        else
        {
            string status = Autostart.Status("client");
            if (status == "missing") Autostart.Install("client", home, ServicePlaces.Default, Machine.Run);
            else if (status == "stopped") Autostart.Restart("client", ServicePlaces.Default, Machine.Run);
        }
        string? url = await LaptopWeb.WaitForAppAsync(home);
        if (url is null)
        {
            log($"Study Stash didn't start. See {Path.Combine(home, "logs", "client.log")}, or run `granola-share doctor`.");
            return null;
        }
        if (browser && (NativeApp(system, at) is not string native || !Dialogs.OpenApp(native)))
            Dialogs.OpenWindow(url); // no app yet (or Linux): its own browser window, with the Study Stash icon
        return url;
    }
}
