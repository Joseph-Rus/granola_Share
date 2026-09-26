using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Canvas;

/// <summary>
/// The Chrome extension that reads Canvas with the person's own sign-in and hands it to the library. Its files are
/// inside the engine; <see cref="Prepare"/> writes them out as a folder Chrome loads ("Load unpacked"), with a
/// config.js that says where the library is and holds the extension's key. When Study Stash updates,
/// <see cref="Refresh"/> brings that folder up to date, and the extension reloads itself from it the next time it
/// checks in.
/// </summary>
public static class Extension
{
    static readonly System.Reflection.Assembly Here = typeof(Extension).Assembly;

    /// <summary>Where Canvas keeps file bodies (a download redirects there): the extension may read them, and so may
    /// an AI's reads. One list for the extension's host permissions, its config.js and the library's own check.</summary>
    public static readonly IReadOnlyList<string> FileHosts = ["*.inscloudgate.net"];

    /// <summary>What the extension and the library say to each other. 2: the extension says when Chrome is signed
    /// out, passes on Canvas's rate limit, never hands over an error page or an oversized file as a file, reloads
    /// itself before taking work when its folder is newer, and posts files one at a time. The extension sends its
    /// own as <c>p</c>; config.js says which one the Study Stash that wrote the folder speaks.</summary>
    public const int Protocol = 2;

    static IEnumerable<string> Files() =>
        Here.GetManifestResourceNames().Where(n => n.StartsWith("extension/", StringComparison.Ordinal)).Select(n => n["extension/".Length..]);

    static byte[] Read(string name)
    {
        using var s = Here.GetManifestResourceStream("extension/" + name)!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    static readonly Lazy<string> Current = new(() => JsonNode.Parse(Read("manifest.json"))?["version"]?.GetValue<string>() ?? "");

    /// <summary>The version in this engine's copy ("1.3").</summary>
    public static string Version() => Current.Value;

    /// <summary>"1.2" is older than "1.3" (and "1.10" newer than "1.9"). False when either isn't a version.</summary>
    public static bool IsOlder(string version, string than) => Parse(version) is { } a && Parse(than) is { } b && a < b;

    static System.Version? Parse(string v) =>
        System.Version.TryParse(v.Contains('.', StringComparison.Ordinal) ? v : v + ".0", out var parsed) ? parsed : null;

    /// <summary>Whether a URL is on Canvas's file store: https, a host in <see cref="FileHosts"/>, nothing else in
    /// the address before the path.</summary>
    public static bool OnFileHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0 && u.IsDefaultPort
        && FileHosts.Any(h => h.StartsWith("*.", StringComparison.Ordinal)
            ? u.Host.EndsWith(h[1..], StringComparison.OrdinalIgnoreCase) : u.Host.Equals(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>Where Chrome loads it from on this computer.</summary>
    public static string Folder(string home) => Path.Combine(home, "chrome-extension");

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Write the folder: the extension's files, a manifest that may reach only this Canvas, its file store and this
    /// library, and config.js. <paramref name="library"/> is the library's address as this computer reaches it.
    /// </summary>
    public static string Prepare(string dir, string library, string key, string canvasUrl)
    {
        Directory.CreateDirectory(dir);
        foreach (string f in Files())
            if (f != "manifest.json") File.WriteAllBytes(Path.Combine(dir, f), Read(f));
        var origin = new Uri(library);
        var hosts = new JsonArray(canvasUrl.TrimEnd('/') + "/*");
        foreach (string h in FileHosts) hosts.Add($"https://{h}/*");
        hosts.Add($"{origin.Scheme}://{origin.Host}:{origin.Port}/*");
        WriteManifest(dir, hosts);
        var config = new JsonObject
        {
            ["app"] = library.TrimEnd('/'), ["key"] = key, ["canvas"] = canvasUrl.TrimEnd('/'),
            ["files"] = new JsonArray(FileHosts.Select(h => (JsonNode)h).ToArray()), ["protocol"] = Protocol,
        };
        string cfgPath = Path.Combine(dir, "config.js");
        File.WriteAllText(cfgPath, $"const STUDY_STASH = {config.ToJsonString()};\n");
        Py.OwnerOnly(cfgPath);
        return dir;
    }

    /// <summary>
    /// Bring a folder <see cref="Prepare"/> made (perhaps by an older Study Stash) up to this one: its scripts and
    /// pages are rewritten and its manifest takes this version, keeping where it may reach, and config.js (the key,
    /// the library's address) is left as it is. Chrome's running copy sees the new version on its next visit and
    /// reloads itself. A folder without manifest.json and config.js isn't one of ours and is left alone. True when
    /// the version changed.
    /// </summary>
    public static bool Refresh(string dir)
    {
        string manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath) || !File.Exists(Path.Combine(dir, "config.js"))) return false;
        JsonObject? old;
        try
        {
            old = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
        }
        catch (JsonException)
        {
            return false; // not a manifest Chrome could load either: Settings' "Make it" writes a fresh folder
        }
        if (old is null) return false;
        string before = old["version"] is JsonValue v && v.TryGetValue(out string? s) ? s : "";
        foreach (string f in Files())
        {
            if (f is "manifest.json" or "config.js") continue;
            byte[] now = Read(f);
            string path = Path.Combine(dir, f);
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(now)) File.WriteAllBytes(path, now);
        }
        WriteManifest(dir, old["host_permissions"] is JsonArray kept ? (JsonArray)kept.DeepClone() : []);
        return before != Version();
    }

    /// <summary>This engine's manifest with these host permissions, written only when it differs.</summary>
    static void WriteManifest(string dir, JsonArray hosts)
    {
        var manifest = JsonNode.Parse(Read("manifest.json"))!.AsObject();
        manifest["host_permissions"] = hosts;
        string path = Path.Combine(dir, "manifest.json"), text = manifest.ToJsonString(Indented) + "\n";
        if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
    }
}
