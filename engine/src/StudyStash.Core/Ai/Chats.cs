using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace StudyStash.Core.Ai;

/// <summary>One message in a chat: what was said, which tools the AI used, and what it changed (with the change's id
/// for Undo).</summary>
public sealed record ChatMessage(string Role, string Text, string When)
{
    public List<string> Tools { get; init; } = [];
    public List<string> Changed { get; init; } = [];
    public string Change { get; init; } = "";
    public bool Failed { get; init; }
}

/// <summary>A conversation with the library's AI: about everything, one class, or one lecture.</summary>
public sealed class Chat
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Lecture { get; set; } = "";
    /// <summary>The AI that holds the conversation, and its own id for it (a follow-up continues it).</summary>
    public string Provider { get; set; } = "";
    public string Session { get; set; } = "";
    public string Updated { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = [];
}

/// <summary>
/// Chatting with the library: the AI picked for agent work reads the whole library (lectures, Canvas, the files you
/// let it read) with the library's tools, answers with its sources, and remembers the conversation for follow-ups.
/// With edits allowed it may also write files in the library (a study guide, a cheat sheet), each turn kept as one
/// change in <see cref="History"/> so it can be undone. Chats are kept in chats/ beside config.toml.
/// </summary>
public sealed class Chats(string home, string library, AiJobs ai, History history, Func<IReadOnlyList<string>>? readDirs = null)
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    readonly Lock gate = new();

    string Dir => Path.Combine(home, "chats");
    string PathOf(string id) => Path.Combine(Dir, id + ".json");

    public static bool GoodId(string id) => id.Length is > 0 and <= 40 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public Chat? Get(string id)
    {
        if (!GoodId(id) || !File.Exists(PathOf(id))) return null;
        try
        {
            return JsonSerializer.Deserialize<Chat>(File.ReadAllText(PathOf(id)), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(Chat c)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathOf(c.Id), JsonSerializer.Serialize(c, Options));
        }
    }

    public bool Delete(string id)
    {
        if (!GoodId(id) || !File.Exists(PathOf(id))) return false;
        File.Delete(PathOf(id));
        return true;
    }

    /// <summary>The chats, most recent first.</summary>
    public List<Chat> List(int n = 50) =>
        Directory.Exists(Dir)
            ? Directory.EnumerateFiles(Dir, "*.json").Select(f => Get(Path.GetFileNameWithoutExtension(f))).OfType<Chat>()
                .OrderByDescending(c => c.Updated, StringComparer.Ordinal).Take(n).ToList()
            : [];

    public Chat New(string className = "", string lecture = "") => new()
    {
        Id = DateTime.Now.ToString("yyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3)),
        ClassName = className, Lecture = lecture,
    };

    /// <summary>What the AI is told about the library, and how to behave.</summary>
    public string SystemPrompt(Chat c, bool edit)
    {
        var dirs = readDirs?.Invoke() ?? [];
        string focus = c.Lecture.Length > 0 ? $"The user is looking at one lecture (id {c.Lecture}); start there (get_lecture)."
            : c.ClassName.Length > 0 ? $"The user is asking about the class \"{c.ClassName}\"; start with its folder and lectures."
            : "The user may ask about any class.";
        return $"""
            You are the study companion inside Study Stash, the user's own library of lecture notes. Your working folder is that library:
            one folder per class, each with its lectures as Markdown files ("YYYY-MM-DD Title.md": notes written from the recording, then
            its transcript), and, for classes linked to Canvas, a Canvas/ folder (assignments/*/spec.md with instructions and rubric,
            feedback.md with the user's score and the grader's comments, modules/ with course files and pages, announcements.md, and
            canvas-recipe.md, a guide to where that instructor puts things).
            Use the study-stash tools too: search_notes (full-text search of every lecture and transcript), get_lecture, get_transcript,
            due_assignments, and the canvas_* tools to read Canvas itself through the user's browser when the library doesn't have something.
            {(dirs.Count > 0 ? "You may also read these folders of the user's: " + string.Join(", ", dirs) + "." : "")}
            {focus}
            Answer like a classmate who took great notes: clear and direct, in plain prose, with short lists only where they help. Explain
            the idea, don't just quote. Say where an answer comes from (the lecture's title and date, or the file), and say plainly when
            the library doesn't cover something. When quizzing or explaining, check understanding one step at a time.
            {(edit
                ? "You may create and change files in this folder when the user asks (a study guide, a cheat sheet, practice questions): put them in the class's folder with a clear name. Never change the lecture files or anything under Canvas/ except canvas-recipe.md; those are rewritten by Study Stash. Don't run git. Say which files you wrote."
                : "This conversation is read-only: don't create or change files. If the user asks for a file, say they can turn on edits.")}
            """;
    }

    /// <summary>
    /// One turn: the user's message, the AI's answer as it comes (text and tool events), then a <c>changes</c> event
    /// (Text: the change's id; Path: the files, one per line) when it wrote anything. The chat is saved as it ends.
    /// </summary>
    public async IAsyncEnumerable<AiEvent> TurnAsync(Chat c, string message, bool edit, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string provider = AiSettings.Load(home).For("agent").Provider;
        if (c.Provider != provider) c.Session = ""; // another AI can't continue this one's conversation: start its own
        c.Provider = provider;
        if (c.Title.Length == 0) c.Title = Py.Head(message.ReplaceLineEndings(" "), 80);
        c.Messages.Add(new ChatMessage("user", message, Now()));
        c.Updated = Now();
        Save(c);
        // A follow-up on a new AI session: give it the conversation so far.
        string prompt = c.Session.Length == 0 && c.Messages.Count > 1
            ? "The conversation so far:\n\n" + string.Join("\n\n", c.Messages.SkipLast(1).TakeLast(12).Select(m => (m.Role == "user" ? "User: " : "You: ") + Py.Head(m.Text, 4000)))
              + "\n\nThe user now says:\n" + message
            : message;
        Dictionary<string, (long, DateTime)>? before = null;
        if (edit)
        {
            history.Baseline();
            before = history.Snapshot();
        }
        string text = "", final = "", error = "";
        var tools = new List<string>();
        await foreach (var e in ai.AgentAsync(prompt, library, edit, SystemPrompt(c, edit), c.Session, readDirs?.Invoke(), ct: ct))
        {
            switch (e.Kind)
            {
                case "session" when e.Text.Length > 0: c.Session = e.Text; break;
                case "text": text += e.Text; break;
                case "final": final = e.Text; break;
                case "tool": tools.Add(e.Name + (e.Path.Length > 0 ? " " + e.Path : "")); break;
                case "error": error = e.Text; break;
            }
            yield return e;
        }
        var changed = before is null ? [] : history.ChangedSince(before).Where(f => f != ".gitignore").ToList();
        string sha = changed.Count > 0 ? history.Commit(changed, message, AiProviders.Get(provider).Name) : "";
        if (changed.Count > 0) yield return new AiEvent("changes", sha, Path: string.Join("\n", changed));
        string answer = Py.Strip(final.Length > 0 ? final : text);
        c.Messages.Add(new ChatMessage("assistant", error.Length > 0 && answer.Length == 0 ? error : answer, Now())
        {
            Tools = tools.Take(40).ToList(), Changed = changed, Change = sha, Failed = error.Length > 0 && answer.Length == 0,
        });
        c.Updated = Now();
        Save(c);
    }

    static string Now() => DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
}
