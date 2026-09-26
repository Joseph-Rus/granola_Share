using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>A published release: where its page is, and every asset it holds by file name (the four installers and
/// SHA256SUMS.txt - see <see cref="Updates.Installers"/>).</summary>
public sealed record Release(string Tag, int[] Version, string Url, string Page, IReadOnlyDictionary<string, string>? Assets = null);

/// <summary>Reading a checksums file (`shasum -a 256` format: `<hex>  name`, or `<hex> *name` for a binary-mode
/// entry): file name to lowercase hex digest.</summary>
public static class Checksums
{
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int sp = line.IndexOf(' ');
            if (sp < 0) continue;
            string hex = line[..sp].Trim().ToLowerInvariant();
            string name = line[(sp + 1)..].TrimStart(' ', '*').Trim();
            if (hex.Length > 0 && name.Length > 0 && hex.All(Uri.IsHexDigit)) map[name] = hex;
        }
        return map;
    }
}

/// <summary>New releases on GitHub, and finding the right installer by name (D2); installing one is Updater.cs.</summary>
public static partial class Updates
{
    public const string RepoSlug = "Joseph-Rus/study-stash";
    public const string LatestApi = $"https://api.github.com/repos/{RepoSlug}/releases/latest";

    // D2's four installers, plus the checksums that cover them.
    public const string MacLaptopAsset = "Study-Stash-Laptop.dmg";
    public const string MacLibraryAsset = "Study-Stash-Library.dmg";
    public const string WindowsLaptopAsset = "Study-Stash-Laptop-Setup.exe";
    public const string WindowsLibraryAsset = "Study-Stash-Library-Setup.exe";
    public const string ChecksumsAsset = "SHA256SUMS.txt";

    public static readonly IReadOnlyList<string> Installers = [MacLaptopAsset, MacLibraryAsset, WindowsLaptopAsset, WindowsLibraryAsset];

    /// <summary>The installer this computer's role downloads, or null when this system doesn't update itself (Linux)
    /// or the role isn't laptop or library.</summary>
    public static string? Installer(string system, string? role) => (system, role) switch
    {
        ("Darwin", "laptop") => MacLaptopAsset,
        ("Darwin", "library") => MacLibraryAsset,
        ("Windows", "laptop") => WindowsLaptopAsset,
        ("Windows", "library") => WindowsLibraryAsset,
        _ => null,
    };

    public static string ArchiveUrl(string reference, bool branch = false) =>
        $"https://github.com/{RepoSlug}/archive/refs/{(branch ? "heads" : "tags")}/{reference}.tar.gz";

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    /// <summary>"v0.4.4" → [0, 4, 4]; at most three numbers, and [0] when there are none.</summary>
    public static int[] ParseVersion(string? v)
    {
        var nums = Digits().Matches((v ?? "").Split('+')[0]).Take(3)
            .Select(m => int.TryParse(m.Value, out int n) ? n : int.MaxValue).ToArray();
        return nums.Length > 0 ? nums : [0];
    }

    /// <summary>Python's tuple comparison: element by element, and a shorter prefix is smaller.</summary>
    public static int Compare(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("study-stash/" + Engine.Version); // GitHub turns away requests without one
        return http;
    }

    /// <summary>The newest published release, or null when there is none yet.</summary>
    public static async Task<Release?> LatestAsync(HttpClient? http = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var r = await (http ?? Http).SendAsync(request);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        var data = Py.JsonLoads(await r.Content.ReadAsStringAsync()) as JsonObject ?? throw new JsonException("not a release");
        string tag = Py.Str(data["tag_name"] ?? throw new JsonException("a release with no tag"));
        var assets = new Dictionary<string, string>();
        foreach (var a in data["assets"] as JsonArray ?? new JsonArray())
            if (a is JsonObject o) assets[Py.Str(o["name"])] = Py.Truthy(o["browser_download_url"]) ? Py.Str(o["browser_download_url"]) : "";
        return new Release(tag, ParseVersion(tag), ArchiveUrl(tag), Py.Truthy(data["html_url"]) ? Py.Str(data["html_url"]) : "", assets);
    }

    static readonly SemaphoreSlim CacheGate = new(1, 1);
    static DateTime cachedAt = DateTime.MinValue;
    static Release? cached;

    /// <summary>For the web pages: at most one GitHub call an hour, and never an exception.</summary>
    public static async Task<Release?> CachedLatestAsync(double maxAgeSeconds = 3600)
    {
        await CacheGate.WaitAsync();
        try
        {
            if ((DateTime.UtcNow - cachedAt).TotalSeconds > maxAgeSeconds)
            {
                try
                {
                    cached = await LatestAsync();
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
                {
                }
                cachedAt = DateTime.UtcNow;
            }
            return cached;
        }
        finally
        {
            CacheGate.Release();
        }
    }

    public static bool IsNewer(Release? release, string? current = null) =>
        release is not null && Compare(release.Version, ParseVersion(current ?? Engine.Version)) > 0;
}
