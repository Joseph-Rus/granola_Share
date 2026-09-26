using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.App.Services;

/// <summary>The library answered a Canvas call with an error: its status and, when the library sent one, the detail
/// it gave (e.g. "That isn't a web address.").</summary>
public sealed class CanvasLibraryException(int status, string detail) : Exception(detail)
{
    public int Status { get; } = status;
}

/// <summary>
/// The app's side of the library's Canvas API (<c>/api/v2/canvas</c> and <c>/api/v2/files</c>), Bearer-keyed like
/// <see cref="StudyStash.Core.RemoteLibrary"/>. A 404 means an older library that doesn't have the call (returns
/// null); any other failure throws <see cref="CanvasLibraryException"/> with the library's own detail when it sent
/// one. View models never talk to this directly — they read a <see cref="CanvasContext"/>.
/// </summary>
public sealed class CanvasClient(string serverUrl, string key, HttpClient? http = null)
{
    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(150) };
    readonly HttpClient client = http ?? Shared;
    readonly string canvasRoot = serverUrl.TrimEnd('/') + "/api/v2/canvas";
    readonly string filesRoot = serverUrl.TrimEnd('/') + "/api/v2/files";

    public string ServerUrl { get; } = serverUrl.TrimEnd('/');

    static string Q(string s) => Uri.EscapeDataString(s);

    async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, JsonNode? body, CancellationToken stop)
    {
        using var request = new HttpRequestMessage(method, url);
        if (key.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await client.SendAsync(request, stop);
    }

    /// <summary>Sends the request and reads its JSON body as <typeparamref name="T"/>: null on a 404 (an older
    /// library); <see cref="CanvasLibraryException"/> on any other failure.</summary>
    async Task<T?> SendAsync<T>(HttpMethod method, string url, JsonNode? body, CancellationToken stop) where T : class
    {
        using var r = await SendRawAsync(method, url, body, stop);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        string text = await r.Content.ReadAsStringAsync(stop);
        if (!r.IsSuccessStatusCode) throw new CanvasLibraryException((int)r.StatusCode, Detail(text) ?? r.ReasonPhrase ?? "");
        return text.Length == 0 ? null : JsonSerializer.Deserialize<T>(text, CanvasApi.Json);
    }

    static string? Detail(string text) =>
        text.Length > 0 && text[0] == '{' && JsonNode.Parse(text) is JsonObject o && o["detail"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    // ---- canvas/state, with the fallback to an old library's GET canvas ----

    public async Task<CanvasApi.State?> StateAsync(CancellationToken stop = default)
    {
        var s = await SendAsync<CanvasApi.State>(HttpMethod.Get, canvasRoot + "/state", null, stop);
        if (s is not null) return s;
        var old = await SendAsync<CanvasApi.Overview>(HttpMethod.Get, canvasRoot, null, stop);
        return old is null ? null : DeriveState(old);
    }

    static CanvasApi.State DeriveState(CanvasApi.Overview o)
    {
        string status =
            o.Url.Length == 0 ? "not_set_up" :
            o.ExtensionSeen.Length == 0 ? "no_extension" :
            o.NeedsLogin ? "signed_out" :
            o.Syncing ? "syncing" :
            o.Error.Length > 0 ? "error" :
            o.ExtensionUpdate is not null ? "updated" :
            "connected";
        DateTimeOffset? seen = DateTimeOffset.TryParse(o.ExtensionSeen, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
        return new CanvasApi.State
        {
            Status = status,
            Url = o.Url,
            Extension = new CanvasApi.ExtensionInfo { Seen = seen, Version = o.ExtensionVersion, Updated = o.ExtensionUpdate },
            LastSync = o.LastSync,
            PollMinutes = o.PollMinutes,
            Syncing = o.Syncing ? new CanvasApi.SyncingInfo { Left = o.Left } : null,
            Error = o.Error.Length > 0 ? new CanvasApi.ErrorInfo { Text = o.Error } : null,
        };
    }

    // ---- the rest of /api/v2/canvas ----

    public Task<CanvasApi.Overview?> OverviewAsync(CancellationToken stop = default) =>
        SendAsync<CanvasApi.Overview>(HttpMethod.Get, canvasRoot, null, stop);

    public Task<CanvasApi.Overview?> SaveAsync(string? url = null, IReadOnlyDictionary<string, double>? courses = null, bool? sync = null,
        int? pollMinutes = null, bool? dismissUpdate = null, CancellationToken stop = default)
    {
        var body = new JsonObject();
        if (url is not null) body["url"] = url;
        if (courses is not null) body["courses"] = new JsonObject(courses.Select(kv => KeyValuePair.Create<string, JsonNode?>(kv.Key, kv.Value)));
        if (sync is not null) body["sync"] = sync.Value;
        if (pollMinutes is not null) body["poll_minutes"] = pollMinutes.Value;
        if (dismissUpdate is not null) body["dismiss_update"] = dismissUpdate.Value;
        return SendAsync<CanvasApi.Overview>(HttpMethod.Post, canvasRoot, body, stop);
    }

    public Task<CanvasApi.ExtensionKey?> ExtensionAsync(CancellationToken stop = default) =>
        SendAsync<CanvasApi.ExtensionKey>(HttpMethod.Get, canvasRoot + "/extension", null, stop);

    public Task<CanvasApi.Overview?> FindCoursesAsync(CancellationToken stop = default) =>
        SendAsync<CanvasApi.Overview>(HttpMethod.Post, canvasRoot + "/courses", null, stop);

    public Task ScoutAsync(string cls, CancellationToken stop = default) =>
        SendAsync<JsonObject>(HttpMethod.Post, canvasRoot + "/scout", new JsonObject { ["class"] = cls }, stop);

    public Task<List<CanvasApi.ClassRow>?> ClassesAsync(CancellationToken stop = default) =>
        SendAsync<List<CanvasApi.ClassRow>>(HttpMethod.Get, canvasRoot + "/classes", null, stop);

    public Task<CanvasApi.DueResponse?> DueAsync(CancellationToken stop = default) =>
        SendAsync<CanvasApi.DueResponse>(HttpMethod.Get, canvasRoot + "/due", null, stop);

    public Task<CanvasApi.AssignmentsResponse?> AssignmentsAsync(string cls, CancellationToken stop = default) =>
        SendAsync<CanvasApi.AssignmentsResponse>(HttpMethod.Get, $"{canvasRoot}/assignments?class={Q(cls)}", null, stop);

    public Task<CanvasApi.AssignmentDetail?> AssignmentAsync(string cls, string id, CancellationToken stop = default) =>
        SendAsync<CanvasApi.AssignmentDetail>(HttpMethod.Get, $"{canvasRoot}/assignment?class={Q(cls)}&id={Q(id)}", null, stop);

    public Task<CanvasApi.ModulesResponse?> ModulesAsync(string cls, CancellationToken stop = default) =>
        SendAsync<CanvasApi.ModulesResponse>(HttpMethod.Get, $"{canvasRoot}/modules?class={Q(cls)}", null, stop);

    public Task<CanvasApi.FilesResponse?> FilesAsync(string cls, CancellationToken stop = default) =>
        SendAsync<CanvasApi.FilesResponse>(HttpMethod.Get, $"{canvasRoot}/files?class={Q(cls)}", null, stop);

    public Task<CanvasApi.AnnouncementsResponse?> AnnouncementsAsync(string cls, CancellationToken stop = default) =>
        SendAsync<CanvasApi.AnnouncementsResponse>(HttpMethod.Get, $"{canvasRoot}/announcements?class={Q(cls)}", null, stop);

    public Task MarkAnnouncementsSeenAsync(string cls, IReadOnlyList<string> ids, CancellationToken stop = default) =>
        SendAsync<JsonObject>(HttpMethod.Post, canvasRoot + "/announcements/seen", new JsonObject
        {
            ["class"] = cls, ["ids"] = new JsonArray([.. ids.Select(i => (JsonNode)i)]),
        }, stop);

    public Task<CanvasApi.NotificationsResponse?> NotificationsAsync(DateTimeOffset? after = null, CancellationToken stop = default) =>
        SendAsync<CanvasApi.NotificationsResponse>(HttpMethod.Get,
            canvasRoot + "/notifications" + (after is null ? "" : $"?after={Q(after.Value.ToString("O", CultureInfo.InvariantCulture))}"), null, stop);

    public Task MarkNotificationsSeenAsync(string upTo, CancellationToken stop = default) =>
        SendAsync<JsonObject>(HttpMethod.Post, canvasRoot + "/notifications/seen", new JsonObject { ["up_to"] = upTo }, stop);

    // ---- /api/v2/files (a lecture-style read of a Canvas file already saved locally) ----

    public async Task<string?> TextAsync(string cls, string path, CancellationToken stop = default)
    {
        var f = await SendAsync<CanvasApi.TextFile>(HttpMethod.Get, $"{filesRoot}?class={Q(cls)}&path={Q(path)}", null, stop);
        return f?.Text;
    }

    /// <summary>Downloads the file's bytes to <paramref name="destFile"/>; false on a 404 (nothing saved).</summary>
    public async Task<bool> DownloadAsync(string cls, string path, string destFile, CancellationToken stop = default)
    {
        using var r = await SendRawAsync(HttpMethod.Get, $"{filesRoot}/raw?class={Q(cls)}&path={Q(path)}", null, stop);
        if (r.StatusCode == HttpStatusCode.NotFound) return false;
        if (!r.IsSuccessStatusCode)
        {
            string text = await r.Content.ReadAsStringAsync(stop);
            throw new CanvasLibraryException((int)r.StatusCode, Detail(text) ?? r.ReasonPhrase ?? "");
        }
        byte[] bytes = await r.Content.ReadAsByteArrayAsync(stop);
        await File.WriteAllBytesAsync(destFile, bytes, stop);
        return true;
    }
}
