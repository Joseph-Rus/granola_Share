using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core.Canvas;

/// <summary>What the extension gets when it asks for work.</summary>
public sealed record CanvasWork(List<CanvasJob> Jobs, bool Hot, string Ext);

/// <summary>
/// Canvas in the library: hands the extension its work (a sync every hour or so, and any reads an AI asked for),
/// files the answers, and when a sync finishes, updates the assignments list and says what changed.
/// </summary>
public sealed partial class CanvasSync
{
    readonly string home;
    readonly Func<string, string> classDir;
    readonly Action<string> log;

    public Crawl Crawl { get; }
    public AgentQueue Agents { get; } = new();
    /// <summary>A sync finished and something changed on Canvas: what, for a notification.</summary>
    public event Action<List<CanvasChange>>? Finished;
    /// <summary>A sync finished: the classes it read (the course scout explores new ones).</summary>
    public event Action<List<string>>? Synced;

    public CanvasSync(string home, Func<string, string> classDir, Action<string>? log = null)
    {
        this.home = home;
        this.classDir = classDir;
        this.log = log ?? Console.WriteLine;
        Crawl = new Crawl(home, classDir, () => Clock(), () => Zone);
    }

    /// <summary>What time it is: when a sync is due, how long Canvas's pause lasts, what's past due (tests set it).</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>The time zone due dates and the Markdown's times are in (tests set it).</summary>
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    public CanvasSettings Settings => CanvasSettings.Load(home);

    /// <summary>The extension asks for work: start a sync when one is due (or asked for), then give it the AI's
    /// reads first and the sync's after. While Canvas has asked the sync to slow down, it isn't told to hurry back.
    /// <paramref name="extVersion"/> and <paramref name="protocol"/> are the extension's own (an extension from
    /// before protocol 2 sends none); every extension so far can do every job, whatever its version.</summary>
    public CanvasWork Work(bool force, string? extVersion = null, int protocol = 1)
    {
        var now = Clock();
        string at = now.ToString("o", CultureInfo.InvariantCulture);
        var s = CanvasSettings.Update(home, st =>
        {
            st.ExtensionSeen = at;
            if (extVersion is not { Length: > 0 } || extVersion == st.ExtensionVersion) return;
            // Chrome reloaded a newer copy from the folder Study Stash keeps up to date: worth a word, once.
            if (Extension.IsOlder(st.ExtensionVersion, extVersion)) st.ExtensionUpdate = new ExtensionUpdate(st.ExtensionVersion, extVersion, at, false);
            st.ExtensionVersion = extVersion;
        });
        if (protocol < 1) return new CanvasWork([], false, Extension.Version()); // nothing this library knows how to hand it
        // A sync that finished just before the library stopped is filed now, before the next one can start.
        if (Crawl.Ready) Finish();
        if (!Crawl.Active && !Crawl.Ready && (force || s.Due(now)) && s.On && s.Courses.Count > 0
            && Crawl.Start(s.Url, s.Courses))
        {
            CanvasSettings.Update(home, st =>
            {
                st.SyncNow = false;
                st.LastSync = at;
            });
            log($"[canvas] syncing {s.Courses.Count} class(es)");
        }
        if (Crawl.TakeSignedOut())
            CanvasSettings.Update(home, st =>
            {
                st.NeedsLogin = true;
                st.Error = "Chrome isn't signed in to Canvas. Open Canvas in Chrome and sign in; it syncs again within the hour.";
            });
        // A fresh extension (installed, reloaded into a new version, or asked to sync) lost whatever its old copy had
        // taken: that goes out again now instead of in ten minutes.
        if (force) Crawl.Requeue();
        var jobs = Agents.Take();
        if (jobs.Count == 0) jobs = Crawl.Next();
        return new CanvasWork(jobs, Agents.Hot || Crawl.Active && Crawl.PausedUntil is null, Extension.Version());
    }

    /// <summary>The extension's answers. When the sync has everything, it's finished here.</summary>
    public void Results(IEnumerable<CanvasResult> results)
    {
        bool any = false;
        foreach (var r in results)
        {
            if (Agents.Answer(r)) continue;
            any |= Crawl.Handle(r);
        }
        if (any && CanvasSettings.Load(home).NeedsLogin && !Crawl.TakeSignedOut())
            CanvasSettings.Update(home, st => { st.NeedsLogin = false; st.Error = ""; });
        if (Crawl.Ready) Finish();
    }

