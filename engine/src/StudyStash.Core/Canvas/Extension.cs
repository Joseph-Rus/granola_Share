using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Canvas;

/// <summary>
/// The Chrome extension that reads Canvas with the person's own sign-in and hands it to the library. Its files are
/// inside the engine; <see cref="Prepare"/> writes them out as a folder Chrome loads ("Load unpacked"), with a
/// config.js that says where the library is and holds the extension's key. The extension reloads itself when this
/// folder has a newer version than the one running.
/// </summary>
public static class Extension
{
    static readonly System.Reflection.Assembly Here = typeof(Extension).Assembly;

    static IEnumerable<string> Files() =>
        Here.GetManifestResourceNames().Where(n => n.StartsWith("extension/", StringComparison.Ordinal)).Select(n => n["extension/".Length..]);

    static byte[] Read(string name)
    {
        using var s = Here.GetManifestResourceStream("extension/" + name)!;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>The version in this engine's copy ("1.0").</summary>
    public static string Version() =>
        JsonNode.Parse(Read("manifest.json"))?["version"]?.GetValue<string>() ?? "";

    /// <summary>Where Chrome loads it from on this computer.</summary>
    public static string Folder(string home) => Path.Combine(home, "chrome-extension");

    /// <summary>
    /// Write the folder: the extension's files, a manifest that may reach only this Canvas and this library, and
    /// config.js. <paramref name="library"/> is the library's address as this computer reaches it.
    /// </summary>
    public static string Prepare(string dir, string library, string key, string canvasUrl)
    {
        Directory.CreateDirectory(dir);
        foreach (string f in Files())
            if (f != "manifest.json") File.WriteAllBytes(Path.Combine(dir, f), Read(f));
        var manifest = JsonNode.Parse(Read("manifest.json"))!.AsObject();
        var origin = new Uri(library);
        manifest["host_permissions"] = new JsonArray(canvasUrl.TrimEnd('/') + "/*", "https://*.inscloudgate.net/*", $"{origin.Scheme}://{origin.Host}:{origin.Port}/*");
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        var config = new JsonObject { ["app"] = library.TrimEnd('/'), ["key"] = key, ["canvas"] = canvasUrl.TrimEnd('/') };
        string cfgPath = Path.Combine(dir, "config.js");
        File.WriteAllText(cfgPath, $"const STUDY_STASH = {config.ToJsonString()};\n");
        Py.OwnerOnly(cfgPath);
        return dir;
    }
}
