using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>A published release, and where its downloads are. Assets has every download by file name: this engine's
/// own come for each system (<see cref="Updates.EngineAsset"/>).</summary>
public sealed record Release(string Tag, int[] Version, string Url, string Page, string MacApp = "", string WindowsApp = "",
    string MacLibraryApp = "", string WindowsHelper = "", IReadOnlyDictionary<string, string>? Assets = null);

/// <summary>New releases on GitHub (update.py): finding them here, installing them in Updater.cs.</summary>
public static partial class Updates
{
    public const string RepoSlug = "Joseph-Rus/study-stash";
    public const string LatestApi = $"https://api.github.com/repos/{RepoSlug}/releases/latest";
    public const string MacAppAsset = "Study-Stash-mac.zip";
    public const string MacLibraryAppAsset = "Study-Stash-Library-mac.zip";
    public const string WindowsAppAsset = "Study-Stash-windows.zip";
    public const string WindowsHelperAsset = "Study-Stash-helper-windows.zip";

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

    public static string ArchiveUrl(string reference, bool branch = false) =>
        $"https://github.com/{RepoSlug}/archive/refs/{(branch ? "heads" : "tags")}/{reference}.tar.gz";

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
        string Asset(string name) => assets.GetValueOrDefault(name, "");
        return new Release(tag, ParseVersion(tag), ArchiveUrl(tag), Py.Truthy(data["html_url"]) ? Py.Str(data["html_url"]) : "",
            Asset(MacAppAsset), Asset(WindowsAppAsset), Asset(MacLibraryAppAsset), Asset(WindowsHelperAsset), assets);
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
