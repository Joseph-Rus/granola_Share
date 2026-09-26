using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

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

    // --- the role preset (D3): what setup and the updater start from ---------------------------------------------

    static readonly XmlReaderSettings PlistReaderSettings = new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };

    /// <summary>The &lt;string&gt; that follows &lt;key&gt;key&lt;/key&gt; in a plist, or null: no such key, the value
    /// isn't a string, or the file isn't a plist at all (a garbled Info.plist should never throw, only answer null).</summary>
    static string? PlistString(string path, string key)
    {
        try
        {
            using var reader = XmlReader.Create(path, PlistReaderSettings);
            var doc = XDocument.Load(reader);
            var value = doc.Descendants("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();
            return value?.Name.LocalName == "string" ? value.Value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Walking up from a folder inside the bundle (e.g. Contents/MacOS/arm64) to "Study Stash.app" itself, or
    /// null short of the root (a build folder isn't inside one).</summary>
    static string? EnclosingMacBundle(string baseDir)
    {
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
            if (dir.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return dir.FullName;
        return null;
    }

    /// <summary>A study-stash.ini value, case- and whitespace-insensitive on both the section and the key, tolerant of
    /// CRLF line endings, a leading BOM and a missing or unreadable file (all answer null, never throw).</summary>
    static string? IniValue(string path, string section, string key)
    {
        try
        {
            string? current = null;
            foreach (string raw in File.ReadAllText(path).Split('\n'))
            {
                string line = raw.Trim().TrimEnd('\r');
                if (line.Length == 0 || line[0] is ';' or '#') continue;
                if (line[0] == '[' && line[^1] == ']')
                {
                    current = line[1..^1].Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0 || !string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(line[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase)) return line[(eq + 1)..].Trim();
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static string? Role(string? value) => value?.Trim().ToLowerInvariant() switch { "laptop" => "laptop", "library" => "library", _ => null };

    /// <summary>Which role this copy was set up as, before the student's own choice in Settings takes over: a Mac
    /// bundle's Info.plist (StudyStashRole) or a Windows install's study-stash.ini ([app] role=). Null in a build
    /// folder, for an unknown value, or when the file is missing or garbled - setup then asks, same as always.</summary>
    public static string? RolePreset(string? baseDir = null, string? system = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        return (system ?? Machine.Platform) switch
        {
            "Darwin" => EnclosingMacBundle(baseDir) is { } app ? Role(PlistString(Path.Combine(app, "Contents", "Info.plist"), "StudyStashRole")) : null,
            "Windows" => Role(IniValue(Path.Combine(baseDir, "study-stash.ini"), "app", "role")),
            _ => null,
        };
    }

    /// <summary>D4's "is this an installed copy" check on a Mac: inside a *.app that says com.study-stash.app, not
    /// still sitting in Gatekeeper's quarantine translocation folder, with a writable parent (so an update can swap
    /// it). The bundle path, or null - a build folder, someone else's app, or a translocated one never updates itself.</summary>
    public static string? MacBundleOf(string? baseDir = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        if (EnclosingMacBundle(baseDir) is not { } app) return null;
        if (app.Contains("/AppTranslocation/", StringComparison.Ordinal)) return null;
        if (PlistString(Path.Combine(app, "Contents", "Info.plist"), "CFBundleIdentifier") != "com.study-stash.app") return null;
        return Path.GetDirectoryName(app) is { } parent && Machine.Writable(parent) ? app : null;
    }

    /// <summary>D4's "is this an installed copy" check on Windows: a folder with an uninstaller beside the exe (the
    /// Setup.exe wrote one at install time). The folder, or null for a build folder.</summary>
    public static string? WindowsInstallOf(string? baseDir = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        return File.Exists(Path.Combine(baseDir, "unins000.exe")) ? baseDir : null;
    }
}
