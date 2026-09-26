using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace StudyStash.Core;

/// <summary>The library answered with an error: its status, in httpx's words.</summary>
public sealed class LibraryRefusedException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>The laptop's side of the library's API: send a lecture, ask whether it's filed.</summary>
public static class LibraryHttp
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(180) };

    /// <summary>httpx's raise_for_status message, so what's saved and shown reads as it did.</summary>
    static LibraryRefusedException Refused(HttpResponseMessage r, string url)
    {
        int code = (int)r.StatusCode;
        string kind = (code / 100) switch { 1 => "Informational response", 3 => "Redirect response", 4 => "Client error", _ => "Server error" };
        return new LibraryRefusedException(code, $"{kind} '{code} {r.ReasonPhrase}' for url '{url}'\n"
            + $"For more information check: https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/{code}");
    }

    public static async Task<JsonObject> PostAsync(string url, string json, string poolKey, HttpClient? http = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + poolKey);
        using var r = await (http ?? Http).SendAsync(request);
        if (!r.IsSuccessStatusCode) throw Refused(r, url);
        return Py.JsonLoads(await r.Content.ReadAsStringAsync()) as JsonObject ?? [];
    }

    /// <summary>Null when the library doesn't know the note (or is too old to answer).</summary>
    public static async Task<JsonObject?> GetAsync(string url, string poolKey, HttpClient? http = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + poolKey);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var r = await (http ?? Http).SendAsync(request, cts.Token);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        if (!r.IsSuccessStatusCode) throw Refused(r, url);
        return Py.JsonLoads(await r.Content.ReadAsStringAsync()) as JsonObject ?? [];
    }
}

/// <summary>How the laptop reaches its library, so tests can answer instead: send a lecture, ask after it, and the
/// clock that paces the retries.</summary>
public sealed class LaptopHost
{
    /// <summary>(url, JSON body, library password) → the library's answer.</summary>
    public Func<string, string, string, Task<JsonObject>> Post { get; init; } = (url, body, key) => LibraryHttp.PostAsync(url, body, key);
    /// <summary>(url, library password) → the answer, or null when the library doesn't know it.</summary>
    public Func<string, string, Task<JsonObject?>> Get { get; init; } = (url, key) => LibraryHttp.GetAsync(url, key);
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
}
