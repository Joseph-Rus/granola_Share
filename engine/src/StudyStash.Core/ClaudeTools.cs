using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace StudyStash.Core;

/// <summary>Where Claude's tools read the library: in the library itself, or over its API from a laptop.</summary>
public interface ILibrarySource
{
    Task<JsonObject> OverviewAsync();
    Task<JsonArray> LecturesAsync(string? className, int limit, string? before);
    Task<JsonObject?> LectureAsync(string id);
    Task<JsonObject> SearchAsync(string query, string? className, int limit);
}

/// <summary>The library on this computer.</summary>
public sealed class LocalLibrary(LibraryReader reader) : ILibrarySource
{
    public Task<JsonObject> OverviewAsync() => Task.FromResult(reader.Overview());
    public Task<JsonArray> LecturesAsync(string? className, int limit, string? before) => Task.FromResult(reader.Lectures(className, limit, before));
    public Task<JsonObject?> LectureAsync(string id) => Task.FromResult(reader.Lecture(id));
    public Task<JsonObject> SearchAsync(string query, string? className, int limit) => Task.FromResult(reader.Search(query, className, limit));
}

/// <summary>
/// The library over its API (/api/v2), with the laptop's password: what the Study Stash app shows, and what Claude
/// reads through <c>Study Stash mcp</c> on a laptop.
/// </summary>
public sealed class RemoteLibrary(string serverUrl, string key, HttpClient? http = null) : ILibrarySource
{
    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(150) };
    readonly HttpClient client = http ?? Shared;
    readonly string root = serverUrl.TrimEnd('/') + "/api/v2";

    public string ServerUrl { get; } = serverUrl.TrimEnd('/');

    async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken stop = default)
    {
        using var request = new HttpRequestMessage(method, root + path);
        if (key.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await client.SendAsync(request, stop);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        string text = await r.Content.ReadAsStringAsync(stop);
        if (!r.IsSuccessStatusCode)
        {
            string detail = (JsonNode.Parse(text.Length > 0 && text[0] == '{' ? text : "{}") as JsonObject)?["detail"]?.GetValue<string>() ?? r.ReasonPhrase ?? "";
            throw new LibraryRefusedException((int)r.StatusCode, detail);
        }
        return JsonNode.Parse(text);
    }

    static string Q(string? s) => Uri.EscapeDataString(s ?? "");

    /// <summary>The library's name and classes. A library from before /api/v2 (the Python engine) answers 404.</summary>
    public async Task<JsonObject> OverviewAsync() =>
        await SendAsync(HttpMethod.Get, "/library") as JsonObject ?? throw new LibraryRefusedException(404, "This library is older than the app: update it to browse it here.");

    public async Task<JsonArray> LecturesAsync(string? className, int limit, string? before) =>
        (JsonArray)(await SendAsync(HttpMethod.Get, $"/lectures?limit={limit}" + (className is null ? "" : $"&class={Q(className)}")
            + (before is null ? "" : $"&before={Q(before)}")))!;

    public async Task<JsonObject?> LectureAsync(string id) => await SendAsync(HttpMethod.Get, $"/lectures/{Q(id)}") as JsonObject;

    public async Task<JsonObject> SearchAsync(string query, string? className, int limit) =>
        (JsonObject)(await SendAsync(HttpMethod.Get, $"/search?q={Q(query)}&limit={limit}" + (className is null ? "" : $"&class={Q(className)}")))!;

    public async Task<JsonObject> AskAsync(string question, string? lectureId = null, string? className = null, string? live = null,
        string? liveTitle = null, CancellationToken stop = default) =>
        (JsonObject)(await SendAsync(HttpMethod.Post, "/ask", new JsonObject
        {
            ["question"] = question, ["lecture"] = lectureId, ["class"] = className, ["live"] = live, ["live_title"] = liveTitle,
        }, stop))!;

    public async Task MoveAsync(string id, string className) =>
        await SendAsync(HttpMethod.Post, $"/lectures/{Q(id)}/class", new JsonObject { ["class"] = className });

    public async Task RewriteAsync(string id) => await SendAsync(HttpMethod.Post, $"/lectures/{Q(id)}/rewrite", new JsonObject());

    public async Task DeleteAsync(string id) => await SendAsync(HttpMethod.Delete, $"/lectures/{Q(id)}");

    public async Task<JsonObject> AddClassAsync(string name) =>
        (JsonObject)(await SendAsync(HttpMethod.Post, "/classes", new JsonObject { ["name"] = name }))!;

    public async Task<JsonObject?> ClaudeAsync(HttpMethod method, string path = "", JsonObject? body = null) =>
        await SendAsync(method, "/claude" + path, body) as JsonObject;
}

/// <summary>
/// The tools Claude gets (Claude Code, Claude Desktop, claude.ai): read-only, over the library's lectures. They
/// answer in short plain text with each lecture's id, so Claude can ask for more.
/// </summary>
public static class ClaudeTools
{
    public const string ServerName = "study-stash";

