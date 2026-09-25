using System.Globalization;
using System.Text.RegularExpressions;

namespace StudyStash.Core.Ai;

/// <summary>
/// Capture: a thought, a link, a reading note, jotted down from anywhere, lands in the library's Inbox/ folder. When
/// the AI sorts each one into the class it's about (its Notes/ folder), adding to a note of the same name when there
/// is one, as one undoable change. Things that fit no class stay in the Inbox.
/// </summary>
public sealed partial class Inbox(string library, Func<IReadOnlyList<string>> classes, Func<string, string> classDir, AiJobs ai, History history,
    Action<string>? log = null)
{
    readonly Action<string> log = log ?? Console.WriteLine;
    readonly SemaphoreSlim filing = new(1, 1);
    CancellationTokenSource? soon;

    public string Dir => Path.Combine(library, "Inbox");

    /// <summary>What's waiting to be filed, oldest first.</summary>
    public List<string> Waiting() =>
        Directory.Exists(Dir) ? Directory.EnumerateFiles(Dir, "*.md").Order(StringComparer.Ordinal).ToList() : [];

    [GeneratedRegex(@"[^\p{L}\p{N} ]+")]
    private static partial Regex NotWords();

    /// <summary>Keep what was typed. With a class, it goes straight to that class's Notes/; otherwise to the Inbox,
    /// for the AI to file. The file it was written to.</summary>
    public string Capture(string text, string? className = null, DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        string words = string.Join(" ", NotWords().Replace(Py.Strip(text).Split('\n')[0], " ").Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(7));
        string name = $"{t:yyyy-MM-dd HHmm} {(words.Length > 0 ? words : "Note")}";
        string dir = className is { Length: > 0 } c && classes().Contains(c) ? Path.Combine(classDir(c), "Notes") : Dir;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Canvas.Crawl.SafeName(name) + ".md");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, Canvas.Crawl.SafeName(name) + $" {i}.md");
        File.WriteAllText(path, $"_Captured {t.ToString("ddd d MMM yyyy, h:mm tt", CultureInfo.InvariantCulture)}._\n\n{Py.Strip(text)}\n");
        if (dir == Dir) FileSoon();
        return path;
    }

    /// <summary>File the Inbox in a little while, so a few captures in a row are filed together.</summary>
    public void FileSoon(TimeSpan? after = null)
    {
        soon?.Cancel();
        var mine = soon = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(after ?? TimeSpan.FromSeconds(45), mine.Token);
                await FileAsync();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    static readonly System.Text.Json.Nodes.JsonObject PlanSchema = System.Text.Json.Nodes.JsonNode.Parse("""
        {"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{
          "n":{"type":"integer"},"class":{"type":"string"},"title":{"type":"string"}},"required":["n","class","title"]}}},"required":["items"]}
        """)!.AsObject();

    public string Prompt(IReadOnlyList<(string Name, string Text)> waiting) => $"""
        Sort things a student jotted down into their classes. The classes: {string.Join("; ", classes().Select(c => $"\"{c}\""))}.
        For each numbered item, give the class it's about (exactly as written above, or "" if it fits none) and a short, clear title
        for it as a note (a few words, no date).

        {string.Join("\n\n", waiting.Select((w, i) => $"[{i + 1}] {Py.Head(w.Text, 1500)}"))}
        """;

    /// <summary>
    /// File what's waiting (one run at a time): the AI says which class each item is about and names it; each goes to
    /// that class's Notes/, added to a note of the same name when there is one. One undoable change. What happened.
    /// </summary>
    public async Task<string> FileAsync()
    {
        if (!await filing.WaitAsync(0)) return "Already filing.";
        try
        {
            var waiting = Waiting();
            if (waiting.Count == 0) return "Nothing to file.";
            var items = waiting.Select(p => (Name: p, Text: File.ReadAllText(p))).ToList();
            System.Text.Json.Nodes.JsonObject? plan;
            try
            {
                plan = System.Text.Json.Nodes.JsonNode.Parse(await ai.PlanAsync("sort", Prompt(items), PlanSchema)) as System.Text.Json.Nodes.JsonObject;
            }
            catch (Exception e) when (e is InvalidOperationException or InvalidDataException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
            {
                log($"[inbox] couldn't sort: {e.Message}");
                return $"The AI couldn't sort them: {e.Message}";
            }
            history.Baseline();
            var before = history.Snapshot();
            var said = new List<string>();
            var names = classes();
            foreach (var p in (plan?["items"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>())
            {
                int n = p["n"] is System.Text.Json.Nodes.JsonValue nv && nv.TryGetValue(out int k) ? k - 1 : -1;
                string cls = p["class"]?.GetValue<string>() ?? "", title = Canvas.Crawl.SafeName(p["title"]?.GetValue<string>() ?? "", 80);
                if (n < 0 || n >= items.Count || !names.Contains(cls) || !File.Exists(items[n].Name)) continue;
                string dir = Path.Combine(classDir(cls), "Notes"), dest = Path.Combine(dir, title + ".md");
                Directory.CreateDirectory(dir);
                string body = items[n].Text.Trim();
                if (File.Exists(dest)) File.AppendAllText(dest, "\n\n" + body + "\n");
                else File.WriteAllText(dest, $"# {title}\n\n{body}\n");
                File.Delete(items[n].Name);
                said.Add($"{title} → {cls}");
            }
            var changed = history.ChangedSince(before).Where(f => f != ".gitignore").ToList();
            string sha = history.Commit(changed, $"Filed {said.Count} thing{(said.Count == 1 ? "" : "s")} from the Inbox", ai.AgentName);
            int left = Waiting().Count;
            log($"[inbox] filed {said.Count}, {left} left ({sha})");
            return said.Count == 0 ? "None of them fit a class, so they're still here."
                : string.Join("; ", said) + (left > 0 ? $". {left} didn't fit a class and stayed here." : ".");
        }
        finally
        {
            filing.Release();
        }
    }
}
