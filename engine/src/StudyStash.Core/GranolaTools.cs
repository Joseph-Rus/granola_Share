using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>A tool on Granola's MCP server, with the argument schema it takes.</summary>
public sealed record ToolInfo(string Name, string Description, JsonObject Schema);

/// <summary>What a tool call sent back: structured content if any, its text blocks, and whether it failed.</summary>
public sealed record ToolResult(JsonNode? Structured, IReadOnlyList<string> Texts, bool IsError = false)
{
    public static ToolResult Text(string text) => new(null, [text]);
}

/// <summary>Who is signed in to Granola.</summary>
public sealed record AccountInfo(string Email, string Workspace, string WorkspaceId, List<string> Scopes, JsonNode? Raw);

public static partial class Granola
{
    // Candidate argument names, most likely first, for what we want to pass. Tool argument names are read from the
    // server's own schemas, so small changes on Granola's side don't break the sync.
    public static readonly Dictionary<string, string[]> ArgCandidates = new()
    {
        ["since"] = ["date_from", "start_date", "from_date", "since", "after", "updated_after", "created_after", "from"],
        ["until"] = ["date_to", "end_date", "to_date", "until", "before", "to"],
        ["limit"] = ["limit", "max_results", "page_size", "count"],
        ["offset"] = ["offset", "skip", "page"],
        ["ids"] = ["document_ids", "meeting_ids", "ids", "note_ids"],
        ["id"] = ["document_id", "meeting_id", "id", "note_id"],
        ["folder"] = ["folder_id", "folder"],
    };

    // Keys under which a list of meetings may hide, in JSON or XML-derived data.
    static readonly string[] MeetingListKeys = ["meetings", "notes", "documents", "results", "items", "data", "meetings_data", "meeting"];

    /// <summary>A tool's answer as plain data: structured content, JSON text, Granola's XML, or the text itself.</summary>
    public static JsonNode? ParseToolResult(ToolResult result)
    {
        if (Py.Truthy(result.Structured)) return result.Structured;
        string blob = Py.Strip(string.Join("\n", result.Texts));
        if (blob.Length == 0) return null;
        blob = StripFences(blob);
        try
        {
            return Py.JsonLoads(blob);
        }
        catch (JsonException)
        {
        }
        if (XmlCandidate(blob) is string xml)
        {
            var data = XmlToData(xml);
            if (Py.AsString(data) is null) return data;
        }
        return JsonValue.Create(blob);
    }

    /// <summary>The list of meeting objects inside whatever shape the tool returned.</summary>
    public static List<JsonObject> ExtractMeetings(JsonNode? payload)
    {
        switch (payload)
        {
            case null:
                return [];
            case JsonValue when Py.AsString(payload) is string text: // the raw XML text, as Granola sends it
                var parsed = XmlToData(text);
                return Py.AsString(parsed) is not null ? [] : ExtractMeetings(parsed);
            case JsonArray list:
                return list.OfType<JsonObject>().ToList();
            case JsonObject o:
                foreach (string key in MeetingListKeys)
                {
                    var v = o[key];
                    if (v is JsonArray items) return items.OfType<JsonObject>().ToList();
                    if (v is JsonObject inner && ExtractMeetings(inner) is { Count: > 0 } found) return found;
                }
                return FirstKey(o, IdKeys) is not null ? [o] : [];
            default:
                return [];
        }
    }

    static JsonObject Properties(JsonNode? schema) =>
        (schema as JsonObject)?["properties"] as JsonObject is { Count: > 0 } props ? props : new JsonObject();

    /// <summary>Our wants (since, limit, ids…) under the tool's real parameter names.</summary>
    public static JsonObject BuildArgs(JsonNode? schema, IEnumerable<(string Want, JsonNode? Value)> wanted)
    {
        var props = Properties(schema);
        var args = new JsonObject();
        foreach (var (want, given) in wanted)
        {
            if (given is null) continue;
            foreach (string cand in ArgCandidates.GetValueOrDefault(want, [want]))
            {
                if (!props.ContainsKey(cand)) continue;
                var value = given.DeepClone();
                string? type = Py.AsString((props[cand] as JsonObject)?["type"]);
                if (want == "ids" && type == "string" && value is JsonArray ids) value = string.Join(",", ids.Select(Py.Str));
                if (want == "id" && value is JsonArray one) value = one[0]?.DeepClone();
                args[cand] = value;
                break;
            }
        }
        return args;
    }

