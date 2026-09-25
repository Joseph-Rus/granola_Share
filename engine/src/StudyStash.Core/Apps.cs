using System.IO.Compression;

namespace StudyStash.Core;

/// <summary>Where the apps and their icons go: the Mac's two Applications folders, Windows' per-account programs and
/// Start Menu folders, and the home folder (Linux keeps its menu entries there). Tests point these at a folder of their
/// own.</summary>
public sealed record AppPlaces(string SystemApps, string PersonalApps, string LocalAppData, string RoamingAppData, string Home)
{
    public static AppPlaces Default => new("/Applications", Path.Combine(Py.UserHome(), "Applications"),
        Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local ? local : Path.Combine(Py.UserHome(), "AppData", "Local"),
        Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } roaming ? roaming : Py.UserHome(), Py.UserHome());

    /// <summary>Everything under one folder, for tests.</summary>
    public static AppPlaces Under(string root) => new(Path.Combine(root, "Applications"), Path.Combine(root, "home", "Applications"),
        Path.Combine(root, "Local"), Path.Combine(root, "Roaming"), Path.Combine(root, "home"));
}

/// <summary>
/// The Study Stash apps (launcher.py's part that updates them): the Mac app and Study Stash Library in Applications,
/// and the Windows app in its folder. An update installs the new ones next to the engine.
/// </summary>
public static class Apps
{
    public const string AppName = "Study Stash";
    /// <summary>The same app, as the library computer's own window.</summary>
    public const string LibraryAppName = AppName + " Library";
    /// <summary>What 0.2 called it: replaced and removed on the next install.</summary>
    public static readonly string[] OldNames = ["Granola Share"];
    /// <summary>The native app's program; the script launcher's is granola-share-app.</summary>
    public const string NativeExe = "Study Stash";

    // --- the Mac -------------------------------------------------------------------------------------------------

    /// <summary>Every place the app may be: today's name first, then older names, in both Applications folders.</summary>
    public static List<string> MacAppPaths(AppPlaces? at = null)
    {
        at ??= AppPlaces.Default;
        return [.. new[] { AppName }.Concat(OldNames).SelectMany(name => new[] { Path.Combine(at.SystemApps, $"{name}.app"), Path.Combine(at.PersonalApps, $"{name}.app") })];
    }

    /// <summary>/Applications when this account can write there (admins can), so it's in Finder's Applications;
    /// otherwise ~/Applications. Spotlight and Launchpad find it in either.</summary>
    public static string MacAppFolder(AppPlaces? at = null)
    {
        at ??= AppPlaces.Default;
        return Machine.Writable(at.SystemApps) ? at.SystemApps : at.PersonalApps;
    }

    public static bool IsNative(string app) =>
        new[] { NativeExe }.Concat(OldNames).Any(exe => File.Exists(Path.Combine(app, "Contents", "MacOS", exe)));

    public static string? NativeInstalled(AppPlaces? at = null) => MacAppPaths(at).FirstOrDefault(IsNative);

    public static List<string> MacLibraryAppPaths(AppPlaces? at = null)
    {
        at ??= AppPlaces.Default;
        return [Path.Combine(at.SystemApps, $"{LibraryAppName}.app"), Path.Combine(at.PersonalApps, $"{LibraryAppName}.app")];
    }

    /// <summary>Study Stash Library, the library computer's own app (Study-Stash-Library.dmg), if it's here.</summary>
    public static string? LibraryAppInstalled(AppPlaces? at = null) => MacLibraryAppPaths(at).FirstOrDefault(IsNative);

    static async Task<string> FetchAsync(string url, string dest, HttpClient? http)
    {
        using var r = await (http ?? DownloadHttp).GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        r.EnsureSuccessStatusCode();
        await using var file = File.Create(dest);
        await r.Content.CopyToAsync(file);
        return dest;
    }

    static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>
    /// Download the Mac app (a zip from the release) into Applications, replacing any older copy. Fetched here rather
    /// than in a browser, macOS doesn't quarantine it, so it opens without a warning.
    /// </summary>
    public static async Task<string?> InstallNativeAsync(string url, Action<string> log, HttpClient? http = null, Runner? run = null,
        string name = AppName, AppPlaces? at = null)
    {
        run ??= Machine.Run;
        try
        {
            using var tmp = new Ready.TempFolder();
            string zip = await FetchAsync(url, Path.Combine(tmp.Path, "app.zip"), http);
            string unpacked = Path.Combine(tmp.Path, "unpacked");
            var p = run("ditto", ["-x", "-k", zip, unpacked], TimeSpan.FromMinutes(5));
            string fresh = Path.Combine(unpacked, $"{name}.app");
            if (p is not { ExitCode: 0 } || !IsNative(fresh))
            {
                log($"The {name} app in that release didn't unpack; keeping the one you have.");
                return null;
            }
            var olds = name == AppName ? MacAppPaths(at) : MacLibraryAppPaths(at);
            string dest = Path.Combine(MacAppFolder(at), $"{name}.app");
            foreach (string old in olds)
                if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            Ready.MoveFolder(fresh, dest, run);
            log($"Installed the {name} app in {Path.GetDirectoryName(dest)}.");
            return dest;
        }
        catch (Exception e)
        {
            log($"Couldn't install the {name} app ({e.Message}); the one in Applications still works.");
            return null;
        }
    }

