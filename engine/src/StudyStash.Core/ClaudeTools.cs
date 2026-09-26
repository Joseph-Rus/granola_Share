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

    /// <summary>Canvas, read through the Chrome extension: the linked courses ("courses"), a read ("fetch": url,
    /// kind, save_to), the assignments ("assignments": class, days), one assignment's whole detail ("assignment":
    /// class, id or name), a class's modules ("modules": class), its files ("files": class) or its announcements
    /// ("announcements": class, limit). Libraries without Canvas say so.</summary>
    bool HasCanvas => false;

    /// <summary>Files that aren't lectures, and readable folders' files, matching a search ([] where unsupported).</summary>
    Task<JsonArray> SearchFilesAsync(string query, int limit) => Task.FromResult(new JsonArray());

    Task<JsonNode> CanvasAsync(string what, JsonObject? body = null) =>
        Task.FromResult<JsonNode>(new JsonObject { ["error"] = "This library can't read Canvas." });
}

/// <summary>The library on this computer.</summary>
public sealed class LocalLibrary(LibraryReader reader, Canvas.CanvasSync? canvas = null, string? home = null, Ai.FileIndex? files = null) : ILibrarySource
{
    public Task<JsonArray> SearchFilesAsync(string query, int limit) => Task.FromResult(new JsonArray((files?.Search(query, limit) ?? [])
        .Select(h => (JsonNode)new JsonObject { ["root"] = h.Root, ["path"] = h.Path, ["title"] = h.Title, ["snippet"] = System.Net.WebUtility.HtmlDecode(h.Snippet.Replace("<mark>", "").Replace("</mark>", "")) }).ToArray()));

    public bool HasCanvas => canvas is not null;

    static JsonObject CanvasErr(string msg) => new() { ["error"] = msg };

