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
    /// <summary>A sync finished: what changed, for a notification.</summary>
    public event Action<List<string>>? Finished;

    public CanvasSync(string home, Func<string, string> classDir, Action<string>? log = null)
    {
        this.home = home;
        this.classDir = classDir;
        this.log = log ?? Console.WriteLine;
        Crawl = new Crawl(home, classDir);
    }

    public CanvasSettings Settings => CanvasSettings.Load(home);

    /// <summary>The extension asks for work: start a sync when one is due (or asked for), then give it the AI's
    /// reads first and the sync's after.</summary>
    public CanvasWork Work(bool force, string? extVersion = null)
    {
        var s = CanvasSettings.Update(home, st =>
        {
            st.ExtensionSeen = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            if (extVersion is { Length: > 0 }) st.ExtensionVersion = extVersion;
        });
        if (!Crawl.Active && !Crawl.Ready && (force || s.Due(DateTimeOffset.Now)) && s.On && s.Courses.Count > 0
            && Crawl.Start(s.Url, s.Courses, DateTime.Now))
        {
            CanvasSettings.Update(home, st =>
            {
                st.SyncNow = false;
                st.LastSync = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            });
            log($"[canvas] syncing {s.Courses.Count} class(es)");
        }
        if (Crawl.TakeSignedOut())
            CanvasSettings.Update(home, st =>
            {
                st.NeedsLogin = true;
                st.Error = "Chrome isn't signed in to Canvas. Open Canvas in Chrome and sign in; it syncs again within the hour.";
            });
        var jobs = Agents.Take();
        if (jobs.Count == 0) jobs = Crawl.Next();
        return new CanvasWork(jobs, Agents.Hot || Crawl.Active, Extension.Version());
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
        var now = DateTimeOffset.Now;
        var items = done.Assignments.SelectMany(kv => kv.Value.Where(Assignments.Published).Select(a => Assignments.From(kv.Key, a, now))).ToList();
        var before = Assignments.Load(home);
        var changes = Assignments.Diff(before, items);
        Assignments.Save(home, items);
        int files = done.Changed.Values.Sum(v => v.Count);
        CanvasSettings.Update(home, st =>
        {
            st.Error = done.Errors.Count > 0 ? $"{done.Errors.Count} thing(s) couldn't be read: {done.Errors[0]}" : "";
            // The first sync finds everything new: that isn't news.
            if (changes.Count > 0 && before.Count > 0) st.Changes = changes.Take(60).ToList();
        });
        log($"[canvas] sync done: {items.Count} assignments, {changes.Count} changes, {files} files" + (done.Errors.Count > 0 ? $", {done.Errors.Count} errors" : ""));
        if (before.Count > 0 && changes.Count > 0) Finished?.Invoke(changes);
    }

    // --- for AIs: reading Canvas through the extension ---------------------------------------------------------

    /// <summary>A full Canvas address for a path or URL an AI gave, or null when it isn't Canvas (or its file store).</summary>
    public string? CanvasUrl(string given)
    {
        string url = Py.Strip(given), b = Settings.Url.TrimEnd('/');
        if (b.Length == 0) return null;
        if (url.StartsWith('/')) url = b + url;
        return url.StartsWith(b + "/", StringComparison.Ordinal) || FileStore().IsMatch(url) ? url : null;
    }

    [GeneratedRegex("^https://[a-z0-9.-]+\\.inscloudgate\\.net/")]
    private static partial Regex FileStore();

    [GeneratedRegex("<([^>]+)>;\\s*rel=\"next\"")]
    private static partial Regex NextLink();

    /// <summary>
    /// Read Canvas for an AI: <c>json</c> (the API, as text), <c>text</c> (a web page as Markdown) or <c>bytes</c>
    /// (a file, saved into a class's folder). <paramref name="saveTo"/> is "Class name/path inside its folder".
    /// </summary>
    public async Task<JsonObject> FetchAsync(string given, string kind, string saveTo = "", CancellationToken ct = default)
    {
        if (CanvasUrl(given) is not string url) return new JsonObject { ["error"] = "Only Canvas addresses (or /api/v1/... paths) can be read." };
        string? dest = null;
        if (kind == "bytes" && (dest = SavePath(saveTo)) is null)
            return new JsonObject { ["error"] = "save_to must be \"<class>/<path in its folder>\", with a class this library has." };
        var r = await Agents.FetchAsync(url, kind == "text" ? "text" : kind, ct: ct);
        if (r.Error.Length > 0) return new JsonObject { ["error"] = r.Error };
        if (r.Status == 401) return new JsonObject { ["error"] = "Chrome isn't signed in to Canvas." };
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
                result["markdown"] = Py.Head(HtmlText.ToMarkdown(r.Text), 60_000);
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