    static string IsoDay(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Arguments for list_meetings, given the tool's real schema. Granola's has time_range (this_week, last_week,
    /// last_30_days, custom) with custom_start and custom_end, and no paging. A `since` becomes a custom range ending
    /// tomorrow, so today's notes are in it; no `since` means the last 30 days. Other schemas get date_from, limit
    /// and offset under whatever names they use.
    /// </summary>
    public static JsonObject ListArgs(JsonNode? schema, DateOnly? since = null, DateOnly? until = null, int? limit = 50,
        int? offset = 0, DateOnly? today = null)
    {
        var props = Properties(schema);
        var timeRange = props["time_range"] as JsonObject;
        var choices = (timeRange?["enum"] as JsonArray ?? new JsonArray()).Select(Py.Str).ToList();
        if (timeRange is not null)
        {
            if (since is DateOnly from && choices.Contains("custom"))
            {
                var end = until ?? (today ?? DateOnly.FromDateTime(DateTime.Today)).AddDays(1);
                return new JsonObject { ["time_range"] = "custom", ["custom_start"] = IsoDay(from), ["custom_end"] = IsoDay(end) };
            }
            if (since is null) return new JsonObject { ["time_range"] = "last_30_days" };
        }
        var args = BuildArgs(schema, [
            ("since", since is DateOnly s ? IsoDay(s) : null),
            ("until", until is DateOnly u ? IsoDay(u) : null),
            ("limit", limit),
            ("offset", offset is > 0 or < 0 ? offset : null),
        ]);
        if (timeRange is not null && !ArgCandidates["since"].Any(args.ContainsKey) && choices.Contains("last_30_days"))
            args["time_range"] = "last_30_days";
        return args;
    }

    // --- accounts -------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"[,;\s]+")]
    private static partial Regex ScopeSeparators();

    static List<string> ScopeList(JsonNode? v)
    {
        if (!Py.Truthy(v)) return [];
        if (Py.AsString(v) is string s) return ScopeSeparators().Split(s).Select(Py.Strip).Where(x => x.Length > 0).ToList();
        if (v is JsonObject o) return ScopeList(FirstKey(o, ["scope", "scopes", "text"]));
        if (v is JsonArray a) return a.Select(x => Py.Strip(AsText(x))).Where(x => x.Length > 0).ToList();
        return [];
    }

    static string StrOrEmpty(JsonNode? v) => Py.Truthy(v) ? Py.Str(v) : "";

    /// <summary>Who is signed in, from get_account_info's output (JSON or XML).</summary>
    public static AccountInfo NormalizeAccount(JsonNode? payload)
    {
        var empty = new AccountInfo("", "", "", [], payload);
        var data = payload;
        if (Py.AsString(data) is string text)
        {
            var parsed = XmlToData(text);
            if (Py.AsString(parsed) is string plain)
            {
                var m = EmailAddress().Match(plain);
                return empty with { Email = m.Success ? m.Value : "" };
            }
            data = parsed;
        }
        if (data is not JsonObject d) return empty;
        foreach (string wrapper in new[] { "account", "account_info", "user", "data", "result" })
        {
            if (d[wrapper] is JsonObject inner && FirstKey(inner, ["email", "active_workspace", "workspace"]) is not null)
            {
                d = inner;
                break;
            }
        }
        var email = FirstKey(d, ["email", "user_email", "account_email"]);
        if (email is null)
        {
            foreach (string k in new[] { "user", "account" })
            {
                if (d[k] is not JsonObject holder) continue;
                email = FirstKey(holder, ["email"]);
                if (Py.Truthy(email)) break;
            }
        }
        string workspace = "", workspaceId = "";
        var ws = FirstKey(d, ["active_workspace", "workspace", "current_workspace"]);
        if (ws is JsonObject w)
        {
            workspace = StrOrEmpty(FirstKey(w, ["display_name", "name", "title"]));
            workspaceId = StrOrEmpty(FirstKey(w, ["id", "workspace_id"]));
        }
        else if (ws is not null)
        {
            workspace = Py.Strip(AsText(ws));
            workspaceId = StrOrEmpty(FirstKey(d, ["workspace_id", "active_workspace_id"]));
        }
        var access = d["mcp_note_access"];
        var scopes = access is JsonObject a ? FirstKey(a, ["scopes", "scope"]) : null;
        if (scopes is null)
            scopes = Py.AsString(access) is not null || access is JsonArray ? access : FirstKey(d, ["scopes", "scope"]);
        return new AccountInfo(Py.Strip(AsText(email)), workspace, workspaceId, ScopeList(scopes), payload);
    }

    /// <summary>"alex@example.com \u00b7 Alex's workspace", or "not signed in".</summary>
    public static string AccountLabel(AccountInfo? info)
    {
        string email = Py.Strip(info?.Email);
        if (email.Length == 0) return "not signed in";
        string ws = Py.Strip(info!.Workspace);
        return ws.Length > 0 ? $"{email} \u00b7 {ws}" : email;
    }

    /// <summary>The transcript from a get_meeting_transcript result: JSON, XML-derived data, or text.</summary>
    public static string TranscriptText(JsonNode? payload)
    {
        if (payload is JsonObject d)
        {
            foreach (string key in new[] { "transcript", "text", "content" })
                if (Py.Truthy(d[key])) return AsText(d[key]);
            return "";
        }
        return AsText(payload);
    }
}