    void Finish()
    {
        if (Crawl.TakeFinished() is not { } done) return;
        var now = Clock();
        string at = now.ToString("o", CultureInfo.InvariantCulture);
        var wall = TimeZoneInfo.ConvertTime(now, Zone).DateTime;
        var before = Assignments.Load(home);
        var order = CanvasSettings.Load(home).Courses.Keys.Concat(done.Sections.Keys).Distinct().ToList();
        var items = new List<Assignment>();
        var changes = new List<CanvasChange>();
        foreach (string cls in order)
        {
            var had = before.Where(a => a.ClassName == cls).ToList();
            // The class's index has its assignments once they've been read completely, in this sync or an earlier one;
            // until then (its listing failed), what was known is kept: missing rows aren't "removed".
            var rows = done.Indexes.GetValueOrDefault(cls) is { } index && index.ReadAt.ContainsKey("assignments")
                ? index.Assignments.Select(a => Assignments.From(cls, a, now, Zone)).ToList()
                : had;
            items.AddRange(rows);
            // A class's first sync finds everything new: that isn't news.
            if (had.Count > 0 || done.Before.GetValueOrDefault(cls)?.ReadAt.ContainsKey("assignments") == true)
                changes.AddRange(Assignments.Diff(had, rows, wall));
        }
        // A class no longer linked to Canvas isn't news either: its rows just go.
        Assignments.Save(home, items);
        int files = done.Changed.Values.Sum(v => v.Count);
        var failed = done.Sections.SelectMany(c => c.Value.Where(l => l.Value == "failed").Select(l => $"{c.Key} {l.Key}")).ToList();
        CanvasSettings.Update(home, st =>
        {
            st.LastDone = at;
            st.Error = failed.Count > 0 ? $"Couldn't read {string.Join(", ", failed)} from Canvas ({Reason(done.Errors, failed[0])}), so what you had is kept."
                : done.Errors.Count > 0 ? $"{done.Errors.Count} thing(s) couldn't be read: {done.Errors[0]}" : "";
            st.ErrorAt = st.Error.Length > 0 ? at : "";
            if (changes.Count == 0) return;
            st.Changes = changes.Take(60).Select(c => c.Text).ToList();
            st.LastChanges = changes.Take(60).ToList();
        });
        CanvasNotifications.AppendChanges(home, changes, at);
        log($"[canvas] sync done: {items.Count} assignments, {changes.Count} changes, {files} files" + (done.Errors.Count > 0 ? $", {done.Errors.Count} errors" : ""));
        if (changes.Count > 0) Finished?.Invoke(changes);
        Synced?.Invoke(done.Sections.Where(c => c.Value.ContainsValue("ok")).Select(c => c.Key).Concat(done.Changed.Keys).Distinct().ToList());
    }

    /// <summary>Why a section failed ("Canvas answered 503"), from the crawl's errors ("CS 101 assignments: …").</summary>
    static string Reason(List<string> errors, string section) =>
        errors.FirstOrDefault(e => e.StartsWith(section + ": ", StringComparison.Ordinal))?[(section.Length + 2)..] ?? "no answer";

    // --- for AIs: reading Canvas through the extension ---------------------------------------------------------

    /// <summary>A full Canvas address for a path or URL an AI gave, or null when it isn't Canvas (or its file store).</summary>
    public string? CanvasUrl(string given)
    {
        string url = Py.Strip(given), b = Settings.Url.TrimEnd('/');
        if (b.Length == 0) return null;
        if (url.StartsWith('/')) url = b + url;
        return url.StartsWith(b + "/", StringComparison.Ordinal) || Extension.OnFileHost(url) ? url : null;
    }

    [GeneratedRegex("<([^>]+)>;\\s*rel=\"next\"")]
    private static partial Regex NextLink();

