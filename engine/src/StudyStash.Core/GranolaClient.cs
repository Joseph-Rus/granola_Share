using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace StudyStash.Core;

/// <summary>One conversation with Granola's MCP server: list its tools, call one. Tests stand in a fake.</summary>
public interface IGranolaSession : IAsyncDisposable
{
    Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken ct = default);
    Task<ToolResult> CallToolAsync(string name, JsonObject args, CancellationToken ct = default);
}

/// <summary>A tool call Granola answered with an error.</summary>
public sealed class GranolaToolException(string message) : Exception(message);

/// <summary>Granola's official remote MCP server (https://mcp.granola.ai/mcp), over streamable HTTP.</summary>
public sealed class McpGranolaSession(McpClient client) : IGranolaSession
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public static async Task<McpGranolaSession> ConnectAsync(string url, string accessToken, CancellationToken ct = default)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(url),
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = "Granola",
            ConnectionTimeout = TimeSpan.FromSeconds(90),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + accessToken },
        }, Http, loggerFactory: null, ownsHttpClient: false);
        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "study-stash", Version = typeof(McpGranolaSession).Assembly.GetName().Version?.ToString() ?? "0" },
        };
        return new McpGranolaSession(await McpClient.CreateAsync(transport, options, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken ct = default)
    {
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return tools.Select(t => new ToolInfo(t.Name, t.Description ?? "",
            JsonNode.Parse(t.JsonSchema.GetRawText()) as JsonObject ?? new JsonObject())).ToList();
    }

    public async Task<ToolResult> CallToolAsync(string name, JsonObject args, CancellationToken ct = default)
    {
        var request = new CallToolRequestParams
        {
            Name = name,
            Arguments = args.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value, JsonNodeContext.Default.JsonNode)),
        };
        var result = await client.CallToolAsync(request, ct);
        var structured = result.StructuredContent is JsonElement e ? Py.JsonLoads(e.GetRawText()) : null;
        var texts = result.Content.OfType<TextContentBlock>().Select(t => t.Text).ToList();
        return new ToolResult(structured, texts, result.IsError == true);
    }

    public ValueTask DisposeAsync() => client.DisposeAsync();
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonNode))]
internal sealed partial class JsonNodeContext : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>Where lectures come from. GranolaClient is the real one; the sync tests stand in canned lectures.</summary>
public interface ILectureSource
{
    Task<IGranolaSession> SessionAsync(CancellationToken ct = default);
    Task<List<Meeting>> ListMeetingsAsync(IGranolaSession session, DateOnly? since = null, DateOnly? until = null, int limit = 50,
        int maxPages = 20, CancellationToken ct = default);
    Task<List<Meeting>> GetMeetingsAsync(IGranolaSession session, IReadOnlyList<string> ids, CancellationToken ct = default);
    Task<string> GetTranscriptAsync(IGranolaSession session, string meetingId, CancellationToken ct = default);
}

/// <summary>
/// Reads lectures from Granola's MCP server. Tool names and argument names come from the server's own tool list,
/// so small changes on Granola's side don't break the sync.
/// </summary>
public sealed class GranolaClient(Func<CancellationToken, Task<IGranolaSession>> connect) : ILectureSource
{
    Dictionary<string, ToolInfo>? tools;

    /// <summary>The real server, signed in as whoever's tokens.json this is.</summary>
    public static GranolaClient For(string mcpUrl, GranolaOAuth oauth) =>
        new(async ct => await McpGranolaSession.ConnectAsync(mcpUrl, await oauth.AccessTokenAsync(ct), ct));

    public Task<IGranolaSession> SessionAsync(CancellationToken ct = default) => connect(ct);

    public async Task<Dictionary<string, ToolInfo>> ToolsAsync(IGranolaSession session, CancellationToken ct = default)
    {
        if (tools is null)
        {
            var found = new Dictionary<string, ToolInfo>();
            foreach (var t in await session.ListToolsAsync(ct)) found[t.Name] = t;
            tools = found;
        }
        return tools;
    }

    /// <summary>The first of these names the server has, or else a tool whose name contains one of them.</summary>
    static string? FindTool(Dictionary<string, ToolInfo> tools, params string[] names)
    {
        foreach (string n in names)
            if (tools.ContainsKey(n)) return n;
        foreach (string n in names)
            foreach (string t in tools.Keys)
                if (t.Contains(n, StringComparison.Ordinal)) return t;
        return null;
    }