    public async Task<JsonNode> CanvasAsync(string what, JsonObject? body = null)
    {
        if (canvas is null) return new JsonObject { ["error"] = "This library can't read Canvas." };
        static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";
        string? cls = S(body?["class"]) is { Length: > 0 } c ? c : null;
        switch (what)
        {
            case "courses": return canvas.Courses();
            case "fetch": return await canvas.FetchAsync(S(body?["url"]), S(body?["kind"]) is { Length: > 0 } k ? k : "json", S(body?["save_to"]));
            case "assignment":
            {
                if (cls is null) return CanvasErr("Give a class name.");
                var index = Canvas.CourseIndex.Load(home ?? "", cls);
                if (index is null) return CanvasErr($"{cls} hasn't synced with Canvas yet.");
                long? id = body?["id"] is JsonValue idv && idv.TryGetValue(out long idl) ? idl : null;
                string name = S(body?["name"]);
                if (id is null && name.Length > 0)
                {
                    var matches = index.Assignments.Where(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0) return CanvasErr($"No assignment in {cls} matches \"{name}\".");
                    if (matches.Count > 1)
                        return new JsonObject { ["candidates"] = new JsonArray(matches.Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["name"] = m.Name }).ToArray()) };
                    id = matches[0].Id;
                }
                if (id is null) return CanvasErr("Give an assignment id or words of its name.");
                var found = Canvas.CanvasView.Assignment(index, id.Value, canvas.Clock(), canvas.Crawl.AssignmentFolder(cls, id.Value));
                return found is null ? CanvasErr($"No assignment {id} in {cls}.") : found;
            }
            case "modules":
                return cls is null ? CanvasErr("Give a class name.") : Canvas.CanvasView.Modules(Canvas.CourseIndex.Load(home ?? "", cls));
            case "files":
                return cls is null ? CanvasErr("Give a class name.") : Canvas.CanvasView.Files(Canvas.CourseIndex.Load(home ?? "", cls));
            case "announcements":
            {
                if (cls is null) return CanvasErr("Give a class name.");
                var result = Canvas.CanvasView.Announcements(Canvas.CourseIndex.Load(home ?? "", cls), Canvas.CanvasSeen.For(home ?? "", cls));
                if (body?["limit"] is JsonValue lv && lv.TryGetValue(out int limit) && result["items"] is JsonArray items)
                    while (items.Count > limit) items.RemoveAt(items.Count - 1);
                return result;
            }
            default: // "assignments": what's still due, everywhere or in one class
                var all = Canvas.Assignments.Load(home ?? "");
                var list = Canvas.Assignments.Upcoming(all, DateTime.Now, body?["days"] is JsonValue d && d.TryGetValue(out int n) ? n : 14, cls);
                return new JsonArray(list.Select(a => (JsonNode)new JsonObject
                {
                    ["class"] = a.ClassName, ["name"] = a.Name, ["due"] = a.Due, ["status"] = a.Status, ["points"] = a.Points,
                    ["label"] = Canvas.Assignments.Label(a.Status, a.Late), ["folder"] = canvas.Crawl.AssignmentFolder(a.ClassName, a.Id), ["url"] = a.Url,
                }).ToArray());
        }
    }

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

    public bool HasCanvas => true; // a library from before Canvas answers each tool with why not

    public async Task<JsonArray> SearchFilesAsync(string query, int limit)
    {
        try
        {
            return await SendAsync(HttpMethod.Get, $"/files/search?q={Q(query)}&limit={limit}") as JsonArray ?? [];
        }
        catch (LibraryRefusedException)
        {
            return [];
        }
    }

    static JsonObject CanvasErr(string msg) => new() { ["error"] = msg };
    const string OlderLibrary = "The library runs an older Study Stash, without this.";

    static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

    public async Task<JsonNode> CanvasAsync(string what, JsonObject? body = null)
    {
        string? cls = S(body?["class"]) is { Length: > 0 } c ? c : null;
        try
        {
            switch (what)
            {
                case "courses": return await SendAsync(HttpMethod.Get, "/canvas/agent-courses") ?? CanvasErr("The library runs an older Study Stash, without Canvas.");
                case "fetch": return await SendAsync(HttpMethod.Post, "/canvas/fetch", body) ?? CanvasErr("The library runs an older Study Stash, without Canvas.");
                case "assignment":
                {
                    if (cls is null) return CanvasErr("Give a class name.");
                    long? id = body?["id"] is JsonValue idv && idv.TryGetValue(out long idl) ? idl : null;
                    string name = S(body?["name"]);
                    if (id is null && name.Length > 0)
                    {
                        var listAll = await SendAsync(HttpMethod.Get, $"/assignments?class={Q(cls)}") as JsonArray ?? [];
                        var matches = listAll.OfType<JsonObject>().Where(o => S(o["name"]).Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (matches.Count == 0) return CanvasErr($"No assignment in {cls} matches \"{name}\".");
                        if (matches.Count > 1)
                            return new JsonObject { ["candidates"] = new JsonArray(matches.Select(m => (JsonNode)new JsonObject { ["id"] = m["id"]!.DeepClone(), ["name"] = m["name"]!.DeepClone() }).ToArray()) };
                        id = matches[0]["id"]!.GetValue<long>();
                    }
                    if (id is null) return CanvasErr("Give an assignment id or words of its name.");
                    return await SendAsync(HttpMethod.Get, $"/canvas/assignment?class={Q(cls)}&id={id}") ?? CanvasErr(OlderLibrary);
                }
                case "modules":
                    return cls is null ? CanvasErr("Give a class name.") : await SendAsync(HttpMethod.Get, $"/canvas/modules?class={Q(cls)}") ?? CanvasErr(OlderLibrary);
                case "files":
                    return cls is null ? CanvasErr("Give a class name.") : await SendAsync(HttpMethod.Get, $"/canvas/files?class={Q(cls)}") ?? CanvasErr(OlderLibrary);
                case "announcements":
                {
                    if (cls is null) return CanvasErr("Give a class name.");
                    if (await SendAsync(HttpMethod.Get, $"/canvas/announcements?class={Q(cls)}") is not JsonObject result) return CanvasErr(OlderLibrary);
                    if (body?["limit"] is JsonValue lv && lv.TryGetValue(out int limit) && result["items"] is JsonArray items)
                        while (items.Count > limit) items.RemoveAt(items.Count - 1);
                    return result;
                }
                default:
                    return await SendAsync(HttpMethod.Get, $"/assignments?days={(body?["days"] is JsonValue d && d.TryGetValue(out int n) ? n : 14)}"
                        + (cls is null ? "" : "&class=" + Q(cls))) ?? CanvasErr("The library runs an older Study Stash, without Canvas.");
            }
        }
        catch (LibraryRefusedException e)
        {
            return new JsonObject { ["error"] = e.Message };
        }
    }

    /// <summary>Canvas's settings for the app: GET, or POST {url, courses, sync}; "/extension" (the extension's key),
    /// "/courses" (POST: look up the person's Canvas courses through Chrome).</summary>
    public async Task<JsonObject?> CanvasSettingsAsync(HttpMethod method, string path = "", JsonObject? body = null) =>
        await SendAsync(method, "/canvas" + path, body) as JsonObject;

    /// <summary>A class's assignments (all of them), or what's due soon everywhere (<paramref name="days"/>).</summary>
    public async Task<JsonArray> AssignmentsAsync(string? className, int? days = null) =>
        await SendAsync(HttpMethod.Get, "/assignments?" + (className is null ? "" : "class=" + Q(className) + "&") + (days is int d ? $"days={d}" : "")) as JsonArray ?? [];

    /// <summary>A text file in a class's folder (an assignment's spec.md, modules.md): its text, or null.</summary>
    public async Task<string?> FileTextAsync(string className, string path) =>
        (await SendAsync(HttpMethod.Get, $"/files?class={Q(className)}&path={Q(path)}") as JsonObject)?["text"]?.GetValue<string>();

    /// <summary>One chat turn, streamed: <paramref name="onEvent"/> gets each event ({"chat"} first, then {"kind", "text",
    /// "name", "path"}). Null when the library is older than chat.</summary>
    public async Task<bool> ChatAsync(JsonObject body, Action<JsonObject> onEvent, CancellationToken stop = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, root + "/chat")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (key.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        using var r = await ChatClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
        if (r.StatusCode == HttpStatusCode.NotFound) return false;
        if (!r.IsSuccessStatusCode) throw new LibraryRefusedException((int)r.StatusCode, r.ReasonPhrase ?? "");
        using var reader = new StreamReader(await r.Content.ReadAsStreamAsync(stop));
        while (await reader.ReadLineAsync(stop) is { } line)
            if (line.Length > 0 && JsonNode.Parse(line) is JsonObject ev) onEvent(ev);
        return true;
    }

    /// <summary>Keep something jotted down (Capture): the library's AI files it under its class.</summary>
    public async Task<JsonObject?> CaptureAsync(string text, string? className = null) =>
        await SendAsync(HttpMethod.Post, "/capture", new JsonObject { ["text"] = text, ["class"] = className }) as JsonObject;

    /// <summary>Open a terminal with the AI in a class's folder, on the library's own computer. What happened.</summary>
    public async Task<string> TerminalAsync(string? className) =>
        (await SendAsync(HttpMethod.Post, "/terminal", new JsonObject { ["class"] = className }) as JsonObject)?["said"]?.GetValue<string>()
        ?? "Your library runs an older Study Stash.";

    /// <summary>A chat turn can take minutes (an AI reading around): its own client with no time limit.</summary>
    static readonly HttpClient ChatClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Which AI does the library's work: /api/v2/ai (GET, or POST a choice), and "/test" to try one.</summary>
    public async Task<JsonObject?> AiAsync(HttpMethod method, string path = "", JsonObject? body = null) =>
        await SendAsync(method, "/ai" + path, body) as JsonObject;
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
        + "or a long hex string; pass them to get_lecture and get_transcript. When Canvas is linked, due_assignments, "
        + "get_assignment, class_modules, class_files and class_announcements read what Study Stash already mirrors from "
        + "Canvas; canvas_api, canvas_page and canvas_download read Canvas directly and never other students' data.";

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

    static async Task<string> SearchFilesText(ILibrarySource lib, string query, int limit)
    {
        var hits = await lib.SearchFilesAsync(query, Math.Clamp(limit, 1, 30));
        return hits.Count == 0 ? "No files match." : string.Join("\n", hits.OfType<JsonObject>().Select(h => $"- {S(h["path"])} ({S(h["root"])}): {S(h["snippet"])}"));
    }

    static async Task<string> CanvasText(ILibrarySource lib, string what, JsonObject? body = null)
    {
        var r = await lib.CanvasAsync(what, body);
        if (r is JsonObject o && o["error"] is not null) return "Couldn't: " + S(o["error"]);
        if (r is JsonObject j && j["markdown"] is not null) return S(j["markdown"]);
        if (r is JsonObject api && api["json"] is not null)
        {
            string text = S(api["json"]);
            api.Remove("json");
            return api.ToJsonString() + "\n" + text;
        }
        return r.ToJsonString();
    }

    static string Num(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) ? d.ToString("0.##", CultureInfo.InvariantCulture) : "";

    static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) && b;

    static string FileLine(JsonObject f)
    {
        string name = S(f["name"]);
        string size = f["size"] is JsonValue sv && sv.TryGetValue(out long bytes) && bytes > 0 ? $" ({Canvas.CanvasMarkdown.Size(bytes)})" : "";
        string local = S(f["local"]), skipped = S(f["skipped"]), url = S(f["url"]);
        if (local.Length > 0) return $"- {name}{size}: {local}";
        if (skipped.Length > 0) return $"- {name}{size}: not saved ({skipped})" + (url.Length > 0 ? " — " + url : "");
        return $"- {name}{size}" + (url.Length > 0 ? ": " + url : "");
    }

    static string RubricLine(JsonObject c)
    {
        string crit = S(c["criterion"]), max = Num(c["points"]);
        if (c["mark"] is not JsonObject mark) return $"- {crit}: not yet marked ({max} pts)";
        string comment = S(mark["comment"]);
        return $"- {crit}: {Num(mark["points"])} / {max}" + (comment.Length > 0 ? " · " + comment : "");
    }

    /// <summary>The Due list, grouped and labelled the way the app shows it: overdue first, then everything else
    /// with a due date, then work with none.</summary>
    public static async Task<string> DueAssignmentsAsync(ILibrarySource lib, string? className, int days)
    {
        var r = await lib.CanvasAsync("assignments", new JsonObject { ["class"] = className, ["days"] = days });
        if (r is JsonObject err && err["error"] is not null) return "Couldn't: " + S(err["error"]);
        var items = (r as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (items.Count == 0) return className is null ? "Nothing due." : $"Nothing due in {className}.";
        var overdue = items.Where(o => S(o["status"]) is "past due" or "missing").ToList();
        var soon = items.Where(o => !overdue.Contains(o) && S(o["due"]).Length > 0).ToList();
        var undated = items.Where(o => !overdue.Contains(o) && S(o["due"]).Length == 0).ToList();
        string Row(JsonObject o)
        {
            string due = S(o["due"]), pts = Num(o["points"]), folder = S(o["folder"]);
            return $"- {S(o["class"])} · {S(o["name"])} · {S(o["label"])}"
                + (due.Length > 0 ? " · due " + Canvas.Assignments.Say(due, DateTime.Now) : "")
                + (pts.Length > 0 ? $" · {pts} pts" : "") + (folder.Length > 0 ? $" · {folder}" : "");
        }
        var sb = new StringBuilder();
        void Section(string label, List<JsonObject> list)
        {
            if (list.Count == 0) return;
            sb.Append(label).Append(":\n").Append(string.Join("\n", list.Select(Row))).Append("\n\n");
        }
        Section("Overdue", overdue);
        Section("Due soon", soon);
        Section("No due date", undated);
        return sb.ToString().TrimEnd();
    }

    static string FormatAssignment(JsonObject a)
    {
        var sb = new StringBuilder();
        sb.Append($"# {S(a["name"])} — {S(a["class"])}\n\n");
        string due = S(a["due_at"]).Length > 0 ? Canvas.CanvasMarkdown.When(S(a["due_at"]), TimeZoneInfo.Local) : "";
        sb.Append("Due: ").Append(due.Length > 0 ? due : "No due date").Append('\n');
        string pts = Num(a["points"]);
        if (pts.Length > 0) sb.Append("Points: ").Append(pts).Append('\n');
        sb.Append("Status: ").Append(S(a["label"]));
        if (S(a["score_text"]) is { Length: > 0 } score) sb.Append(" · ").Append(score);
        sb.Append('\n');
        if (a["submission"] is JsonObject subm && S(subm["submitted_at"]) is { Length: > 0 } submittedAt)
            sb.Append("Submitted: ").Append(Canvas.CanvasMarkdown.When(submittedAt, TimeZoneInfo.Local)).Append('\n');
        if (S(a["instructions"]) is { Length: > 0 } instructions)
            sb.Append("\nInstructions:\n").Append(Py.Head(instructions, 4000).Trim()).Append('\n');
        var files = (a["files"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (files.Count > 0) sb.Append("\nFiles:\n").Append(string.Join("\n", files.Select(FileLine))).Append('\n');
        var rubric = (a["rubric"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (rubric.Count > 0) sb.Append("\nRubric:\n").Append(string.Join("\n", rubric.Select(RubricLine))).Append('\n');
        if (a["submission"] is JsonObject sub2 && (sub2["files"] as JsonArray ?? []).OfType<JsonObject>().ToList() is { Count: > 0 } subFiles)
            sb.Append("\nSubmission:\n").Append(string.Join("\n", subFiles.Select(FileLine))).Append('\n');
        var comments = (a["comments"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (comments.Count > 0)
        {
            sb.Append("\nComments:\n");
            foreach (var c in comments)
            {
                string at = S(c["at"]);
                sb.Append($"- {S(c["author"])}").Append(at.Length > 0 ? $" ({Canvas.CanvasMarkdown.When(at, TimeZoneInfo.Local)})" : "").Append(": ").Append(S(c["text"])).Append('\n');
            }
        }
        if (S(a["spec"]) is { Length: > 0 } spec) sb.Append("\nSpec: ").Append(spec).Append('\n');
        if (S(a["feedback"]) is { Length: > 0 } feedback) sb.Append("Feedback: ").Append(feedback).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>One assignment's whole story: instructions, rubric with marks and comments, submission and files,
    /// grader comments with author and date. <paramref name="assignment"/> is its id, or words of its name (several
    /// matches are listed instead).</summary>
    public static async Task<string> GetAssignmentAsync(ILibrarySource lib, string class_name, string assignment)
    {
        var body = new JsonObject { ["class"] = class_name };
        if (long.TryParse(assignment, out long id)) body["id"] = id; else body["name"] = assignment;
        var r = await lib.CanvasAsync("assignment", body);
        if (r is not JsonObject o) return "Couldn't read that assignment.";
        if (o["error"] is not null) return "Couldn't: " + S(o["error"]);
        if (o["candidates"] is JsonArray cands)
            return "Several assignments match: " + string.Join("; ", cands.OfType<JsonObject>().Select(c => $"{S(c["name"])} (id {c["id"]})"));
        return FormatAssignment(o);
    }

    static string FormatModules(JsonObject m)
    {
        var modules = (m["modules"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (modules.Count == 0) return "No modules.";
        var sb = new StringBuilder();
        foreach (var mod in modules)
        {
            sb.Append("## ").Append(S(mod["name"])).Append('\n');
            foreach (var it in (mod["items"] as JsonArray ?? []).OfType<JsonObject>())
            {
                string kind = S(it["kind"]), source = S(it["source"]);
                string extra = kind == "file" && S(it["format"]) is { Length: > 0 } fmt ? $" ({fmt})"
                    : kind == "link" ? source.Length > 0 ? $" (saved from {char.ToUpperInvariant(source[0])}{source[1..]})" : Bool(it["saved"]) ? " (saved)" : " (link)"
                    : "";
                sb.Append("- ").Append(S(it["title"])).Append(extra).Append(Bool(it["locked"]) ? " · locked" : "").Append('\n');
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static async Task<string> ClassModulesAsync(ILibrarySource lib, string class_name)
    {
        var r = await lib.CanvasAsync("modules", new JsonObject { ["class"] = class_name });
        if (r is JsonObject err && err["error"] is not null) return "Couldn't: " + S(err["error"]);
        return r is JsonObject m ? FormatModules(m) : "Couldn't read this class's modules.";
    }

    static string FileRow(JsonObject f)
    {
        string folder = S(f["folder"]);
        string name = (folder.Length > 0 ? folder + "/" : "") + S(f["name"]);
        string size = f["size"] is JsonValue sv && sv.TryGetValue(out long bytes) && bytes > 0 ? $" ({Canvas.CanvasMarkdown.Size(bytes)})" : "";
        string local = S(f["local"]);
        return $"- {name}{size}" + (local.Length > 0 ? $": {local}" : "");
    }

    /// <summary>The class's Files area, with its own words when Canvas hides it from the student.</summary>
    public static async Task<string> ClassFilesAsync(ILibrarySource lib, string class_name)
    {
        var r = await lib.CanvasAsync("files", new JsonObject { ["class"] = class_name });
        if (r is JsonObject err && err["error"] is not null) return "Couldn't: " + S(err["error"]);
        if (r is not JsonObject f) return "Couldn't read this class's files.";
        bool allowed = f["allowed"] is not JsonValue av || !av.TryGetValue(out bool a) || a;
        var files = (f["files"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        string header = allowed ? "" : "Canvas hides this class's Files area from the student; showing what's known from modules and links.\n";
        if (files.Count == 0) return header.Length > 0 ? header.TrimEnd() : "No files.";
        return header + string.Join("\n", files.Select(FileRow));
    }

    /// <summary>A class's announcements, newest first, with bodies cut short.</summary>
    public static async Task<string> ClassAnnouncementsAsync(ILibrarySource lib, string class_name, int limit)
    {
        var r = await lib.CanvasAsync("announcements", new JsonObject { ["class"] = class_name, ["limit"] = Math.Clamp(limit, 1, 30) });
        if (r is JsonObject err && err["error"] is not null) return "Couldn't: " + S(err["error"]);
        if (r is not JsonObject a) return "Couldn't read this class's announcements.";
        var items = (a["items"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (items.Count == 0) return "No announcements.";
        var sb = new StringBuilder();
        foreach (var it in items)
        {
            string when = Canvas.CanvasMarkdown.When(S(it["posted_at"]), TimeZoneInfo.Local);
            sb.Append($"- {S(it["title"])} — {S(it["author"])}, {when}").Append(Bool(it["new"]) ? " (new)" : "").Append('\n');
            if (S(it["body"]) is { Length: > 0 } body) sb.Append("  ").Append(Py.Head(body, 300).ReplaceLineEndings(" ").Trim()).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public static List<McpServerTool> Tools(ILibrarySource lib)
    {
        static McpServerToolCreateOptions Named(string name, string title, string description, bool readOnly = true) =>
            new() { Name = name, Title = title, Description = description, ReadOnly = readOnly, Idempotent = true, Destructive = false, OpenWorld = !readOnly };
        List<McpServerTool> tools =
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
        if (lib.HasCanvas)
            tools.Add(McpServerTool.Create(
                ([Description("Words to look for.")] string query, [Description("How many files (1 to 30).")] int limit = 10) => SearchFilesText(lib, query, limit),
                Named("search_files", "Search files", "Full-text search of everything in the library that isn't a lecture (Canvas files and "
                    + "pages, slides, PDFs, study guides) and the folders the user lets Study Stash read. Returns each file's full path, "
                    + "to open with your file tools.")));
        if (lib.HasCanvas)
            tools.AddRange(
            [
                McpServerTool.Create(
                    ([Description("Only this class. Leave out for every class.")] string? class_name = null,
                     [Description("Due within this many days (overdue ones are included).")] int days = 14) =>
                        DueAssignmentsAsync(lib, class_name, days),
                    Named("due_assignments", "What's due", "Canvas assignments still to hand in, grouped (Overdue, Due soon, No due date) and "
                        + "labelled the way the app shows them, soonest first: class, name, status, due date, points, and the folder in the "
                        + "class with its instructions (spec.md) and any feedback (feedback.md).")),
                McpServerTool.Create(() => CanvasText(lib, "courses"),
                    Named("canvas_courses", "Canvas courses", "The classes linked to Canvas: each one's Canvas course id, and whether a "
                        + "course recipe (Canvas/canvas-recipe.md: where this instructor puts things) has been written.")),
                McpServerTool.Create(
                    ([Description("A class from list_classes.")] string class_name,
                     [Description("The assignment's id, or words of its name (\"problem set 4\"). Several matches are listed instead.")] string assignment) =>
                        GetAssignmentAsync(lib, class_name, assignment),
                    Named("get_assignment", "Read an assignment", "One assignment's whole story: instructions, points, due date, status, rubric "
                        + "with your marks and the grader's comments, what you submitted and its files, and the grader's comments with author "
                        + "and date.")),
                McpServerTool.Create(([Description("A class from list_classes.")] string class_name) => ClassModulesAsync(lib, class_name),
                    Named("class_modules", "Read a class's modules", "A class's Canvas modules, in order: each item's kind (file, page, "
                        + "assignment, quiz, discussion, link, tool) and whether it's saved locally.")),
                McpServerTool.Create(([Description("A class from list_classes.")] string class_name) => ClassFilesAsync(lib, class_name),
                    Named("class_files", "Read a class's Files area", "A class's Canvas Files area (folders and files, with sizes and local "
                        + "paths), or says when Canvas hides it from the student.")),
                McpServerTool.Create(
                    ([Description("A class from list_classes.")] string class_name, [Description("How many, newest first (1 to 30).")] int limit = 10) =>
                        ClassAnnouncementsAsync(lib, class_name, limit),
                    Named("class_announcements", "Read a class's announcements", "A class's Canvas announcements, newest first, with the "
                        + "author, date, whether it's new, and the body cut short.")),
                McpServerTool.Create(
                    ([Description("A Canvas REST API path, like /api/v1/courses/123/modules?include[]=items&per_page=100.")] string path) =>
                        CanvasText(lib, "fetch", new JsonObject { ["url"] = path, ["kind"] = "json" }),
                    Named("canvas_api", "Read Canvas's API", "GET a Canvas REST API path, through the user's own Chrome sign-in (read-only). "
                        + "Returns JSON text; when there's more, next_page is the address to read next. Useful paths: /api/v1/courses/<id>/pages, "
                        + "/api/v1/courses/<id>/front_page, /api/v1/courses/<id>?include[]=syllabus_body, /api/v1/courses/<id>/files, "
                        + "/api/v1/courses/<id>/discussion_topics. Refuses paths that would read other people's data (rosters, other "
                        + "students' posts, conversations).")),
                McpServerTool.Create(
                    ([Description("A Canvas web page, like /courses/123 or /courses/123/pages/syllabus.")] string url) =>
                        CanvasText(lib, "fetch", new JsonObject { ["url"] = url, ["kind"] = "text" }),
                    Named("canvas_page", "Read a Canvas page", "A Canvas web page as Markdown text.")),
                McpServerTool.Create(
                    ([Description("The file's download address on Canvas (a file object's url).")] string url,
                     [Description("Where to keep it: \"<class>/<path in its folder>\", like \"CS 101/Canvas/files/Week 1/slides.pdf\".")] string save_to) =>
                        CanvasText(lib, "fetch", new JsonObject { ["url"] = url, ["kind"] = "bytes", ["save_to"] = save_to }),
                    Named("canvas_download", "Save a Canvas file", "Download a Canvas file into a class's folder in the library.", readOnly: false)),
            ]);
        return tools;
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
