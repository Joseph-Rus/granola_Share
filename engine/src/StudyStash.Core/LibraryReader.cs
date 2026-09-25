using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>
/// The library as the Study Stash app and Claude read it: its classes, lectures, notes, transcripts, search, and
/// answers from your notes. Every answer is plain JSON, the same over /api/v2 and from the MCP tools.
/// </summary>
public sealed partial class LibraryReader(Config cfg, Store store)
{
    public Config Cfg { get; } = cfg;
    public Store Store { get; } = store;

    /// <summary>The library's name, its classes (in the order they were added: their dot colors follow it), and how
    /// much is waiting.</summary>
    public JsonObject Overview()
    {
        var counts = Store.ClassesSummary().ToDictionary(c => c.ClassName, c => c.Count);
        var classes = new JsonArray();
        int index = 0;
        foreach (var c in Cfg.Classes)
            classes.Add(new JsonObject { ["name"] = c.Name, ["lectures"] = counts.GetValueOrDefault(c.Name), ["color"] = index++ });
        // Lectures filed under a class since removed from the config still show, after the rest.
        foreach (var (name, n) in counts.Where(kv => kv.Key != Configs.Unsorted && Cfg.Classes.All(c => c.Name != kv.Key)))
            classes.Add(new JsonObject { ["name"] = name, ["lectures"] = n, ["color"] = index++ });
        var status = Store.StatusCounts();
        return new JsonObject
        {
            ["name"] = Cfg.PoolName,
            ["version"] = Engine.Version,
            ["classes"] = classes,
            ["unsorted"] = counts.GetValueOrDefault(Configs.Unsorted),
            ["writing"] = status.GetValueOrDefault(Store.Queued) + status.GetValueOrDefault(Store.Working),
            ["ask"] = Cfg.OllamaEnabled,
            ["notes_model"] = Cfg.SummaryEnabled && Cfg.OllamaEnabled ? Cfg.EffectiveSummaryModel : null,
        };
    }

    /// <summary>Lectures newest first, in one class (or Unsorted) or all, before a date to page back.</summary>
    public JsonArray Lectures(string? className = null, int limit = 50, string? before = null)
    {
        var rows = Store.ListNotes(className);
        if (!string.IsNullOrEmpty(before)) rows = rows.Where(r => string.CompareOrdinal(r.Date ?? "", before) < 0).ToList();
        return new JsonArray(rows.Take(Math.Clamp(limit, 1, 500)).Select(r => (JsonNode?)Summary(r)).ToArray());
    }

    public JsonObject? Lecture(string id)
    {
        var row = Store.Get(id);
        if (row is null) return null;
        var m = Store.Meeting(row);
        var result = Summary(row);
        result["notes"] = NotesOf(row, m);
        result["transcript"] = m.Transcript;
        result["classified_by"] = row.ClassifiedBy;
        result["notes_model"] = row.SummaryModel;
        result["error"] = row.Error;
        return result;
    }

    /// <summary>The notes a lecture shows: ours, written from the transcript, or else the ones it came with.</summary>
    static string NotesOf(NoteRow row, Meeting m) => !string.IsNullOrEmpty(row.SummaryMd) ? row.SummaryMd : m.NotesMarkdown;

