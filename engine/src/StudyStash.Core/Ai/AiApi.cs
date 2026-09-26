using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Ai;

// The wire shapes for /api/v2/ai/*, shared by the library (which serializes them) and the app (which reads them
// through AiRemote): one JsonSerializerOptions (AiApi.Options, snake_case) on both sides, so the JSON keys always agree.

public sealed record ModelOption(string Id, string Label);

/// <summary>state: ready | unchecked | not_installed | not_signed_in | not_running | model_missing | limited | failed</summary>
public sealed record EngineInfo(string Id, string Name, string State)
{
    public bool Installed { get; init; }
    public string Model { get; init; } = "";
    public List<ModelOption> Models { get; init; } = [];
    public string Site { get; init; } = "";
    public string Until { get; init; } = "";
    public string Why { get; init; } = "";
}

/// <summary>kind: engine_offline | not_signed_in | model_missing | usage_limit (the app adds fell_back,
/// library_offline, rewrite_failed and access_request itself: those never come from the library).</summary>
public sealed record AiProblemInfo(string Id, string Kind, string Engine, string EngineName)
{
    public string Until { get; init; } = "";
    public string Model { get; init; } = "";
    public double SizeGb { get; init; }
    public string FallbackTo { get; init; } = "";
}

public sealed record PullInfo(string Model, double Fraction, string Why);

public sealed record AiOverview(List<EngineInfo> Engines, string Notes, string Ask, bool Fallback, List<AiProblemInfo> Problems)
{
    public PullInfo? Pulling { get; init; }
}

/// <summary>What an action said, and the library's state right after doing it.</summary>
public sealed record AiSaid(string Said, AiOverview? Overview);

public sealed record AskRequest(string Question)
{
    public string? Lecture { get; init; }
    public string? Class { get; init; }
    public string? Engine { get; init; }
    public string? Live { get; init; }
    public string? LiveTitle { get; init; }
}

public sealed record AskSource(string? Id, string Title, string? Class, string? Date, double? At, string Section);

public sealed record AskReply(string Answer, List<AskSource> Sources, string Engine, string EngineName)
{
    public string Asked { get; init; } = "";
    public string AskedName { get; init; } = "";
    public bool FellBack { get; init; }
    public string Why { get; init; } = "";
}

public sealed record NotesVersion(string Markdown, string By, string At);

/// <summary>state: none | working | ready | failed | cancelled</summary>
public sealed record RewriteInfo(string Lecture, string State)
{
    public string Engine { get; init; } = "";
    public string EngineName { get; init; } = "";
    public string Started { get; init; } = "";
    public string Error { get; init; } = "";
    public int Done { get; init; }
    public int Parts { get; init; }
    public NotesVersion? Current { get; init; }
    public NotesVersion? Draft { get; init; }
}

public sealed record ReadingScopes(bool Lectures = true, bool Notes = true, bool Canvas = true, bool Audio = false);

public sealed record ToolConnection(string Id, string Name, string Kind)
{
    public double Created { get; init; }
    public double? LastUsed { get; init; }
}

public sealed record ToolAccessInfo(bool On, ReadingScopes Reading, List<ToolConnection> Connections)
{
    public string? PublicUrl { get; init; }
    public bool HasPassword { get; init; }
}

/// <summary>What the app's AI view models need from the library: engines and their state, the defaults, the
/// per-engine actions, and asking a question. Rewrite and tool-access members join this in later tasks. Every
/// member answers null when the library is too old to have the route (a plain 404, not one of ours).</summary>
public interface IAiLibrary
{
    Task<AiOverview?> EnginesAsync();
    Task<AiOverview?> DefaultsAsync(string? notes = null, string? ask = null, bool? fallback = null);
    Task<AiSaid?> StartAsync(string engine);
    Task<AiSaid?> DownloadAsync(string engine);
    Task<AiSaid?> SignInAsync(string engine);
    Task<AiSaid?> CheckAsync(string engine, string model = "");
    Task<AiOverview?> ModelAsync(string engine, string model);
    Task<AiOverview?> DismissAsync(string problemId);
    Task<AskReply?> AskAsync(AskRequest request);
    Task<RewriteInfo?> RewriteAsync(string lecture);
    Task<RewriteInfo?> RewriteStartAsync(string lecture, string engine);
    Task<RewriteInfo?> RewriteCancelAsync(string lecture);
    Task<RewriteInfo?> RewriteKeepAsync(string lecture);
    Task<RewriteInfo?> RewriteUseAsync(string lecture);
}