    // --- Windows ---------------------------------------------------------------------------------------------------

    /// <summary>Where the Setup.exe installers and updates put the Windows app: Study Stash (the laptop's) or Study
    /// Stash Library (the library computer's).</summary>
    public static string WindowsAppDir(string name = AppName, AppPlaces? at = null) =>
        Path.Combine((at ?? AppPlaces.Default).LocalAppData, "Programs", name);

    public static string? WindowsAppExe(string name = AppName, AppPlaces? at = null)
    {
        string exe = Path.Combine(WindowsAppDir(name, at), $"{AppName}.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>Each installed copy's folder: an update refreshes them all.</summary>
    public static List<string> WindowsAppsInstalled(AppPlaces? at = null) =>
        [.. new[] { AppName, LibraryAppName }.Where(n => WindowsAppExe(n, at) is not null).Select(n => WindowsAppDir(n, at))];

    /// <summary>
    /// Download the Windows app (a zip from the release) into its folder. Windows won't overwrite a program that's
    /// running, but it lets one be renamed, so a file in use moves aside to *.old first (the app deletes those the next
    /// time it starts). Fetched here rather than in a browser, it isn't marked as downloaded, so SmartScreen doesn't ask.
    /// </summary>
    public static async Task<string?> InstallWindowsAppAsync(string url, Action<string> log, string dest, HttpClient? http = null)
    {
        try
        {
            using var tmp = new Ready.TempFolder();
            string zip = await FetchAsync(url, Path.Combine(tmp.Path, "app.zip"), http);
            string fresh = Path.Combine(tmp.Path, "app");
            ZipFile.ExtractToDirectory(zip, fresh);
            if (!File.Exists(Path.Combine(fresh, $"{AppName}.exe")))
            {
                log("The Study Stash app in that release didn't unpack; keeping the one you have.");
                return null;
            }
            foreach (string f in Directory.EnumerateFiles(fresh, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                string target = Path.Combine(dest, Path.GetRelativePath(fresh, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    try
                    {
                        File.Delete(target);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) // in use
                    {
                        string aside = target + ".old";
                        try
                        {
                            File.Delete(aside);
                        }
                        catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                        {
                            aside = $"{target}.{Environment.ProcessId}.old";
                        }
                        File.Move(target, aside);
                    }
                }
                File.Copy(f, target);
            }
            log($"Installed the Study Stash app in {dest}.");
            return Path.Combine(dest, $"{AppName}.exe");
        }
        catch (Exception e)
        {
            log($"Couldn't install the Study Stash app ({e.Message}); the Start Menu entry still opens Study Stash.");
            return null;
        }
    }
}

/// <summary>
/// The "Study Stash" icon (launcher.py): an app in Applications (a Mac), a Start Menu entry (Windows), or a menu entry
/// (Linux). It opens the native Study Stash app when that's installed; before then it runs `client open`, which starts
/// the background service if needed and shows its page.
/// </summary>
public static class Launcher
{
    public const string BundleId = "com.granola-share.app";

    public static List<string> Command(string home, IReadOnlyList<string>? engine = null) =>
        [.. engine ?? Autostart.EngineCommand(), "--home", home, "client", "open"];

    public static string WindowsShortcutPath(AppPlaces at, string name = Apps.AppName) =>
        Path.Combine(at.RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", $"{name}.lnk");

    public static string LinuxDesktopPath(AppPlaces at) => Path.Combine(at.Home, ".local", "share", "applications", "granola-share.desktop");

    static string Xml(string s) => s.Replace("&", "&amp;").Replace(">", "&gt;").Replace("<", "&lt;");

    public static string RenderInfoPlist() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>CFBundleName</key><string>{Xml(Apps.AppName)}</string>
            <key>CFBundleDisplayName</key><string>{Xml(Apps.AppName)}</string>
            <key>CFBundleIdentifier</key><string>{BundleId}</string>
            <key>CFBundleExecutable</key><string>granola-share-app</string>
            <key>CFBundlePackageType</key><string>APPL</string>
            <key>CFBundleShortVersionString</key><string>1.0</string>
            <key>LSMinimumSystemVersion</key><string>11.0</string>
            <key>LSUIElement</key><true/>
        </dict>
        </plist>

        """.ReplaceLineEndings("\n");

    public static string RenderMacScript(IReadOnlyList<string> args) =>
        "#!/bin/sh\n# Opens the Study Stash page (starting its background service if needed).\nexec "
        + string.Join(" ", args.Select(a => "'" + a.Replace("'", "'\\''") + "'")) + "\n";

    static string Ps(string s) => s.Replace("'", "''");

    /// <summary>Put the icon in place. Returns where, or null when that didn't work. `icon` is a .ico file (Windows) or
    /// a .png (Linux) for the menu entry.</summary>
    public static string? Install(string home, string system, Runner run, AppPlaces at, IReadOnlyList<string>? engine = null, string? icon = null)
    {
        var args = Command(home, engine);
        try
        {
            if (system == "Darwin")
            {
                if (Apps.NativeInstalled(at) is string native) // the real app is there: never swap it for the script launcher
                {
                    foreach (string other in Apps.MacAppPaths(at))
                        if (other != native && !Apps.IsNative(other) && Directory.Exists(other)) Directory.Delete(other, recursive: true);
                    return native;
                }
                string app = Path.Combine(Apps.MacAppFolder(at), $"{Apps.AppName}.app");
                Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
                Py.WriteText(Path.Combine(app, "Contents", "Info.plist"), RenderInfoPlist());
                string exe = Path.Combine(app, "Contents", "MacOS", "granola-share-app");
                Py.WriteText(exe, RenderMacScript(args));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(exe, (UnixFileMode)Convert.ToInt32("755", 8));
                foreach (string other in Apps.MacAppPaths(at)) // one copy only: 0.2.0 used ~/Applications
                    if (other != app && Directory.Exists(other)) Directory.Delete(other, recursive: true);
                return app;
            }
            if (system == "Windows")
            {
                string link = WindowsShortcutPath(at);
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                foreach (string old in Apps.OldNames) File.Delete(WindowsShortcutPath(at, old));
                string target, arguments, iconLine, style = "";
                if (Apps.WindowsAppExe(Apps.AppName, at) is string appExe) // the real app is there: the Start Menu opens it
                {
                    (target, arguments, iconLine) = (appExe, "", $"$s.IconLocation='{Ps(appExe)},0';");
                }
                else
                {
                    target = args[0];
                    arguments = Py.List2CmdLine(args.Skip(1)).Replace("'", "''");
                    iconLine = icon is not null && File.Exists(icon) ? $"$s.IconLocation='{Ps(icon)},0';" : "";
                    style = "$s.WindowStyle=7;"; // this engine is a console program: its window only flashes, minimized
                }
                string ps = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{Ps(link)}');"
                    + $"$s.TargetPath='{Ps(target)}';$s.Arguments='{arguments}';{iconLine}{style}"
                    + "$s.Description='Open Study Stash';$s.Save()";
                run("powershell", ["-NoProfile", "-Command", ps], TimeSpan.FromSeconds(60));
                return link;
            }
            string desktop = LinuxDesktopPath(at);
            Directory.CreateDirectory(Path.GetDirectoryName(desktop)!);
            string execLine = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            Py.WriteText(desktop, $"[Desktop Entry]\nType=Application\nName={Apps.AppName}\n"
                + $"Comment=Send your Granola lectures to your library\nExec={execLine}\n"
                + $"Icon={icon ?? ""}\nTerminal=false\nCategories=Office;Education;\n");
            return desktop;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Take the icon away again (best effort). On Windows the app's own uninstaller runs, when it has one.</summary>
    public static void Uninstall(string system, AppPlaces at)
    {
        try
        {
            if (system == "Darwin")
            {
                foreach (string app in Apps.MacAppPaths(at))
                    if (Directory.Exists(app)) Directory.Delete(app, recursive: true);
            }
            else if (system == "Windows")
            {
                foreach (string name in new[] { Apps.AppName }.Concat(Apps.OldNames)) File.Delete(WindowsShortcutPath(at, name));
                string folder = Apps.WindowsAppDir(Apps.AppName, at);
                string uninstaller = Path.Combine(folder, "unins000.exe"); // put there by the Setup.exe installers
                if (File.Exists(uninstaller))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(uninstaller) { UseShellExecute = true };
                    foreach (string a in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART" }) psi.ArgumentList.Add(a);
                    using var _ = System.Diagnostics.Process.Start(psi);
                }
                else if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            else
            {
                File.Delete(LinuxDesktopPath(at));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public static bool Installed(string system, AppPlaces at) => system switch
    {
        "Darwin" => Apps.MacAppPaths(at).Any(Directory.Exists),
        "Windows" => File.Exists(WindowsShortcutPath(at)),
        _ => File.Exists(LinuxDesktopPath(at)),
    };
}
