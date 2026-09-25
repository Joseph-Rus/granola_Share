using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>(cfg, model, prompt, num_ctx) → the model's answer.</summary>
public delegate Task<string> ChatFn(Config cfg, string model, string prompt, int numCtx);

/// <summary>(cfg, model) → the model's own context length, or null when Ollama doesn't say.</summary>
public delegate Task<int?> ShowFn(Config cfg, string model);

/// <summary>The model didn't finish properly: it ran to the length cap, or repeated itself.</summary>
public sealed class RunawayOutputException(string message) : Exception(message);

/// <summary>
/// Write lecture notes from the transcript with a local Ollama model. Granola's own summary comes from whatever
/// model Granola runs; this writes the notes with the model you pick, from the raw transcript. Transcripts longer
/// than the model's context are summarized in parts, then merged.
/// </summary>
public static partial class Summarize
{
    public const double CharsPerToken = 3.5; // rough, for English speech
    // Below this (a couple of minutes of speech) there's too little to fill the notes: small models then pad and
    // loop. Such lectures keep Granola's own summary.
    public const int MinTranscriptChars = 1500;
    // Good notes are well under 2,000 tokens. Without a cap, a small model that starts repeating itself never
    // stops: Ollama keeps shifting its context and generating (39,000 tokens seen, from llama3.2:3b).
    public const int MaxNotesTokens = 4096;

    // .ReplaceLineEndings: on Windows, git checks this file out with "\r\n", which would change the prompts.
    public static readonly string Structure = """
        Use exactly this structure, and skip any section the lecture has nothing for:

        ## Summary
        Two to four sentences: what the lecture covered and how it fits the course.

        ## Key points
        Bullets: the ideas to remember, each in one or two sentences, the way the lecturer explained them.

        ## Definitions
        Bullets: the bold term, a colon, then what it means in one sentence.

        ## Details and examples
        Worked examples, derivations, formulas (LaTeX in $...$), code, and demonstrations, in the order they were taught.

        ## Announcements
        Deadlines, exams, assignments, and readings, with dates exactly as said.

        ## Questions to review
        Three to five numbered questions a student should be able to answer after this lecture.
        """.ReplaceLineEndings("\n");

    public static readonly string Rules = """
        Rules:
        - Use only what is in the transcript. Never invent facts, dates, or examples.
        - The transcript comes from speech recognition: fix obvious mis-hearings of technical terms, and skip filler, small talk, and audio problems.
        - Write in the language of the lecture. Output Markdown only, with no preamble.
        """.ReplaceLineEndings("\n");