/// <summary>The library's AI over its API (/api/v2/ai), the way the Study Stash app reads it. Like
/// <see cref="RemoteLibrary"/>: a plain 404 with no JSON detail means an older library without this route (null,
/// not an error); one of our own refusals (400/409/503/401) throws <see cref="LibraryRefusedException"/> with its
/// words.</summary>
public sealed class AiRemote(string serverUrl, string key, HttpClient? http = null) : IAiLibrary
{
    /// <summary>snake_case both ways, so the library and the app agree on every key without translating by hand.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(150) };
    readonly HttpClient client = http ?? Shared;
    readonly string root = serverUrl.TrimEnd('/') + "/api/v2/ai";

    async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, root + path);
        if (key.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await client.SendAsync(request, ct);
        string text = await r.Content.ReadAsStringAsync(ct);
        JsonObject? asObject = text.Length > 0 && text.TrimStart().StartsWith('{') ? JsonNode.Parse(text) as JsonObject : null;
        if (r.StatusCode == HttpStatusCode.NotFound && asObject?["detail"] is null)
            return null; // an older library: this route doesn't exist there, not that nothing matched
        if (!r.IsSuccessStatusCode)
            throw new LibraryRefusedException((int)r.StatusCode, Py.AsString(asObject?["detail"]) ?? r.ReasonPhrase ?? "");
        return text.Length > 0 ? JsonNode.Parse(text) : null;
    }

    static T? As<T>(JsonNode? n) where T : class => n?.Deserialize<T>(Options);

    public async Task<AiOverview?> EnginesAsync() => As<AiOverview>(await SendAsync(HttpMethod.Get, "/engines"));

    public async Task<AiOverview?> DefaultsAsync(string? notes = null, string? ask = null, bool? fallback = null)
    {
        var body = new JsonObject();
        if (notes is not null) body["notes"] = notes;
        if (ask is not null) body["ask"] = ask;
        if (fallback is not null) body["fallback"] = fallback.Value;
        return As<AiOverview>(await SendAsync(HttpMethod.Post, "/defaults", body));
    }

    static string Seg(string s) => Uri.EscapeDataString(s);

    public async Task<AiSaid?> StartAsync(string engine) => As<AiSaid>(await SendAsync(HttpMethod.Post, $"/engines/{Seg(engine)}/start"));

    public async Task<AiSaid?> DownloadAsync(string engine) => As<AiSaid>(await SendAsync(HttpMethod.Post, $"/engines/{Seg(engine)}/download"));

    public async Task<AiSaid?> SignInAsync(string engine) => As<AiSaid>(await SendAsync(HttpMethod.Post, $"/engines/{Seg(engine)}/sign-in"));

    public async Task<AiSaid?> CheckAsync(string engine, string model = "") => As<AiSaid>(await SendAsync(HttpMethod.Post,
        $"/engines/{Seg(engine)}/check", model.Length > 0 ? new JsonObject { ["model"] = model } : null));

    public async Task<AiOverview?> ModelAsync(string engine, string model) =>
        As<AiOverview>(await SendAsync(HttpMethod.Post, $"/engines/{Seg(engine)}/model", new JsonObject { ["model"] = model }));

    public async Task<AiOverview?> DismissAsync(string problemId) =>
        As<AiOverview>(await SendAsync(HttpMethod.Post, $"/problems/{Seg(problemId)}/dismiss"));

    public async Task<AskReply?> AskAsync(AskRequest request) =>
        As<AskReply>(await SendAsync(HttpMethod.Post, "/ask", JsonSerializer.SerializeToNode(request, Options)));

    public async Task<RewriteInfo?> RewriteAsync(string lecture) =>
        As<RewriteInfo>(await SendAsync(HttpMethod.Get, $"/rewrite/{Seg(lecture)}"));

    public async Task<RewriteInfo?> RewriteStartAsync(string lecture, string engine) => As<RewriteInfo>(await SendAsync(
        HttpMethod.Post, $"/rewrite/{Seg(lecture)}", new JsonObject { ["engine"] = engine }));

    public async Task<RewriteInfo?> RewriteCancelAsync(string lecture) =>
        As<RewriteInfo>(await SendAsync(HttpMethod.Post, $"/rewrite/{Seg(lecture)}/cancel"));

    public async Task<RewriteInfo?> RewriteKeepAsync(string lecture) =>
        As<RewriteInfo>(await SendAsync(HttpMethod.Post, $"/rewrite/{Seg(lecture)}/keep"));

    public async Task<RewriteInfo?> RewriteUseAsync(string lecture) =>
        As<RewriteInfo>(await SendAsync(HttpMethod.Post, $"/rewrite/{Seg(lecture)}/use"));
}
