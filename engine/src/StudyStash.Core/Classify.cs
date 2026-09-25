using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>(cfg, prompt, JSON schema) → the model's answer: the sorting call.</summary>
public delegate Task<string> SortChatFn(Config cfg, string prompt, JsonObject schema);

/// <summary>
/// Decide which class a note belongs in. The student's own Granola folder or title wins; otherwise a local
/// Ollama model answers against a strict JSON schema; otherwise Unsorted.
/// </summary>
public static partial class Classify
{
    public const int MaxNoteChars = 12000; // ~3k tokens; fits the context below with room for the prompt

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotWordChars();

    internal static string Norm(string? s) => Py.Strip(NotWordChars().Replace((s ?? "").ToLowerInvariant(), " "));

    public static Classification? ByRules(Meeting m, IReadOnlyList<ClassDef> classes)
    {
        string folder = Norm(m.Folder);
        string title = Norm(m.Title);
        foreach (var c in classes)
        {
            foreach (string n in c.Aliases.Prepend(c.Name))
            {
                string nn = Norm(n);
                if (nn.Length == 0) continue;
                if (folder.Length > 0 && (folder == nn || folder.Contains(nn, StringComparison.Ordinal)))
                    return new Classification(c.Name, 0.95, "folder", m.Title);
            }
        }
        foreach (var c in classes)
        {
            foreach (string n in c.Aliases.Prepend(c.Name))
            {
                string nn = Norm(n);
                if (nn.Length >= 3 && Regex.IsMatch(title, $@"\b{Regex.Escape(nn)}\b"))
                    return new Classification(c.Name, 0.85, "rules", m.Title);
            }
        }
        return null;
    }

    public static JsonObject Schema(IEnumerable<string> classNames) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["class_name"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(classNames.Append(Configs.Unsorted).Select(n => (JsonNode?)n).ToArray()),
            },
            ["confidence"] = new JsonObject { ["type"] = "number" },
            ["lecture_title"] = new JsonObject { ["type"] = "string" },
            ["topics"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
        },
        ["required"] = new JsonArray("class_name", "confidence", "lecture_title", "topics"),
    };

    public static string BuildPrompt(Meeting m, IReadOnlyList<ClassDef> classes)
    {
        var lines = new List<string> { "You file lecture notes into the right class. Classes:" };
        foreach (var c in classes)
        {
            string extra = c.Aliases.Count > 0 ? $" (also called: {string.Join(", ", c.Aliases)})" : "";
            string desc = c.Description.Length > 0 ? $" \u2014 {c.Description}" : "";
            lines.Add($"- {c.Name}{extra}{desc}");
        }
        string notes = m.NotesMarkdown.Length > 0 ? m.NotesMarkdown : m.Transcript;
        lines.AddRange([
            $"- {Configs.Unsorted} \u2014 use this when the note is not a lecture for any class above, or you are unsure.",
            "",
            "Return JSON with: class_name (one of the classes above), confidence (0 to 1),",
            "lecture_title (a short specific title for this lecture), topics (3-6 short topic tags).",
            "",
            $"Title: {m.Title}",
            $"Date: {m.Date}",
            $"Granola folder: {(m.Folder.Length > 0 ? m.Folder : "(none)")}",
            $"Attendees: {(m.Attendees.Count > 0 ? string.Join(", ", m.Attendees) : "(none)")}",
            "",
            "Notes:",
            Py.Head(notes, MaxNoteChars),
        ]);
        return string.Join("\n", lines);
    }

    public static async Task<int> SortContextAsync(Config cfg)
    {
        // Same model as the summarizer: ask for the same context, or Ollama reloads it between the two calls.
        if (cfg.SummaryEnabled && cfg.OllamaModel == cfg.EffectiveSummaryModel)
            return await Summarize.ContextSizeAsync(cfg, cfg.OllamaModel);
        return 8192;
    }

    public static async Task<string> OllamaChatAsync(Config cfg, string prompt, JsonObject schema)
    {
        var body = new JsonObject
        {
            ["model"] = cfg.OllamaModel,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["stream"] = false,
            ["format"] = schema.DeepClone(),
            ["think"] = false,
            // The answer is a small JSON object: a cap stops a model that loops from running on for minutes.
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_ctx"] = await SortContextAsync(cfg), ["num_predict"] = 512 },
        };
        var data = await Ollama.PostAsync(cfg.OllamaHost, "/api/chat", body, TimeSpan.FromSeconds(300));
        if (Py.AsString(data?["done_reason"]) == "length")
            throw new RunawayOutputException("the sorting answer ran past 512 tokens"); // WithOllama then falls back
        return Py.AsString(data?["message"]?["content"]) ?? throw new InvalidDataException("Ollama sent no message");
    }

    public static async Task<Classification?> WithOllamaAsync(Meeting m, IReadOnlyList<ClassDef> classes, Config cfg,
        SortChatFn? chat = null)
    {
        if (classes.Count == 0) return null;
        var names = classes.Select(c => c.Name).ToList();
        JsonObject data;
        try
        {
            string content = await (chat ?? OllamaChatAsync)(cfg, BuildPrompt(m, classes), Schema(names));
            if (JsonNode.Parse(content) is not JsonObject o) return null;
            data = o;
        }
        catch (Exception)
        {
            return null; // Ollama down, a timeout, or an answer that isn't JSON: the note goes to Unsorted
        }
        string? name = Py.AsString(data["class_name"]);
        if (name is null || !(names.Contains(name) || name == Configs.Unsorted)) return null;
        double conf = Math.Clamp(Confidence(data["confidence"]), 0.0, 1.0);
        var topics = (data["topics"] as JsonArray ?? new JsonArray()).Select(Py.Str).Take(8).ToList();
        string title = Py.Truthy(data["lecture_title"]) ? Py.Str(data["lecture_title"]) : m.Title;
        if (name == Configs.Unsorted || conf < cfg.MinConfidence)
            return new Classification(Configs.Unsorted, conf, "ollama", title, topics);
        return new Classification(name, conf, "ollama", title, topics);
    }

    /// <summary>float(data.get("confidence", 0)), and 0 when that fails.</summary>
    static double Confidence(JsonNode? v)
    {
        switch (v)
        {
            case null:
                return 0;
            case JsonValue n when n.GetValueKind() == JsonValueKind.Number:
                return Py.NumberValue(n);
            case JsonValue b when b.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                return b.GetValueKind() == JsonValueKind.True ? 1 : 0;
            case JsonValue s when s.GetValueKind() == JsonValueKind.String:
                return double.TryParse(Py.Strip(s.GetValue<string>()), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    && !double.IsNaN(d) ? d : 0;
            default:
                return 0;
        }
    }

    public static async Task<Classification> ClassifyAsync(Meeting m, Config cfg, SortChatFn? chat = null)
    {
        var c = ByRules(m, cfg.Classes);
        if (c is not null) return c;
        if (cfg.OllamaEnabled && cfg.Classes.Count > 0)
        {
            c = await WithOllamaAsync(m, cfg.Classes, cfg, chat);
            if (c is not null) return c;
        }
        return new Classification(Configs.Unsorted, 0.0, "none", m.Title);
    }
}
