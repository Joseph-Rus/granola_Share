using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core.Canvas;

/// <summary>A Canvas address for the extension to read, as JSON (the API) or as a file's bytes.</summary>
public sealed record CanvasJob(string Id, string Url, string Kind);

/// <summary>What a finished sync did: the files it changed (class → paths inside the class's folder), what it couldn't
/// read, how each class's listings went, and each class's index before the sync and after it.</summary>
public sealed record CrawlFinished(Dictionary<string, List<string>> Changed, List<string> Errors,
    Dictionary<string, Dictionary<string, string>> Sections, Dictionary<string, CourseIndex?> Before, Dictionary<string, CourseIndex> Indexes);

/// <summary>What the extension read: the HTTP status, Canvas's paging header, and the text or bytes (base64). Status
/// 401 with no text, or <see cref="SignedOut"/>, means Chrome isn't signed in to Canvas; 401 with Canvas's
/// "unauthorized" JSON only means the student can't see that part of the course.</summary>
public sealed record CanvasResult(string Id, int Status, string Link, string Text, string B64, string Error, string Final)
{
    /// <summary>Canvas sent the extension to its sign-in page (newer extensions say so; older ones answer 401).</summary>
    public bool SignedOut { get; init; }
    /// <summary>Canvas's X-Rate-Limit-Remaining: how much of its allowance is left. Null when it didn't say.</summary>
    public double? Rate { get; init; }
    /// <summary>Seconds Canvas asked to wait (Retry-After), when it asked.</summary>
    public double? RetryAfter { get; init; }
    /// <summary>The answer's content type.</summary>
    public string Type { get; init; } = "";