    public static async Task<string> OllamaGenerateAsync(Config cfg, string model, string prompt, int numCtx,
        HttpClient? http = null)
    {
        int numPredict = Math.Min(MaxNotesTokens, numCtx / 2);
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["stream"] = false,
            ["think"] = false,
            // A mild repeat penalty: stronger ones mangle Markdown, whose bullets legitimately repeat.
            ["options"] = new JsonObject
            {
                ["temperature"] = 0.2, ["num_ctx"] = numCtx, ["num_predict"] = numPredict,
                ["repeat_penalty"] = 1.1, ["repeat_last_n"] = 256,
            },
        };
        // A long lecture on a big model, or a slow computer, takes minutes; fine, this runs in the background.
        // Output is capped, so this only has to cover reading the transcript plus 4,096 tokens.
        var data = await Ollama.PostAsync(cfg.OllamaHost, "/api/chat", body, TimeSpan.FromSeconds(900), http);
        if (Py.AsString(data?["done_reason"]) == "length")
            throw new RunawayOutputException($"{model} kept writing past {numPredict} tokens without finishing "
                + "(small models sometimes loop), so Granola's notes stay");
        return Py.AsString(data?["message"]?["content"]) ?? throw new InvalidDataException("Ollama sent no message");
    }

    /// <summary>The same line over and over: a model stuck in a loop, not notes.</summary>
    public static bool Repetitive(string text, int times = 5)
    {
        var lines = Py.SplitLines(text).Select(Py.Strip).Where(l => l.Length >= 12).ToList();
        if (lines.Count == 0) return false;
        var counts = lines.GroupBy(l => l, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
        return counts.Values.Max() >= times || (lines.Count >= 20 && counts.Count < lines.Count / 2.0);
    }

    /// <summary>The model's own context length, from Ollama's /api/show.</summary>
    public static async Task<int?> ModelContextAsync(Config cfg, string model)
    {
        try
        {
            var data = await Ollama.PostAsync(cfg.OllamaHost, "/api/show", new JsonObject { ["model"] = model },
                TimeSpan.FromSeconds(10));
            if (data?["model_info"] is JsonObject info)
                foreach (var (key, value) in info)
                    if (key.EndsWith(".context_length", StringComparison.Ordinal) && value is JsonValue v)
                        return v.GetValueKind() == JsonValueKind.Number ? (int)v.GetValue<double>() : int.Parse(Py.Str(v));
        }
        catch (Exception)
        {
            // Ollama not running, an old version, or a model it doesn't know: use our own cap.
        }
        return null;
    }

    public static async Task<int> ContextSizeAsync(Config cfg, string model, ShowFn? show = null)
    {
        int cap = Math.Max(4096, cfg.SummaryMaxContext);
        int? native = await (show ?? ModelContextAsync)(cfg, model);
        return native is int n && n != 0 ? Math.Min(cap, n) : cap;
    }

    /// <summary>Characters of transcript that fit in one call, leaving room for instructions and the answer.</summary>
    public static int TranscriptBudget(int ctx)
    {
        int reserved = Math.Min(4096, ctx / 3);
        return (int)((ctx - reserved) * CharsPerToken);
    }

    public static bool WantsSummary(Meeting m, Config cfg) =>
        cfg.OllamaEnabled && cfg.SummaryEnabled && Py.Strip(m.Transcript).Length >= MinTranscriptChars;

    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex Thinking();

    [GeneratedRegex(@"\A```(?:markdown|md)?\s*\n(.*)\n```\z", RegexOptions.Singleline)]
    private static partial Regex Fenced();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceEnd();

    public static string CleanOutput(string? text)
    {
        string t = Py.Strip(Thinking().Replace(text ?? "", ""));
        var fence = Fenced().Match(t);
        t = Py.Strip(fence.Success ? fence.Groups[1].Value : t);
        if (Repetitive(t))
            throw new RunawayOutputException("the model repeated itself instead of writing notes, so Granola's notes stay");
        return t;
    }

    /// <summary>Break one over-long line at sentence ends, then at spaces, then anywhere.</summary>
    internal static List<string> Pieces(string line, int maxChars)
    {
        if (line.Length <= maxChars) return [line];
        var output = new List<string>();
        string cur = "";
        foreach (string s in SentenceEnd().Split(line))
        {
            string sentence = s;
            while (sentence.Length > maxChars)
            {
                int cut = sentence.LastIndexOf(' ', maxChars - 1);
                cut = cut > maxChars / 2 ? cut : maxChars;
                string head = sentence[..cut];
                sentence = Py.LStrip(sentence[cut..]);
                if (cur.Length > 0)
                {
                    output.Add(cur);
                    cur = "";
                }
                output.Add(head);
            }
            if (cur.Length > 0 && cur.Length + 1 + sentence.Length > maxChars)
            {
                output.Add(cur);
                cur = "";
            }
            cur = cur.Length > 0 ? $"{cur} {sentence}" : sentence;
        }
        if (cur.Length > 0) output.Add(cur);
        return output;
    }

    /// <summary>Split on line breaks into parts of at most maxChars.</summary>
    public static List<string> SplitTranscript(string text, int maxChars)
    {
        var parts = new List<string>();
        var cur = new List<string>();
        int size = 0;
        foreach (string raw in Py.SplitLines(text))
        {
            foreach (string line in Pieces(raw, maxChars))
            {
                if (cur.Count > 0 && size + line.Length + 1 > maxChars)
                {
                    parts.Add(string.Join("\n", cur));
                    cur = [];
                    size = 0;
                }
                cur.Add(line);
                size += line.Length + 1;
            }
        }
        if (cur.Count > 0) parts.Add(string.Join("\n", cur));
        return parts.Where(p => Py.Strip(p).Length > 0).ToList();
    }

    static string Header(Meeting m)
    {
        var lines = new List<string> { $"Lecture: {m.Title}", $"Date: {Py.Head(m.Date, 10)}" };
        if (m.Folder.Length > 0) lines.Add($"Granola folder: {m.Folder}");
        return string.Join("\n", lines);
    }

    public static string WholePrompt(Meeting m, string transcript) =>
        "You are an expert note-taker for university lectures. Write study notes for this lecture "
        + $"from its transcript.\n\n{Structure}\n\n{Rules}\n\n{Header(m)}\n\nTranscript:\n{transcript}";

    public static string PartPrompt(Meeting m, string part, int i, int n) =>
        $"You are taking notes on part {i} of {n} of a lecture transcript. Write detailed Markdown bullet "
        + "notes for this part only: every concept, definition, example, formula, and announcement, in order. "
        + "They will be merged with the other parts later, so write no introduction or conclusion.\n\n"
        + $"{Rules}\n\n{Header(m)}\n\nTranscript part {i} of {n}:\n{part}";

    public static string CondensePrompt(Meeting m, IReadOnlyList<string> notes)
    {
        string joined = string.Join("\n\n", notes.Select((n, i) => $"### Notes {i + 1}\n{n}"));
        return "Combine these notes on consecutive parts of one lecture into one set of detailed Markdown bullet "
            + $"notes. Remove repetition but keep every distinct fact.\n\n{Rules}\n\n{Header(m)}\n\n{joined}";
    }

    public static string MergePrompt(Meeting m, IReadOnlyList<string> notes)
    {
        string joined = string.Join("\n\n", notes.Select((n, i) => $"### Part {i + 1}\n{n}"));
        return "You are an expert note-taker for university lectures. Below are notes on consecutive parts of one "
            + "lecture. Merge them into one set of study notes, removing repetition and keeping every distinct "
            + $"fact.\n\n{Structure}\n\n{Rules}\n\n{Header(m)}\n\n{joined}";
    }

    /// <summary>Our own study notes for a lecture, written from its transcript.</summary>
    public static async Task<string> SummarizeTranscriptAsync(Meeting m, Config cfg, ChatFn? chat = null, ShowFn? show = null)
    {
        chat ??= (c, model, prompt, ctx) => OllamaGenerateAsync(c, model, prompt, ctx);
        string model = cfg.EffectiveSummaryModel;
        int ctx = await ContextSizeAsync(cfg, model, show);
        int budget = TranscriptBudget(ctx);
        string text = Py.Strip(TimedText.Plain(m.Transcript)); // a recording's times would only distract the model
        if (text.Length <= budget) return CleanOutput(await chat(cfg, model, WholePrompt(m, text), ctx));
        var parts = SplitTranscript(text, budget);
        var notes = new List<string>();
        for (int i = 0; i < parts.Count; i++)
            notes.Add(CleanOutput(await chat(cfg, model, PartPrompt(m, parts[i], i + 1, parts.Count), ctx)));
        // Very long lectures: fold pairs of part-notes together until the merge fits in one call.
        while (notes.Count > 1 && notes.Sum(n => n.Length) > budget)
        {
            var folded = new List<string>();
            for (int i = 0; i < notes.Count; i += 2)
                folded.Add(i + 1 < notes.Count ? CleanOutput(await chat(cfg, model, CondensePrompt(m, notes[i..(i + 2)]), ctx)) : notes[i]);
            notes = folded;
        }
        return CleanOutput(await chat(cfg, model, MergePrompt(m, notes), ctx));
    }
}
