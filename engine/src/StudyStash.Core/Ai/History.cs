using System.Globalization;

namespace StudyStash.Core.Ai;

/// <summary>One change an AI made to the library: when, what it was asked, and the files it touched.</summary>
public sealed record Change(string Sha, string When, string Title, string By, List<string> Files);

/// <summary>
/// Undo for the AI's changes. The library's folder becomes a git repository (just for this; nothing is pushed
/// anywhere). Each chat turn that changes files is one commit of exactly those files, so History can list what
/// the AI changed and Undo puts each one back. Without git on the computer, changes still happen but can't be undone.
/// </summary>
public sealed class History(string root, Runner? run = null)
{
    readonly Runner run = run ?? Machine.Run;
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    public const string Trailer = "Study-Stash-By";

    string? Git(params string[] args) =>
        run(AiProvider.Which("git") ?? "git", ["-C", root, .. args], Wait) is { ExitCode: 0 } r ? r.Stdout : null;

    public bool Available => AiProvider.Which("git") is not null;

    /// <summary>Make the folder a repository, once. The database and anything big stay out of it.</summary>
    public bool Ensure()
    {
        if (!Available) return false;
        if (Directory.Exists(Path.Combine(root, ".git"))) return true;
        Directory.CreateDirectory(root);
        if (Git("init", "-q") is null) return false;
        File.WriteAllText(Path.Combine(root, ".gitignore"), ".DS_Store\n*.zip\n*.mp4\n*.mov\n*.wav\n*.m4a\n");
        Git("config", "user.name", "Study Stash");
        Git("config", "user.email", "study-stash@localhost");
        return Git("add", ".gitignore") is not null && Git("commit", "-q", "-m", "Start keeping the library's history") is not null;
    }

    /// <summary>
    /// Keep the library's text as it is now, before an AI may change it, so undoing an edit to a file that was never in
    /// the history restores it instead of deleting it. Not listed in History (no trailer).
    /// </summary>
    public void Baseline()
    {
        if (!Ensure()) return;
        // Text only: course PDFs and slides are big, and the AI doesn't edit them.
        var text = Snapshot().Keys.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).ToList();
        for (int i = 0; i < text.Count; i += 200) Git(["add", "--", .. text.Skip(i).Take(200)]);
        if (Git("diff", "--cached", "--quiet") is null) Git("commit", "-q", "-m", "The library before a change");
    }

    /// <summary>Every file and when it last changed, to see afterwards what a turn touched.</summary>
    public Dictionary<string, (long Size, DateTime Time)> Snapshot() =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .ToDictionary(f => Path.GetRelativePath(root, f), f => (new FileInfo(f).Length, File.GetLastWriteTimeUtc(f)))
            : [];

    /// <summary>What's new, changed or gone since a snapshot.</summary>
    public List<string> ChangedSince(Dictionary<string, (long, DateTime)> before)
    {
        var now = Snapshot();
        var changed = now.Where(kv => !before.TryGetValue(kv.Key, out var b) || b != kv.Value).Select(kv => kv.Key).ToList();
        changed.AddRange(before.Keys.Where(k => !now.ContainsKey(k)));
        return changed.Select(p => p.Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Commit exactly these files as one change. Its id, or "" when nothing could be kept.</summary>
    public string Commit(IReadOnlyList<string> files, string title, string by)
    {
        if (files.Count == 0 || !Ensure()) return "";
        if (Git(["add", "-A", "--", .. files]) is null) return "";
        string subject = Py.Head(title.ReplaceLineEndings(" "), 72);
        if (Git(["commit", "-q", "-m", subject, "-m", $"{Trailer}: {by}", "--", .. files]) is null) return "";
        return Py.Strip(Git("rev-parse", "--short", "HEAD") ?? "");
    }

    /// <summary>The AI's changes, newest first.</summary>
    public List<Change> Log(int n = 50)
    {
        if (!Directory.Exists(Path.Combine(root, ".git"))) return [];
        string? out_ = Git("log", $"-{n}", $"--grep={Trailer}:", "--name-only", "--format=\u001e%h\u001f%aI\u001f%s\u001f%(trailers:key=" + Trailer + ",valueonly)");
        var list = new List<Change>();
        foreach (string rec in (out_ ?? "").Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = rec.Split('\n');
            var f = lines[0].Split('\u001f');
            if (f.Length < 4) continue;
            var files = lines.Skip(1).Select(Py.Strip).Where(l => l.Length > 0 && !l.Contains('\u001f')).ToList();
            list.Add(new Change(f[0], f[1], f[2], Py.Strip(f[3]), files));
        }
        return list;
    }

    /// <summary>Put a change back. False (with why) when a later change touched the same lines.</summary>
    public (bool Ok, string Why) Undo(string sha)
    {
        if (!Directory.Exists(Path.Combine(root, ".git"))) return (false, "There's no history to undo.");
        if (Git("revert", "--no-edit", sha) is not null)
        {
            Git("commit", "--amend", "-q", "-m", $"Undid: {Py.Strip(Git("log", "-1", "--format=%s", sha + "") ?? sha)}", "-m", $"{Trailer}: undo");
            return (true, "");
        }
        Git("revert", "--abort");
        return (false, "A later change touched the same files. Undo that one first.");
    }

    public static string Ago(string iso, DateTime now) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? (now - t.LocalDateTime) switch
            {
                { TotalMinutes: < 1 } => "just now",
                { TotalMinutes: < 60 } d => $"{(int)d.TotalMinutes} min ago",
                { TotalHours: < 24 } d => $"{(int)d.TotalHours} h ago",
                _ => t.LocalDateTime.ToString("d MMM", CultureInfo.InvariantCulture),
            }
            : "";
}