    /// <summary>Refuses another student's data by path, ignoring the query string (like everything else here): a
    /// roster (<c>/users</c> except <c>/users/self</c>), <c>/enrollments</c>, <c>/peer_reviews</c>,
    /// <c>/search/recipients</c>, <c>/conversations</c>, a discussion's <c>entries</c>/<c>view</c>/<c>entry_list</c>,
    /// or a course's <c>/students</c> (its own <c>/students/submissions</c> — the student's own grades — is fine).</summary>
    public static bool DeniesOtherPeople(string urlOrPath)
    {
        string path = Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri) ? uri.AbsolutePath : urlOrPath;
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length; i++)
        {
            string s = segs[i];
            bool Next(string word) => i + 1 < segs.Length && segs[i + 1].Equals(word, StringComparison.OrdinalIgnoreCase);
            if (s.Equals("enrollments", StringComparison.OrdinalIgnoreCase) || s.Equals("peer_reviews", StringComparison.OrdinalIgnoreCase)
                || s.Equals("conversations", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.Equals("users", StringComparison.OrdinalIgnoreCase) && !Next("self")) return true;
            if (s.Equals("search", StringComparison.OrdinalIgnoreCase) && Next("recipients")) return true;
            if (s.Equals("students", StringComparison.OrdinalIgnoreCase) && !Next("submissions")) return true;
            if (s.Equals("discussion_topics", StringComparison.OrdinalIgnoreCase) && i + 2 < segs.Length
                && segs[i + 2] is "entries" or "view" or "entry_list") return true;
        }
        return false;
    }

    /// <summary>
    /// Read Canvas for an AI: <c>json</c> (the API, as text), <c>text</c> (a web page as Markdown) or <c>bytes</c>
    /// (a file, saved into a class's folder). <paramref name="saveTo"/> is "Class name/path inside its folder".
    /// </summary>
    public async Task<JsonObject> FetchAsync(string given, string kind, string saveTo = "", CancellationToken ct = default)
    {
        if (CanvasUrl(given) is not string url) return new JsonObject { ["error"] = "Only Canvas addresses (or /api/v1/... paths) can be read." };
        if (DeniesOtherPeople(url)) return new JsonObject { ["error"] = "Study Stash doesn't read other people's Canvas data." };
        string? dest = null;
        if (kind == "bytes" && (dest = SavePath(saveTo)) is null)
            return new JsonObject { ["error"] = "save_to must be \"<class>/<path in its folder>\", with a class this library has." };
        var r = await Agents.FetchAsync(url, kind == "text" ? "text" : kind, ct: ct);
        if (r.Error.Length > 0) return new JsonObject { ["error"] = r.Error };
        // A 401 for a tab this student can't see comes back as Canvas said it; only a real sign-out is an error.
        if (Crawl.Classify(r) == CanvasAnswer.SignedOut) return new JsonObject { ["error"] = "Chrome isn't signed in to Canvas." };
        if (kind == "bytes" && r.Status >= 400) return new JsonObject { ["error"] = $"Canvas answered {r.Status}, so nothing was saved.", ["status"] = r.Status };
        var result = new JsonObject { ["status"] = r.Status, ["url"] = r.Final.Length > 0 ? r.Final : url };
        if (NextLink().Match(r.Link) is { Success: true } m) result["next_page"] = m.Groups[1].Value;
        switch (kind)
        {
            case "bytes":
                byte[] body = Convert.FromBase64String(r.B64);
                Directory.CreateDirectory(Py.Parent(dest!));
                File.WriteAllBytes(dest!, body);
                result["saved"] = saveTo;
                result["bytes"] = body.Length;
                break;
            case "text":
                // A web page reads as Markdown; anything else (plain text, CSV, JSON) as Canvas sent it.
                result["markdown"] = Py.Head(r.Type.Contains("html", StringComparison.OrdinalIgnoreCase) ? HtmlText.ToMarkdown(r.Text) : r.Text, 60_000);
                break;
            default:
                result["json"] = Py.Head(r.Text, 200_000);
                break;
        }
        return result;
    }

    /// <summary>"CS 101/Canvas/files/slides.pdf" → the file in that class's folder, or null when the class isn't one
    /// of this library's or the path leaves its folder.</summary>
    public string? SavePath(string saveTo)
    {
        string[] parts = saveTo.Replace('\\', '/').Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[1].Length == 0 || !Settings.Courses.ContainsKey(parts[0]) && !KnownClass(parts[0])) return null;
        string root = Path.GetFullPath(classDir(parts[0])), full = Path.GetFullPath(Path.Combine(root, parts[1]));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>Set by the library: whether a class of that name exists.</summary>
    public Func<string, bool> KnownClass { get; set; } = _ => false;

    /// <summary>For AIs: each linked class, its Canvas id, and whether a course recipe exists.</summary>
    public JsonObject Courses()
    {
        var s = Settings;
        var list = new JsonArray();
        foreach (var (cls, id) in s.Courses)
            list.Add(new JsonObject
            {
                ["class"] = cls, ["canvas_id"] = id, ["folder"] = cls + "/Canvas",
                ["has_recipe"] = File.Exists(Path.Combine(classDir(cls), "Canvas", "canvas-recipe.md")),
            });
        return new JsonObject { ["canvas"] = s.Url, ["courses"] = list };
    }
}