    static string ToolList(Dictionary<string, ToolInfo> tools) =>
        Py.Repr(new JsonArray(tools.Keys.Order(StringComparer.Ordinal).Select(k => (JsonNode?)k).ToArray()));

    public async Task<JsonNode?> CallAsync(IGranolaSession session, string name, JsonObject args, CancellationToken ct = default)
    {
        var result = await session.CallToolAsync(name, args, ct);
        if (result.IsError) throw new GranolaToolException($"{name} failed: {Py.Str(Granola.ParseToolResult(result))}");
        return Granola.ParseToolResult(result);
    }

    public async Task<List<Meeting>> ListMeetingsAsync(IGranolaSession session, DateOnly? since = null, DateOnly? until = null,
        int limit = 50, int maxPages = 20, CancellationToken ct = default)
    {
        var all = await ToolsAsync(session, ct);
        string name = FindTool(all, "list_meetings", "list_notes", "search_meetings")
            ?? throw new GranolaToolException($"no list_meetings tool on server; tools are {ToolList(all)}");
        var schema = all[name].Schema;
        var output = new List<Meeting>();
        var seen = new HashSet<string>();
        int offset = 0;
        bool schemaPages = Granola.ArgCandidates["offset"].Any(c => (schema["properties"] as JsonObject)?.ContainsKey(c) == true);
        for (int page = 0; page < maxPages; page++)
        {
            var args = Granola.ListArgs(schema, since, until, limit, offset);
            var batch = Granola.ExtractMeetings(await CallAsync(session, name, args, ct)).Select(Granola.NormalizeMeeting).ToList();
            var fresh = batch.Where(m => !seen.Contains(m.Id)).ToList();
            output.AddRange(fresh);
            foreach (var m in fresh) seen.Add(m.Id);
            bool canPage = Granola.ArgCandidates["offset"].Any(args.ContainsKey) || (offset == 0 && schemaPages);
            if (fresh.Count == 0 || !canPage || batch.Count < limit) break;
            offset += batch.Count;
        }
        return output;
    }

    public async Task<List<Meeting>> GetMeetingsAsync(IGranolaSession session, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var all = await ToolsAsync(session, ct);
        string name = FindTool(all, "get_meetings", "get_meeting", "get_notes", "get_note")
            ?? throw new GranolaToolException($"no get_meetings tool on server; tools are {ToolList(all)}");
        var schema = all[name].Schema;
        bool takesList = Granola.ArgCandidates["ids"].Any(c => (schema["properties"] as JsonObject)?.ContainsKey(c) == true);
        var output = new List<Meeting>();
        if (takesList)
        {
            for (int i = 0; i < ids.Count; i += 10)
            {
                var chunk = new JsonArray(ids.Skip(i).Take(10).Select(id => (JsonNode?)id).ToArray());
                var payload = await CallAsync(session, name, Granola.BuildArgs(schema, [("ids", chunk)]), ct);
                output.AddRange(Granola.ExtractMeetings(payload).Select(Granola.NormalizeMeeting));
            }
        }
        else
        {
            foreach (string id in ids)
            {
                var payload = await CallAsync(session, name, Granola.BuildArgs(schema, [("id", id)]), ct);
                output.AddRange(Granola.ExtractMeetings(payload).Select(Granola.NormalizeMeeting));
            }
        }
        return output;
    }

    public async Task<string> GetTranscriptAsync(IGranolaSession session, string meetingId, CancellationToken ct = default)
    {
        var all = await ToolsAsync(session, ct);
        string? name = FindTool(all, "get_meeting_transcript", "get_transcript");
        if (name is null) return "";
        JsonNode? payload;
        try
        {
            payload = await CallAsync(session, name, Granola.BuildArgs(all[name].Schema, [("id", meetingId)]), ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return ""; // a paid-plan tool: free accounts get an error here
        }
        return Granola.TranscriptText(payload);
    }

    /// <summary>Who is signed in, or null when the server can't say.</summary>
    public async Task<AccountInfo?> GetAccountInfoAsync(IGranolaSession session, CancellationToken ct = default)
    {
        try
        {
            var all = await ToolsAsync(session, ct);
            string? name = FindTool(all, "get_account_info", "account_info", "whoami");
            return name is null ? null : Granola.NormalizeAccount(await CallAsync(session, name, new JsonObject(), ct));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