    JsonObject Summary(NoteRow r)
    {
        var raw = JsonNode.Parse(string.IsNullOrEmpty(r.RawJson) ? "{}" : r.RawJson) as JsonObject;
        double? seconds = raw?["seconds"] is JsonValue v && v.TryGetValue(out double s) ? s : null;
        string notes = r.SummaryMd ?? "";
        if (notes.Length == 0 || seconds is null)
        {
            var m = Store.Meeting(r);
            if (notes.Length == 0) notes = m.NotesMarkdown;
            if (seconds is null && TimedText.HasTimes(m.Transcript)) seconds = TimedText.Parse(m.Transcript).LastOrDefault()?.End;
        }
        string status = string.IsNullOrEmpty(r.Status) ? Store.Done : r.Status;
        return new JsonObject
        {
            ["id"] = r.Id,
            ["title"] = !string.IsNullOrEmpty(r.LectureTitle) ? r.LectureTitle : r.Title,
            ["class"] = r.ClassName,
            ["date"] = r.Date,
            ["seconds"] = seconds,
            ["owner"] = r.Owner,
            ["status"] = status,
            ["summary"] = FirstSentence(notes),
            ["topics"] = JsonNode.Parse(string.IsNullOrEmpty(r.Topics) ? "[]" : r.Topics),
            ["has_transcript"] = r.HasTranscript is > 0,
        };
    }

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9“""(])")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\*\*|__|`|\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Markup();

    /// <summary>The lecture in a line: the first sentence of its Summary (or Overview), for lists.</summary>
    public static string FirstSentence(string notes)
    {
        string text = "";
        foreach (string heading in new[] { "Summary", "Overview" })
            if ((text = Notes.Section(notes, heading)).Length > 0) break;
        if (text.Length == 0)
            text = string.Join(" ", notes.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).SkipWhile(l => l.Length == 0 || l.StartsWith('#'))
                .TakeWhile(l => l.Length > 0 && !l.StartsWith('#')));
        text = Markup().Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value : "").Replace('\n', ' ').Trim();
        string first = SentenceEnd().Split(text).FirstOrDefault() ?? "";
        return first.Length > 200 ? first[..197].TrimEnd() + "…" : first;
    }

    /// <summary>What the search box finds as you type: lectures by title and topic, passages from notes and
    /// transcripts, and classes by name.</summary>
    public JsonObject Search(string query, string? className = null, int limit = 8)
    {
        query = query.Trim();
        var result = new JsonObject { ["query"] = query, ["lectures"] = new JsonArray(), ["passages"] = new JsonArray(), ["classes"] = new JsonArray() };
        if (query.Length == 0) return result;
        var lectures = (JsonArray)result["lectures"]!;
        foreach (var r in Store.Search(query, 50).Where(r => className is null || r.ClassName == className).Take(limit)) lectures.Add(Summary(r));
        var passages = (JsonArray)result["passages"]!;
        if (Passages.AllWords(query) is string match)
            foreach (var hit in Store.SearchPassages(match, className, limit: limit * 2).Take(limit * 2)) passages.Add(PassageJson(hit.Note, hit.Passage));
        var classes = (JsonArray)result["classes"]!;
        foreach (var c in ((JsonArray)Overview()["classes"]!).OfType<JsonObject>())
            if (c["name"]!.GetValue<string>().Contains(query, StringComparison.OrdinalIgnoreCase)) classes.Add(c.DeepClone());
        return result;
    }

    static JsonObject PassageJson(NoteRow note, Passage p) => new()
    {
        ["id"] = note.Id,
        ["title"] = !string.IsNullOrEmpty(note.LectureTitle) ? note.LectureTitle : note.Title,
        ["class"] = note.ClassName,
        ["date"] = note.Date,
        ["kind"] = p.Kind,
        ["section"] = p.Section,
        ["at"] = p.Start,
        ["text"] = p.Text,
    };

    // --- asking your notes ----------------------------------------------------------------------------------------

    /// <summary>(prompt, JSON schema) → the model's answer, as JSON text.</summary>
    public delegate Task<string> AskChatFn(string prompt, JsonObject schema);

    public const string NoAnswer = "I couldn't find that in your notes.";

    static readonly JsonObject AnswerSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["answer"] = new JsonObject { ["type"] = "string" },
            ["sources"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "integer" } },
        },
        ["required"] = new JsonArray("answer", "sources"),
    };

    /// <summary>
    /// A question answered from your own lectures by the library's model: the passages that best match it (in one
    /// lecture, one class, or all), and while you record, what's been said so far. The answer names its sources, each
    /// with the moment in the recording when there is one.
    /// </summary>
    public async Task<JsonObject> AskAsync(string question, string? lectureId = null, string? className = null,
        string? liveTranscript = null, string liveTitle = "This lecture", AskChatFn? chat = null)
    {
        var picked = new List<(NoteRow? Note, Passage Passage)>();
        if (!string.IsNullOrWhiteSpace(liveTranscript))
            foreach (var p in BestOf(Passages.FromTranscript("live", liveTranscript), question, 6)) picked.Add((null, p));
        if (lectureId is not null && Store.Get(lectureId) is { } one)
        {
            foreach (var p in BestOf(Store.PassagesOf(lectureId), question, 8)) picked.Add((one, p));
        }
        else if (Passages.AnyWords(question) is string match)
        {
            foreach (var hit in Store.SearchPassages(match, className, limit: 10)) picked.Add((hit.Note, hit.Passage));
        }
        var sources = new JsonArray();
        if (picked.Count == 0) return new JsonObject { ["answer"] = NoAnswer, ["sources"] = sources };

        var prompt = new StringBuilder("""
            You answer a student's question from their own lecture notes and transcripts, numbered below.
            Use only these sources. If they don't answer the question, say so in one sentence.
            Answer in one to three plain sentences, the way a classmate who took good notes would. Don't mention the sources by number in the answer.
            Return JSON: answer (the answer), sources (the numbers of the sources you used, most important first).

            """);
        for (int i = 0; i < picked.Count; i++)
        {
            var (note, p) = picked[i];
            string where = note is null ? liveTitle : $"{Title(note)} ({note.ClassName}, {note.Date})";
            string at = p.Start is double s ? $" at {TimedText.Clock(s)}" : p.Section.Length > 0 ? $", {p.Section}" : "";
            prompt.Append('[').Append(i + 1).Append("] ").Append(where).Append(at).Append(":\n").Append(p.Text).Append("\n\n");
        }
        prompt.Append("Question: ").Append(question.Trim());
        string text = await (chat ?? OllamaAsk)(prompt.ToString(), AnswerSchema);
        string answer;
        List<int> used;
        try
        {
            var data = JsonNode.Parse(text) as JsonObject;
            answer = Py.Strip(Py.AsString(data?["answer"]) ?? "");
            used = (data?["sources"] as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue(out int k) ? k : 0).ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            answer = Py.Strip(text);
            used = [];
        }
        if (answer.Length == 0) answer = NoAnswer;
        foreach (int k in used.Where(k => k >= 1 && k <= picked.Count).Distinct())
        {
            var (note, p) = picked[k - 1];
            sources.Add(new JsonObject
            {
                ["id"] = note?.Id, ["title"] = note is null ? liveTitle : Title(note), ["class"] = note?.ClassName,
                ["date"] = note?.Date, ["at"] = p.Start, ["section"] = p.Section, ["text"] = p.Text,
            });
        }
        return new JsonObject { ["answer"] = answer, ["sources"] = sources };
    }

    static string Title(NoteRow n) => !string.IsNullOrEmpty(n.LectureTitle) ? n.LectureTitle : n.Title ?? "";

    /// <summary>A lecture's passages that share the most words with the question (ties keep the order they came in).</summary>
    static IEnumerable<Passage> BestOf(List<Passage> passages, string question, int n)
    {
        var words = Passages.Words(question).Where(w => w.Length > 2).ToHashSet();
        return passages.Select((p, i) => (p, i, score: Passages.Words(p.Text).Count(words.Contains)))
            .OrderByDescending(x => x.score).ThenBy(x => x.i).Take(n).OrderBy(x => x.i).Select(x => x.p);
    }

    Task<string> OllamaAsk(string prompt, JsonObject schema) => OllamaAskAsync(Cfg, prompt, schema);

    /// <summary>An answer from the library's own Ollama model.</summary>
    public static async Task<string> OllamaAskAsync(Config Cfg, string prompt, JsonObject schema)
    {
        if (!Cfg.OllamaEnabled) throw new InvalidOperationException("Asking needs the library's AI model: turn it on in the library's Settings.");
        var body = new JsonObject
        {
            ["model"] = Cfg.EffectiveSummaryModel,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["stream"] = false,
            ["think"] = false,
            ["format"] = schema.DeepClone(),
            ["options"] = new JsonObject { ["temperature"] = 0.1, ["num_ctx"] = 8192, ["num_predict"] = 400 },
        };
        var data = await Ollama.PostAsync(Cfg.OllamaHost, "/api/chat", body, TimeSpan.FromSeconds(120));
        return Py.AsString(data?["message"]?["content"]) ?? "";
    }
}