    public const string Instructions =
        "Study Stash is the user's own lecture library: lectures they recorded, transcribed on their laptop, with notes "
        + "written from each transcript. Search it before answering anything about their classes, lectures, exams or "
        + "assignments, and say which lecture (and the time in it) an answer comes from. Lecture ids look like rec-... "
        + "or a long hex string; pass them to get_lecture and get_transcript.";

    static string Day(string? date)
    {
        if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)) return date ?? "";
        return d.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
    }

    static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

    static string Line(JsonObject l)
    {
        var sb = new StringBuilder($"- [{S(l["id"])}] {S(l["title"])} — {S(l["class"])}, {Day(S(l["date"]))}");
        if (l["seconds"] is JsonValue v && v.TryGetValue(out double s)) sb.Append(", ").Append(TimedText.Length(s));
        if (S(l["status"]) is not ("done" or "")) sb.Append(" (notes still being written)");
        if (S(l["summary"]) is { Length: > 0 } summary) sb.Append(". ").Append(summary);
        return sb.ToString();
    }

    public static async Task<string> ListClassesAsync(ILibrarySource lib)
    {
        var o = await lib.OverviewAsync();
        var sb = new StringBuilder($"Library: {S(o["name"])}\n");
        var classes = (o["classes"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (classes.Count == 0) sb.Append("No classes yet.\n");
        foreach (var c in classes) sb.Append($"- {S(c["name"])} ({c["lectures"]} lecture{(c["lectures"]?.GetValue<int>() == 1 ? "" : "s")})\n");
        if (o["unsorted"]?.GetValue<int>() is int u and > 0) sb.Append($"Unsorted: {u} lecture{(u == 1 ? "" : "s")} not yet in a class\n");
        return sb.ToString().TrimEnd();
    }

    public static async Task<string> ListLecturesAsync(ILibrarySource lib, string? className, int limit, string? before)
    {
        var list = await lib.LecturesAsync(string.IsNullOrWhiteSpace(className) ? null : className, Math.Clamp(limit, 1, 100), before);
        if (list.Count == 0) return className is null ? "No lectures yet." : $"No lectures in {className}.";
        string more = list.Count >= limit ? $"\n(More: call again with before=\"{S(list[^1]?["date"])}\".)" : "";
        return string.Join("\n", list.OfType<JsonObject>().Select(Line)) + more;
    }

    public static async Task<string> SearchAsync(ILibrarySource lib, string query, string? className, int limit)
    {
        var r = await lib.SearchAsync(query, string.IsNullOrWhiteSpace(className) ? null : className, Math.Clamp(limit, 1, 25));
        var sb = new StringBuilder();
        var lectures = (r["lectures"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var passages = (r["passages"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (lectures.Count == 0 && passages.Count == 0) return $"Nothing in the library matches \"{query}\". Try fewer or other words.";
        if (lectures.Count > 0) sb.Append("Lectures:\n").AppendJoin("\n", lectures.Select(Line)).Append("\n\n");
        if (passages.Count > 0)
        {
            sb.Append("Passages:\n");
            foreach (var p in passages)
            {
                string where = p["at"] is JsonValue v && v.TryGetValue(out double at) ? $"at {TimedText.Clock(at)}"
                    : S(p["section"]) is { Length: > 0 } sec ? sec : S(p["kind"]);
                sb.Append($"- [{S(p["id"])}] {S(p["title"])} ({S(p["class"])}, {Day(S(p["date"]))}), {where}: {S(p["text"])}\n");
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static async Task<string> GetLectureAsync(ILibrarySource lib, string id)
    {
        var l = await lib.LectureAsync(id.Trim());
        if (l is null) return $"There's no lecture {id}. Use search_notes or list_lectures to find one.";
        var sb = new StringBuilder($"# {S(l["title"])}\n\nClass: {S(l["class"])}\nDate: {Day(S(l["date"]))}\n");
        if (l["seconds"] is JsonValue v && v.TryGetValue(out double s)) sb.Append($"Length: {TimedText.Length(s)}\n");
        if (S(l["owner"]) is { Length: > 0 } owner) sb.Append($"Recorded by: {owner}\n");
        var topics = (l["topics"] as JsonArray ?? []).Select(S).Where(t => t.Length > 0).ToList();
        if (topics.Count > 0) sb.Append($"Topics: {string.Join(", ", topics)}\n");
        string notes = S(l["notes"]);
        sb.Append('\n').Append(notes.Length > 0 ? notes : "(No notes yet: the library is still writing them, or there was too little to write from.)");
        string transcript = S(l["transcript"]);
        if (transcript.Length > 0)
            sb.Append($"\n\n(The transcript has {transcript.Split('\n').Length} lines: get_transcript reads it, from any time.)");
        return sb.ToString();
    }

    public const int TranscriptChars = 40000;

    static double? ParseClock(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return null;
        var parts = t.Trim().Split(':');
        double total = 0;
        foreach (string p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return null;
            total = total * 60 + n;
        }
        return total;
    }

    public static async Task<string> GetTranscriptAsync(ILibrarySource lib, string id, string? start, string? end)
    {
        var l = await lib.LectureAsync(id.Trim());
        if (l is null) return $"There's no lecture {id}.";
        string transcript = S(l["transcript"]);
        if (transcript.Length == 0) return "This lecture has no transcript.";
        if (!TimedText.HasTimes(transcript))
            return transcript.Length > TranscriptChars ? transcript[..TranscriptChars] + "\n(Cut here: this transcript has no times to page by.)" : transcript;
        double from = ParseClock(start) ?? 0, to = ParseClock(end) ?? double.MaxValue;
        var lines = TimedText.Parse(transcript).Where(s => s.Start >= from && s.Start < to).ToList();
        var sb = new StringBuilder();
        foreach (var s in lines)
        {
            string line = $"[{TimedText.Clock(s.Start)}] {s.Text}\n";
            if (sb.Length + line.Length > TranscriptChars)
            {
                sb.Append($"(More: call again with start=\"{TimedText.Clock(s.Start)}\".)");
                return sb.ToString();
            }
            sb.Append(line);
        }
        return sb.Length == 0 ? "Nothing was said in that stretch." : sb.ToString().TrimEnd();
    }

    public static List<McpServerTool> Tools(ILibrarySource lib)
    {
        static McpServerToolCreateOptions Named(string name, string title, string description) =>
            new() { Name = name, Title = title, Description = description, ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false };
        return
        [
            McpServerTool.Create(() => ListClassesAsync(lib),
                Named("list_classes", "List classes", "The user's classes in Study Stash, with how many lectures each has.")),
            McpServerTool.Create(
                ([Description("Only this class (a name from list_classes). Leave out for every class.")] string? class_name = null,
                 [Description("How many lectures, newest first (1 to 100).")] int limit = 20,
                 [Description("Only lectures before this date (from a previous answer), to page back.")] string? before = null) =>
                    ListLecturesAsync(lib, class_name, limit, before),
                Named("list_lectures", "List lectures", "Recent lectures, newest first: id, title, class, date, length, and the lecture in one sentence.")),
            McpServerTool.Create(
                ([Description("Words to look for, as you'd type them in a search box.")] string query,
                 [Description("Only this class. Leave out to search everything.")] string? class_name = null,
                 [Description("How many results of each kind (1 to 25).")] int limit = 10) =>
                    SearchAsync(lib, query, class_name, limit),
                Named("search_notes", "Search notes and transcripts",
                    "Full-text search of every lecture's notes and transcript. Returns matching lectures, and passages with the "
                    + "section of the notes or the time in the recording they come from.")),
            McpServerTool.Create(
                ([Description("The lecture's id, from list_lectures or search_notes.")] string lecture_id) => GetLectureAsync(lib, lecture_id),
                Named("get_lecture", "Read a lecture's notes", "One lecture's notes (Markdown: summary, key points, definitions, announcements, questions) and details.")),
            McpServerTool.Create(
                ([Description("The lecture's id.")] string lecture_id,
                 [Description("Start here, as mm:ss or h:mm:ss. Leave out for the beginning.")] string? start = null,
                 [Description("Stop before this time. Leave out for the end.")] string? end = null) =>
                    GetTranscriptAsync(lib, lecture_id, start, end),
                Named("get_transcript", "Read a lecture's transcript", "What was said in a lecture, line by line with the time of each line, from a given time.")),
        ];
    }

    public static List<McpServerPrompt> Prompts() =>
    [
        McpServerPrompt.Create(
            ([Description("The class, as named in Study Stash.")] string class_name) =>
                $"Make me a study guide for {class_name} from my Study Stash lectures. Use list_lectures for {class_name}, read the "
                + "lectures with get_lecture, and organize the guide by topic: key ideas, definitions, worked examples, and anything "
                + "announced about exams or assignments. Say which lecture each part comes from.",
            new McpServerPromptCreateOptions { Name = "study_guide", Title = "Study guide for a class", Description = "A study guide for a class, from its lectures." }),
        McpServerPrompt.Create(
            ([Description("A class, a topic, or a lecture title.")] string topic) =>
                $"Quiz me on {topic}, using my Study Stash lectures (search_notes, then get_lecture). Ask one question at a time, "
                + "wait for my answer, then tell me what I got right and what the lecture said, with the lecture and time.",
            new McpServerPromptCreateOptions { Name = "quiz_me", Title = "Quiz me", Description = "Questions on a class or topic, one at a time, from your lectures." }),
    ];

    public static McpServerOptions Options(ILibrarySource lib)
    {
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var t in Tools(lib)) tools.Add(t);
        var prompts = new McpServerPrimitiveCollection<McpServerPrompt>();
        foreach (var p in Prompts()) prompts.Add(p);
        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Title = "Study Stash", Version = Engine.Version },
            ServerInstructions = Instructions,
            ToolCollection = tools,
            PromptCollection = prompts,
        };
    }

    /// <summary>The MCP server over stdin and stdout: what Claude Code and Claude Desktop start (<c>Study Stash mcp</c>).</summary>
    public static async Task RunStdioAsync(ILibrarySource lib, CancellationToken stop)
    {
        var options = Options(lib);
        await using var server = McpServer.Create(new StdioServerTransport(ServerName), options);
        await server.RunAsync(stop);
    }
}