    public static CanvasResult From(JsonObject o)
    {
        static string S(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.String ? j.GetValue<string>() : "";
        // Header values arrive as text from the extension ("699.8"); numbers are fine too.
        static double? N(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.Number ? j.GetValue<double>()
            : double.TryParse(S(v), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
        int status = o["status"] is JsonValue sv && sv.GetValueKind() == JsonValueKind.Number ? (int)sv.GetValue<double>() : 0;
        return new CanvasResult(o["id"] is JsonValue iv && iv.GetValueKind() == JsonValueKind.Number ? iv.ToString() : S(o["id"]),
            status, S(o["link"]), S(o["text"]), S(o["b64"]), S(o["error"]), S(o["final"]))
        {
            SignedOut = o["signed_out"] is JsonValue so && so.GetValueKind() == JsonValueKind.True,
            Rate = N(o["rate"]), RetryAfter = N(o["retry_after"]), Type = S(o["type"]),
        };
    }
}

/// <summary>What one of Canvas's answers means for the sync.</summary>
public enum CanvasAnswer
{
    /// <summary>The data: file it.</summary>
    Ok,
    /// <summary>Chrome isn't signed in to Canvas: the sync stops until it is.</summary>
    SignedOut,
    /// <summary>The student can't see this (a hidden tab, a locked page, something removed): skipped quietly.</summary>
    Hidden,
    /// <summary>Canvas asked to slow down: the sync pauses and asks again.</summary>
    RateLimited,
    /// <summary>Canvas or the network hiccuped: asked again, a few times.</summary>
    Transient,
    /// <summary>It didn't work and won't by asking again.</summary>
    Failed,
}

/// <summary>
/// Mirrors Canvas into each class's folder, one fetch at a time, through the Chrome extension. The library hands the
/// extension jobs; the extension fetches them with the person's own Canvas session and hands the answers back;
/// <see cref="Handle"/> files them and queues what follows (next pages, file bodies). In each class's folder:
/// <code>
/// Canvas/assignments/&lt;name&gt;/spec.md      instructions, due date, points, rubric
/// Canvas/assignments/&lt;name&gt;/feedback.md  your submission: status, score, rubric marks, comments
/// Canvas/assignments/&lt;name&gt;/submission/  the files you turned in
/// Canvas/modules/&lt;NN Module&gt;/             module files, and pages as Markdown
/// Canvas/modules.md                      the module outline, linked to the copies
/// Canvas/announcements.md                announcements, newest first
/// </code>
/// It keeps its queue in crawl.json, so a restart carries on where it was. Each class's listings (assignments,
/// submissions, modules, announcements) are sections: read completely (<c>ok</c>), not shown to the student
/// (<c>hidden</c>) or <c>failed</c>, so a sync that couldn't read something never passes that off as "gone".
/// </summary>
public sealed partial class Crawl
{
    public const long MaxBytes = 40L * 1024 * 1024;
    const double InflightSeconds = 600;
    /// <summary>Asked this many times, a server error or a dropped connection counts as failed.</summary>
    public const int MaxTries = 3;
    /// <summary>The first pause when Canvas rate-limits, doubling each time it happens again, up to <see cref="MaxBackoff"/>.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(10);

    /// <summary>The listings each class is read through; each is a section of the sync.</summary>
    public static readonly IReadOnlyList<string> Listings = ["assignments", "submissions", "modules", "announcements"];
    // Listings whose pages only make sense together (an outline, a newest-first list): filed once the last page is in.
    static readonly HashSet<string> Whole = ["modules", "announcements"];

    readonly string home;
    readonly Func<string, string> classDir;
    readonly Func<DateTimeOffset> clock;
    readonly Func<TimeZoneInfo> zone;
    readonly Lock gate = new();
    readonly JsonObject data;
    // Each class's index as this sync has read it so far (home/canvas/<class>.sync.json), and the ones to save.
    readonly Dictionary<string, CourseIndex> staging = [];
    readonly HashSet<string> dirty = [];

    /// <param name="classDir">A class's folder in the library (created if missing).</param>
    public Crawl(string home, Func<string, string> classDir) : this(home, classDir, () => DateTimeOffset.Now)
    {
    }

    /// <param name="clock">What time it is (tests set it).</param>
    public Crawl(string home, Func<string, string> classDir, Func<DateTimeOffset> clock) : this(home, classDir, clock, () => TimeZoneInfo.Local)
    {
    }

    /// <param name="zone">The time zone the Markdown gives times in (tests set it).</param>
    public Crawl(string home, Func<string, string> classDir, Func<DateTimeOffset> clock, Func<TimeZoneInfo> zone)
    {
        this.home = home;
        this.classDir = classDir;
        this.clock = clock;
        this.zone = zone;
        data = Read() ?? [];
        data.Remove("assignments"); // a crawl.json from before the index kept every assignment's JSON here
        if (data["jobs"] is not JsonArray) data["jobs"] = new JsonArray();
        foreach (string key in new[] { "inflight", "changed", "manifest", "sections", "pages" })
            if (data[key] is not JsonObject) data[key] = new JsonObject();
        if (data["errors"] is not JsonArray) data["errors"] = new JsonArray();
    }

    string StatePath => Path.Combine(home, "crawl.json");

    JsonObject? Read()
    {
        try
        {
            return File.Exists(StatePath) ? JsonNode.Parse(File.ReadAllText(StatePath)) as JsonObject : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    void Save()
    {
        string tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, data.ToJsonString());
        File.Move(tmp, StatePath, overwrite: true);
    }

    JsonArray Jobs => (JsonArray)data["jobs"]!;
    JsonObject Inflight => (JsonObject)data["inflight"]!;
    JsonObject Manifest => (JsonObject)data["manifest"]!;
    JsonObject Changed => (JsonObject)data["changed"]!;
    JsonArray Errors => (JsonArray)data["errors"]!;
    JsonObject SectionStates => (JsonObject)data["sections"]!;
    JsonObject PagesSoFar => (JsonObject)data["pages"]!;

    static bool Flag(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.True;
    static string S(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.String ? j.GetValue<string>() : "";
    // Numbers parsed from JSON and numbers set here (ints) read the same way.
    static double? D(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.Number
        ? double.Parse(j.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>Now, in seconds since 1970 (how crawl.json keeps times).</summary>
    double Seconds() => clock().ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>A sync is running.</summary>
    public bool Active { get { lock (gate) return Flag(data["active"]); } }
    /// <summary>A sync finished and its results are waiting for <see cref="TakeFinished"/>.</summary>
    public bool Ready { get { lock (gate) return Flag(data["ready"]); } }
    /// <summary>Jobs still to do, and being done.</summary>
    public (int Waiting, int Inflight) Left { get { lock (gate) return (Jobs.Count, Inflight.Count); } }

    /// <summary>Canvas asked to slow down: nothing is handed out before this. Null when the sync isn't paused.</summary>
    public DateTimeOffset? PausedUntil
    {
        get
        {
            lock (gate)
                return D(data["pause_until"]) is double until && until > Seconds()
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)(until * 1000)) : null;
        }
    }

    /// <summary>How each class's listings went in this sync (or the last one): class → listing → reading | ok | hidden | failed.</summary>
    public Dictionary<string, Dictionary<string, string>> Sections { get { lock (gate) return SectionsNow(); } }

    Dictionary<string, Dictionary<string, string>> SectionsNow() =>
        SectionStates.ToDictionary(kv => kv.Key, kv => (kv.Value as JsonObject ?? []).ToDictionary(x => x.Key, x => S(x.Value)));

    /// <summary>The extension was sent to Canvas's sign-in page: the sync stopped. Reading clears it.</summary>
    public bool TakeSignedOut()
    {
        lock (gate)
        {
            if (!Flag(data["signed_out"])) return false;
            data["signed_out"] = false;
            Save();
            return true;
        }
    }

    /// <summary>A class's index as this sync has read it so far.</summary>
    CourseIndex Staged(string cls)
    {
        if (!staging.TryGetValue(cls, out var index))
            staging[cls] = index = CourseIndex.LoadStaged(home, cls) ?? new CourseIndex { Class = cls, Staged = new SyncParts() };
        index.Staged ??= new SyncParts();
        dirty.Add(cls);
        return index;
    }

    void Add(string url, string kind, JsonObject tag)
    {
        int seq = (int)(D(data["seq"]) ?? 0) + 1;
        data["seq"] = seq;
        Jobs.Add(new JsonObject { ["id"] = seq.ToString(CultureInfo.InvariantCulture), ["url"] = url, ["kind"] = kind, ["tag"] = tag });
    }

    static JsonObject Tag(string type, string cls, params (string Key, string Value)[] more)
    {
        var t = new JsonObject { ["type"] = type, ["class"] = cls };
        foreach (var (k, v) in more) t[k] = v;
        return t;
    }

    /// <summary>A listing moves on from <c>reading</c> once: to ok, hidden or failed. What it became first stays.</summary>
    void Section(string cls, string listing, string state)
    {
        if (!Listings.Contains(listing)) return;
        if (SectionStates[cls] is not JsonObject mine) SectionStates[cls] = mine = [];
        if (S(mine[listing]) is "" or "reading") mine[listing] = state;
        if (state != "ok" && PagesSoFar[cls] is JsonObject kept) kept.Remove(listing);
    }

    /// <summary>Start a sync of these classes (class → Canvas course id). False if one is already running.</summary>
    public bool Start(string canvasUrl, IReadOnlyDictionary<string, long> courses)
    {
        lock (gate)
        {
            if (Flag(data["active"])) return false;
            data["jobs"] = new JsonArray();
            data["inflight"] = new JsonObject();
            data["changed"] = new JsonObject();
            data["errors"] = new JsonArray();
            data["sections"] = new JsonObject();
            data["pages"] = new JsonObject();
            data["active"] = true;
            data["ready"] = false;
            data["signed_out"] = false;
            data["started"] = clock().ToString("o", CultureInfo.InvariantCulture);
            data["base"] = canvasUrl;
            CourseIndex.DropStaged(home);
            staging.Clear();
            dirty.Clear();
            foreach (var (cls, id) in courses)
            {
                staging[cls] = new CourseIndex { Class = cls, CourseId = id, Staged = new SyncParts() };
                staging[cls].SaveStaged(home);
                string api = $"{canvasUrl}/api/v1/courses/{id}";
                Add($"{api}/assignments?include[]=submission&per_page=100&order_by=due_at", "json", Tag("assignments", cls));
                // Only the student's own: every attempt (submission_history), the grader's comments and rubric marks.
                Add($"{api}/students/submissions?student_ids[]=self&include[]=submission_comments&include[]=rubric_assessment&include[]=assignment"
                    + "&include[]=submission_history&per_page=100", "json", Tag("submissions", cls));
                Add($"{api}/modules?include[]=items&per_page=100", "json", Tag("modules", cls));
                // Not /api/v1/announcements: without an end_date it stops 28 days after its start_date. The course's
                // own list has the whole term, and whether the student has read each one.
                Add($"{api}/discussion_topics?only_announcements=true&per_page=100", "json", Tag("announcements", cls));
                foreach (string listing in Listings) Section(cls, listing, "reading");
            }
            Save();
            return true;
        }
    }

    /// <summary>Up to <paramref name="n"/> jobs for the extension; none while Canvas has asked to slow down. Jobs it
    /// took and never answered (Chrome closed) go back in the queue after ten minutes.</summary>
    public List<CanvasJob> Next(int n = 6)
    {
        lock (gate)
        {
            double t = Seconds();
            bool changed = false;
            foreach (var (id, rec) in Inflight.ToList())
                if (t - (D(rec?["t"]) ?? 0) > InflightSeconds)
                {
                    Jobs.Add(rec!["job"]!.DeepClone());
                    Inflight.Remove(id);
                    changed = true;
                }
            var take = D(data["pause_until"]) is double until && until > t ? [] : Jobs.Take(n).Select(j => j!.AsObject()).ToList();
            foreach (var j in take)
            {
                Jobs.Remove(j);
                Inflight[S(j["id"])] = new JsonObject { ["job"] = j, ["t"] = t };
            }
            if (take.Count > 0 || changed) Save();
            return take.Select(j => new CanvasJob(S(j["id"]), S(j["url"]), S(j["kind"]))).ToList();
        }
    }

    /// <summary>The extension started afresh (installed, reloaded, or asked to sync): whatever it had taken is lost with
    /// its old copy, so it goes back in the queue now instead of in ten minutes.</summary>
    public void Requeue()
    {
        lock (gate)
        {
            if (Inflight.Count == 0) return;
            foreach (var (id, rec) in Inflight.ToList())
            {
                Jobs.Add(rec!["job"]!.DeepClone());
                Inflight.Remove(id);
            }
            Save();
        }
    }

    [GeneratedRegex("\"status\"\\s*:\\s*\"unauthorized\"|not authorized", RegexOptions.IgnoreCase)]
    private static partial Regex NotAuthorized();

    /// <summary>
    /// What an answer means. Canvas says 401 for two different things: "unauthenticated" (nobody is signed in, and
    /// the extension also reports a bounce to the sign-in page as 401) and "unauthorized" (signed in, but this
    /// student can't see that tab). Only the first stops the sync. "403 Forbidden (Rate Limit Exceeded)" asks the
    /// sync to slow down; any other 403 or 404 is something the student can't see.
    /// </summary>
    public static CanvasAnswer Classify(CanvasResult r)
    {
        if (r.SignedOut) return CanvasAnswer.SignedOut;
        string body = BodyText(r);
        if (r.Status == 429 || r.Status == 403 && (body.Contains("Rate Limit Exceeded", StringComparison.OrdinalIgnoreCase) || r.Rate is <= 0))
            return CanvasAnswer.RateLimited;
        if (r.Status == 401) return NotAuthorized().IsMatch(body) ? CanvasAnswer.Hidden : CanvasAnswer.SignedOut;
        if (r.Status is 403 or 404) return CanvasAnswer.Hidden;
        if (r.Status >= 500) return CanvasAnswer.Transient;
        if (r.Status == 0) return r.Error.StartsWith("refused", StringComparison.Ordinal) || r.Error == "too big" ? CanvasAnswer.Failed : CanvasAnswer.Transient;
        return r.Error.Length > 0 || r.Status >= 400 ? CanvasAnswer.Failed : CanvasAnswer.Ok;
    }

    /// <summary>An answer's text: for a file, its bytes when they're short enough to be an error message.</summary>
    static string BodyText(CanvasResult r)
    {
        if (r.Text.Length > 0 || r.B64.Length is 0 or > 64 * 1024) return r.Text;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(r.B64));
        }
        catch (FormatException)
        {
            return "";
        }
    }

    static string Why(CanvasResult r) => r.Error.Length > 0 ? r.Error : r.Status > 0 ? $"Canvas answered {r.Status}" : "no answer";

    /// <summary>
    /// Canvas asked to slow down: nothing goes out for a while, twice as long each time a job sent after the last
    /// pause is refused again. The rest of the burst that caused a pause (sent before it) doesn't lengthen it.
    /// </summary>
    void Pause(double handedOut, double? retryAfter)
    {
        double until = D(data["pause_until"]) ?? 0, now = Seconds();
        if (handedOut >= until)
        {
            double backoff = D(data["backoff"]) is double b && b > 0 ? Math.Min(b * 2, MaxBackoff.TotalSeconds) : FirstBackoff.TotalSeconds;
            data["backoff"] = backoff;
            until = now + backoff;
        }
        data["pause_until"] = Math.Max(until, now + (retryAfter ?? 0));
    }

    /// <summary>An answer came back fine: Canvas is calm again once a job sent after the last pause gets through.</summary>
    void Calm(double handedOut, double? rate)
    {
        double until = D(data["pause_until"]) ?? 0;
        // Canvas says its allowance is used up: this answer is good, the next ones wouldn't be.
        if (rate is <= 0) data["pause_until"] = Math.Max(until, Seconds() + FirstBackoff.TotalSeconds);
        else if (handedOut >= until) data["backoff"] = 0;
    }

    /// <summary>File one answer. False when it isn't one of this crawl's jobs.</summary>
    public bool Handle(CanvasResult r)
    {
        lock (gate)
        {
            if (Inflight[r.Id] is not JsonObject rec) return false;
            Inflight.Remove(r.Id);
            var job = rec["job"]!.AsObject();
            var tag = job["tag"]!.AsObject();
            string cls = S(tag["class"]), type = S(tag["type"]);
            try
            {
                switch (Classify(r))
                {
                    case CanvasAnswer.SignedOut:
                        data["signed_out"] = true;
                        data["jobs"] = new JsonArray();
                        data["inflight"] = new JsonObject();
                        break;
                    case CanvasAnswer.Hidden:
                        Section(cls, type, "hidden");
                        break;
                    case CanvasAnswer.RateLimited:
                        Jobs.Insert(0, job.DeepClone()); // first out when the pause is over: nothing is lost
                        Pause(D(rec["t"]) ?? 0, r.RetryAfter);
                        break;
                    case CanvasAnswer.Transient when (D(tag["tries"]) ?? 0) + 1 < MaxTries:
                        var again = job.DeepClone().AsObject();
                        again["tag"]!["tries"] = (int)(D(tag["tries"]) ?? 0) + 1;
                        Jobs.Add(again);
                        break;
                    case CanvasAnswer.Transient or CanvasAnswer.Failed:
                        Errors.Add($"{cls} {type}: {Why(r)}");
                        Section(cls, type, "failed");
                        break;
                    default:
                        Calm(D(rec["t"]) ?? 0, r.Rate);
                        Dispatch(job, tag, r);
                        break;
                }
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or FormatException or UnauthorizedAccessException)
            {
                Errors.Add($"{cls} {type}: {e.Message}"); // one odd item never stops the sync
                Section(cls, type, "failed");
            }
            if (Flag(data["active"]) && Jobs.Count == 0 && Inflight.Count == 0)
            {
                data["active"] = false;
                data["ready"] = !Flag(data["signed_out"]);
            }
            SaveStaged();
            Save();
            return true;
        }
    }

    /// <summary>Keep what this sync has read of each class that changed, so a restart carries on.</summary>
    void SaveStaged()
    {
        foreach (string cls in dirty)
            if (staging.TryGetValue(cls, out var index)) index.SaveStaged(home);
        dirty.Clear();
    }

    /// <summary>
    /// A finished sync's results, once. Each class's index is promoted (<see cref="CourseIndex.Promoted"/>) and its
    /// Markdown written from it; then: the files that changed (class → paths inside the class folder), what couldn't
    /// be read, how each class's listings went, and each class's index before and after. Null while it's still running.
    /// </summary>
    public CrawlFinished? TakeFinished()
    {
        lock (gate)
        {
            if (!Flag(data["ready"])) return null;
            var sections = SectionsNow();
            var before = new Dictionary<string, CourseIndex?>();
            var after = new Dictionary<string, CourseIndex>();
            string at = clock().ToString("o", CultureInfo.InvariantCulture);
            foreach (var (cls, states) in sections)
            {
                try
                {
                    before[cls] = CourseIndex.Load(home, cls);
                    after[cls] = CourseIndex.Promote(home, cls, states, at, index => Forget(cls, index));
                    Render(cls, after[cls]);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Errors.Add($"{cls}: {e.Message}");
                }
            }
            staging.Clear();
            dirty.Clear();
            var changed = Changed.ToDictionary(kv => kv.Key, kv => (kv.Value as JsonArray ?? []).Select(S).Distinct().ToList());
            var errors = Errors.Select(S).ToList();
            data["ready"] = false;
            data["changed"] = new JsonObject();
            data["pages"] = new JsonObject();
            Save();
            return new CrawlFinished(changed, errors, sections, before, after);
        }
    }

    /// <summary>A file the index says was saved but never arrived (Canvas refused it) isn't there: the next sync asks again.</summary>
    void Forget(string cls, CourseIndex index)
    {
        string root = classDir(cls);
        void Check(FileRef f)
        {
            if (f.Local is { } local && !File.Exists(Path.Combine(root, local))) f.Local = null;
        }
        foreach (var a in index.Assignments)
        {
            a.InstructionFiles.ForEach(Check);
            if (a.Submission is not { } s) continue;
            s.Files.ForEach(Check);
            foreach (var t in s.Attempts) t.Files.ForEach(Check);
            foreach (var c in s.Comments) c.Files.ForEach(Check);
        }
    }

    /// <summary>Each assignment's spec.md and feedback.md, from the class's index (a spec written by hand is left alone).</summary>
    void Render(string cls, CourseIndex index)
    {
        var tz = zone();
        var now = clock();
        foreach (var a in index.Assignments)
        {
            if (a.Folder.Length == 0) continue;
            string dir = Path.Combine(classDir(cls), a.Folder);
            if (SpecHead(dir) is not { } head || head.Contains(Generated, StringComparison.Ordinal))
                Write(cls, Path.Combine(dir, "spec.md"), CanvasMarkdown.Spec(cls, a, tz));
            if (CanvasMarkdown.HasFeedback(a)) Write(cls, Path.Combine(dir, "feedback.md"), CanvasMarkdown.Feedback(cls, a, tz, now));
        }
    }

    // --- what each answer becomes ---------------------------------------------------------------------------------

    [GeneratedRegex("<([^>]+)>;\\s*rel=\"next\"")]
    private static partial Regex NextLink();

    void Dispatch(JsonObject job, JsonObject tag, CanvasResult r)
    {
        string type = S(tag["type"]);
        if (S(job["kind"]) == "bytes")
        {
            FileBytes(tag, Convert.FromBase64String(r.B64));
            return;
        }
        var body = JsonNode.Parse(r.Text.Length > 0 ? r.Text : "null");
        bool more = false;
        if (body is JsonArray && NextLink().Match(r.Link) is { Success: true } m)
        {
            var next = (JsonObject)tag.DeepClone();
            next.Remove("tries"); // a new page gets its own tries
            Add(m.Groups[1].Value, "json", next);
            more = true;
        }
        string cls = S(tag["class"]);
        switch (type)
        {
            case "assignments": AssignmentsPage(cls, body as JsonArray ?? []); break;
            case "submissions": Submissions(cls, body as JsonArray ?? []); break;
            case var whole when Whole.Contains(whole):
                if (S(SectionStates[cls]?[type]) is "failed" or "hidden") break; // an earlier page went wrong: never file part of it
                if (PagesSoFar[cls] is not JsonObject mine) PagesSoFar[cls] = mine = [];
                if (mine[type] is not JsonArray all) mine[type] = all = [];
                foreach (var item in body as JsonArray ?? []) all.Add(item?.DeepClone());
                if (more) break;
                mine.Remove(type);
                if (type == "modules") Modules(cls, all);
                else Announcements(cls, all);
                break;
            case "file_meta": if (body is JsonObject meta) WantFile(cls, meta, S(tag["dir"])); break;
            case "page": if (body is JsonObject page) Page(cls, page, S(tag["dir"])); break;
        }
        if (!more) Section(cls, type, "ok");
    }

    string CanvasDir(string cls) => Path.Combine(classDir(cls), "Canvas");

    /// <summary>Write a file if it's new or different, and note it as changed.</summary>
    void Write(string cls, string path, byte[] content)
    {
        Directory.CreateDirectory(Py.Parent(path));
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
        File.WriteAllBytes(path, content);
        if (Changed[cls] is not JsonArray list) Changed[cls] = list = [];
        list.Add(Path.GetRelativePath(classDir(cls), path).Replace('\\', '/'));
    }

    void Write(string cls, string path, string text) => Write(cls, path, new UTF8Encoding(false).GetBytes(text));

    [GeneratedRegex("[\\\\/:*?\"<>|\\x00-\\x1f]+")]
    private static partial Regex Unsafe();

    /// <summary>A name that works as a file name everywhere.</summary>
    public static string SafeName(string name, int limit = 120)
    {
        string n = Unsafe().Replace(Py.Strip(name), "-").Trim(' ', '.', '-');
        if (n.Length == 0) n = "untitled";
        return n[..Math.Min(n.Length, limit)].TrimEnd(' ', '.', '-');
    }

    /// <summary>Where an assignment's spec and feedback are, inside its class's folder ("Canvas/assignments/Lab 1"), once a
    /// sync has written them.</summary>
    public string? AssignmentFolder(string cls, long id)
    {
        lock (gate) return S(Manifest[$"asgdir:{cls}:{id}"]) is { Length: > 0 } rel ? rel.Replace('\\', '/') : null;
    }

    /// <summary>The start of a spec.md, or null when there isn't one.</summary>
    static string? SpecHead(string dir)
    {
        string spec = Path.Combine(dir, "spec.md");
        if (!File.Exists(spec)) return null;
        using var reader = new StreamReader(spec);
        var buf = new char[1500];
        return new string(buf, 0, reader.ReadBlock(buf, 0, buf.Length));
    }

    /// <summary>A spec's front matter, from its opening "---" to the closing one; "" when it has none.</summary>
    static string FrontMatter(string head)
    {
        if (!head.StartsWith("---", StringComparison.Ordinal)) return "";
        int end = head.IndexOf("\n---", 3, StringComparison.Ordinal);
        return end < 0 ? head : head[..end];
    }

    /// <summary>An assignment's folder: the one already made for its Canvas id, one whose spec.md names it, else a new
    /// one named after it. A spec the sync wrote names its assignment only by the canvas_id in its front matter; one
    /// written by hand may name it by its Canvas id or its Canvas address anywhere.</summary>
    string AssignmentDir(string cls, JsonObject a)
    {
        string key = $"asgdir:{cls}:{D(a["id"]):0}";
        if (S(Manifest[key]) is { Length: > 0 } known) return Path.Combine(classDir(cls), known);
        string root = Path.Combine(CanvasDir(cls), "assignments"), id = Num(D(a["id"])), url = S(a["html_url"]);
        var byId = new Regex($"(?m)^canvas_id: {id}\\r?$");
        // ".../assignments/900" must not claim the spec of ".../assignments/9001".
        var byHand = new Regex($"canvas_id: {id}(?![0-9])" + (url.Length > 0 ? $"|{Regex.Escape(url)}(?![0-9])" : ""));
        // Instructions the sync copied into a spec link other assignments by their full Canvas address, and a link
        // must not hand the linking assignment's folder to the one it links.
        bool Names(string head) => head.Contains(Generated, StringComparison.Ordinal) ? byId.IsMatch(FrontMatter(head)) : byHand.IsMatch(head);
        string? dir = Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal).FirstOrDefault(d => SpecHead(d) is { } head && Names(head))
            : null;
        if (dir is null)
        {
            // Two assignments with the same name (or one renamed on Canvas) get their own folders. Specs are written when
            // the sync finishes, so a folder another assignment already has is taken too.
            string mine = $"asgdir:{cls}:";
            var taken = Manifest.Where(kv => kv.Key.StartsWith(mine, StringComparison.Ordinal)).Select(kv => S(kv.Value)).ToHashSet();
            bool Taken(string d) => File.Exists(Path.Combine(d, "spec.md")) || taken.Contains(Path.GetRelativePath(classDir(cls), d));
            dir = Path.Combine(root, SafeName(S(a["name"]), 80));
            for (int i = 2; Taken(dir); i++) dir = Path.Combine(root, SafeName(S(a["name"]), 76) + $" {i}");
        }
        Manifest[key] = Path.GetRelativePath(classDir(cls), dir);
        return dir;
    }

    /// <summary>The marker in every spec.md the sync writes; one without it was written by hand and is left alone.</summary>
    public const string Generated = "generated_by: study-stash";

    /// <summary>A path inside the class's folder, with / ("Canvas/assignments/Lab 1/spec.md").</summary>
    string Rel(string cls, string path) => Path.GetRelativePath(classDir(cls), path).Replace('\\', '/');

    void AssignmentsPage(string cls, JsonArray list)
    {
        var index = Staged(cls);
        foreach (var a in list.OfType<JsonObject>().Where(Assignments.Published))
        {
            var info = AssignmentInfo.From(a, Rel(cls, AssignmentDir(cls, a)));
            int at = index.Assignments.FindIndex(x => x.Id == info.Id);
            if (at >= 0) index.Assignments[at] = info;
            else index.Assignments.Add(info);
        }
    }

    static string Num(double? d) => (d ?? 0).ToString("0.##", CultureInfo.InvariantCulture);

    void Submissions(string cls, JsonArray list)
    {
        var index = Staged(cls);
        foreach (var s in list.OfType<JsonObject>())
        {
            var a = s["assignment"] as JsonObject;
            long id = (long)(D(s["assignment_id"]) ?? D(a?["id"]) ?? 0);
            if (id == 0) continue;
            string? dir = a is not null ? AssignmentDir(cls, a)
                : S(Manifest[$"asgdir:{cls}:{id}"]) is { Length: > 0 } known ? Path.Combine(classDir(cls), known) : null;
            index.Staged!.Submissions[id] = Submission(cls, s, dir);
            if (a is not null && dir is not null && Assignments.Published(a)) index.Staged.Assignments[id] = AssignmentInfo.From(a, Rel(cls, dir));
        }
    }

    [GeneratedRegex("/users/\\d+/")]
    private static partial Regex UserPath();

    /// <summary>
    /// A submission as the index keeps it, with its files queued: the latest attempt's into submission/, older
    /// attempts' into submission/attempt N/, files attached to comments into feedback/. Audio and video comments stay
    /// links. People are kept by display name only (never ids, avatars or emails); a comment is the student's own
    /// when its author is the submission's owner.
    /// </summary>
    SubmissionInfo Submission(string cls, JsonObject s, string? dir)
    {
        var info = SubmissionInfo.Summary(s);
        var had = new Dictionary<long, FileRef>(); // a file handed in again with the next attempt is saved once
        List<FileRef> Files(JsonNode? list, string into) => (list as JsonArray ?? []).OfType<JsonObject>().Select(meta =>
        {
            long fid = (long)(D(meta["id"]) ?? 0);
            if (fid != 0 && had.TryGetValue(fid, out var known)) return known;
            var f = dir is null ? Describe(meta) : WantFile(cls, meta, Path.Combine(dir, into));
            if (fid != 0) had[fid] = f;
            return f;
        }).ToList();

        info.Files = Files(s["attachments"], "submission");
        info.Body = HtmlText.ToMarkdown(S(s["body"])) is { Length: > 0 } body ? body : S(s["url"]) is { Length: > 0 } link ? $"<{link}>" : "";
        var history = (s["submission_history"] as JsonArray ?? []).OfType<JsonObject>().Where(h => D(h["attempt"]) is not null)
            .GroupBy(h => (int)D(h["attempt"])!.Value).Select(g => g.Last()).OrderBy(h => D(h["attempt"]));
        foreach (var h in history)
        {
            int n = (int)D(h["attempt"])!.Value;
            info.Attempts.Add(new AttemptInfo
            {
                Attempt = n, SubmittedAt = S(h["submitted_at"]) is { Length: > 0 } at ? at : null, Late = Flag(h["late"]),
                Files = n == info.Attempt ? info.Files : Files(h["attachments"], Path.Combine("submission", $"attempt {n}")),
            });
        }
        if (info.Attempts.Count == 0 && info.Attempt is int only && info.SubmittedAt is not null)
            info.Attempts.Add(new AttemptInfo { Attempt = only, SubmittedAt = info.SubmittedAt, Late = info.Late, Files = info.Files });

        if (s["rubric_assessment"] is JsonObject marks)
            foreach (var (criterion, m) in marks)
                if (m is JsonObject mark)
                    info.Marks[criterion] = new RubricMark { Points = D(mark["points"]), RatingId = S(mark["rating_id"]) is { Length: > 0 } r ? r : null, Comment = Py.Strip(S(mark["comments"])) };

        double? owner = D(s["user_id"]);
        foreach (var c in (s["submission_comments"] as JsonArray ?? []).OfType<JsonObject>())
            info.Comments.Add(new CommentInfo
            {
                Author = Py.Strip(S(c["author_name"])),
                At = S(c["created_at"]) is { Length: > 0 } at ? at : null,
                Text = Py.Strip(S(c["comment"])),
                Files = Files(c["attachments"], "feedback"),
                // Canvas's link names the student by id; "self" opens the same recording.
                MediaUrl = c["media_comment"] is JsonObject media && S(media["url"]) is { Length: > 0 } url ? UserPath().Replace(url, "/users/self/") : null,
                Mine = owner is not null && D(c["author_id"]) == owner,
            });
        return info;
    }

    /// <summary>A Canvas file as the index keeps it (its link without the one-time verifier), and why it won't be
    /// saved, if it won't: "locked", "too big", or "video" (audio and video).</summary>
    static FileRef Describe(JsonObject meta)
    {
        string type = S(meta["content-type"]), url = S(meta["url"]);
        return new FileRef
        {
            Id = (long)(D(meta["id"]) ?? 0),
            Name = S(meta["display_name"]) is { Length: > 0 } dn ? dn : S(meta["filename"]) is { Length: > 0 } fn ? fn : Num(D(meta["id"])),
            Size = D(meta["size"]) is double n ? (long)n : null,
            ContentType = type,
            UpdatedAt = S(meta["updated_at"]) is { Length: > 0 } u ? u : null,
            Url = url.Split('?')[0],
            // A video is never saved, whatever its size: "video" says more than "too big".
            Skipped = Flag(meta["locked_for_user"]) || url.Length == 0 ? "locked"
                : type.StartsWith("video/", StringComparison.Ordinal) || type.StartsWith("audio/", StringComparison.Ordinal) ? "video"
                : (D(meta["size"]) ?? 0) > MaxBytes ? "too big"
                : null,
        };
    }

    /// <summary>Queue a file's bytes into <paramref name="destDir"/>, unless this version is already there, and say
    /// where it goes (or why it won't).</summary>
    FileRef WantFile(string cls, JsonObject meta, string destDir, string? name = null)
    {
        var f = Describe(meta);
        if (f.Skipped is not null) return f;
        string path = Path.Combine(destDir, SafeName(name ?? f.Name));
        f.Local = Rel(cls, path);
        string key = $"file:{Num(D(meta["id"]))}:{Path.GetRelativePath(classDir(cls), path)}";
        if (S(Manifest[key]) == S(meta["updated_at"]) && File.Exists(path)) return f;
        Add(S(meta["url"]), "bytes", Tag("file_bytes", cls, ("path", path), ("key", key), ("updated", S(meta["updated_at"]))));
        return f;
    }

    void FileBytes(JsonObject tag, byte[] body)
    {
        Write(S(tag["class"]), S(tag["path"]), body);
        Manifest[S(tag["key"])] = S(tag["updated"]);
    }

    void Modules(string cls, JsonArray list)
    {
        string root = CanvasDir(cls);
        var outline = new StringBuilder($"# {cls}: Canvas modules\n\n_From Canvas; the local copies are linked. Rewritten on every sync._\n\n");
        foreach (var mod in list.OfType<JsonObject>().OrderBy(m => D(m["position"]) ?? 0))
        {
            string folder = Path.Combine(root, "modules", $"{D(mod["position"]) ?? 0:00} {SafeName(S(mod["name"]), 80)}");
            outline.Append("## ").Append(S(mod["name"])).Append("\n\n");
            foreach (var it in (mod["items"] as JsonArray ?? []).OfType<JsonObject>())
            {
                string kind = S(it["type"]), title = S(it["title"]), indent = new(' ', 2 * (int)(D(it["indent"]) ?? 0));
                string Link(string file) => Path.GetRelativePath(root, Path.Combine(folder, file)).Replace('\\', '/').Replace(" ", "%20");
                switch (kind)
                {
                    case "SubHeader":
                        outline.Append(indent).Append("- **").Append(title).Append("**\n");
                        break;
                    case "File":
                        Add(S(it["url"]), "json", Tag("file_meta", cls, ("dir", folder)));
                        outline.Append(indent).Append($"- [{title}]({Link(SafeName(title))}) (file)\n");
                        break;
                    case "Page":
                        Add(S(it["url"]), "json", Tag("page", cls, ("dir", folder)));
                        outline.Append(indent).Append($"- [{title}]({Link(SafeName(title) + ".md")})\n");
                        break;
                    case "ExternalUrl" or "ExternalTool":
                        outline.Append(indent).Append($"- [{title}]({(S(it["external_url"]) is { Length: > 0 } ext ? ext : S(it["html_url"]))}) (link)\n");
                        break;
                    default:
                        outline.Append(indent).Append($"- {title} ({(kind.Length > 0 ? kind.ToLowerInvariant() : "item")}, on Canvas: {S(it["html_url"])})\n");
                        break;
                }
            }
            outline.Append('\n');
        }
        Write(cls, Path.Combine(root, "modules.md"), outline.ToString().TrimEnd() + "\n");
    }

    void Page(string cls, JsonObject page, string dir)
    {
        if (Flag(page["locked_for_user"])) return;
        string text = $"# {S(page["title"])}\n\n_From Canvas ({S(page["html_url"])}), updated {CanvasMarkdown.When(S(page["updated_at"]), zone())}._\n\n{HtmlText.ToMarkdown(S(page["body"]))}\n";
        Write(cls, Path.Combine(dir, SafeName(S(page["title"]) is { Length: > 0 } t ? t : "page") + ".md"), text);
    }

    void Announcements(string cls, JsonArray list)
    {
        if (list.Count == 0) return;
        var sb = new StringBuilder($"# {cls}: announcements\n\n_From Canvas, newest first._\n\n");
        foreach (var a in list.OfType<JsonObject>().OrderByDescending(x => S(x["posted_at"]), StringComparer.Ordinal))
            sb.Append("## ").Append(S(a["title"])).Append("\n_").Append(CanvasMarkdown.When(S(a["posted_at"]), zone())).Append(" · ")
                .Append(S(a["author"]?["display_name"])).Append("_\n\n").Append(HtmlText.ToMarkdown(S(a["message"]))).Append("\n\n");
        Write(cls, Path.Combine(CanvasDir(cls), "announcements.md"), sb.ToString().TrimEnd() + "\n");
    }
}
