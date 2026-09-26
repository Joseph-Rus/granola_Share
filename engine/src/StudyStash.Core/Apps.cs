using System.Xml;
using System.Xml.Linq;

namespace StudyStash.Core;

/// <summary>
/// Where the Study Stash app lives on this computer, and which role it was installed as (D3): a Mac bundle
/// (Study Stash.app) or a Windows install folder. What the updater (Updates.cs) needs to find and replace it.
/// </summary>
public static class Apps
{
    public const string AppName = "Study Stash";

    // --- the role preset (D3): what setup and the updater start from -----------------------------------------------

    static readonly XmlReaderSettings PlistReaderSettings = new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };

    /// <summary>The &lt;string&gt; that follows &lt;key&gt;key&lt;/key&gt; in a plist, or null: no such key, the value
    /// isn't a string, or the file isn't a plist at all (a garbled Info.plist should never throw, only answer null).</summary>
    internal static string? PlistString(string path, string key)
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

    /// <summary>The version a Windows install's study-stash.ini says it is (both Setup.exe write it), or null.</summary>
    internal static string? WindowsIniVersion(string baseDir) => IniValue(Path.Combine(baseDir, "study-stash.ini"), "app", "version");

    /// <summary>D4's "is this an installed copy" check on a Mac: inside a *.app that says com.study-stash.app, not
    /// still sitting in Gatekeeper's quarantine translocation folder, with a writable parent (so an update can swap
    /// it). The bundle path, or null with `problem` set to why - a build folder, someone else's app, a translocated
    /// one, or one whose folder this account can't write to never updates itself.</summary>
    public static string? MacBundleOf(string? baseDir, out string problem)
    {
        baseDir ??= AppContext.BaseDirectory;
        problem = "not-installed";
        if (EnclosingMacBundle(baseDir) is not { } app) return null;
        if (app.Contains("/AppTranslocation/", StringComparison.Ordinal))
        {
            problem = "translocated";
            return null;
        }
        if (PlistString(Path.Combine(app, "Contents", "Info.plist"), "CFBundleIdentifier") != "com.study-stash.app") return null;
        if (Path.GetDirectoryName(app) is not { } parent || !Machine.Writable(parent))
        {
            problem = "unwritable";
            return null;
        }
        return app;
    }

    public static string? MacBundleOf(string? baseDir = null) => MacBundleOf(baseDir, out _);

    /// <summary>D4's "is this an installed copy" check on Windows: a folder with an uninstaller beside the exe (the
    /// Setup.exe wrote one at install time). The folder, or null for a build folder.</summary>
    public static string? WindowsInstallOf(string? baseDir = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        return File.Exists(Path.Combine(baseDir, "unins000.exe")) ? baseDir : null;
    }

    /// <summary>Swap `fresh` in for `current` (a Mac bundle an update just downloaded next to it): `current` becomes
    /// "…name.app.old", `fresh` becomes `current`, then the old one is deleted; if moving `fresh` in fails, the old
    /// bundle goes back first, so `current` is never left missing.</summary>
    public static void SwapMacBundle(string current, string fresh, Runner run)
    {
        string old = current + ".old";
        TryDeleteFolder(old);
        Ready.MoveFolder(current, old, run);
        try
        {
            Ready.MoveFolder(fresh, current, run);
        }
        catch
        {
            Ready.MoveFolder(old, current, run);
            throw;
        }
        TryDeleteFolder(old);
    }

    internal static void TryDeleteFolder(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
