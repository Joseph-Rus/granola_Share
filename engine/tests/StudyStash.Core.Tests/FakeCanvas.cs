using System.Text;
using System.Text.RegularExpressions;
using StudyStash.Core.Canvas;

namespace StudyStash.Core.Tests;

/// <summary>
/// Stands in for Canvas and the Chrome extension together: it answers the jobs a <see cref="CanvasSync"/> hands out, the
/// way the extension would hand Canvas's answers back. Routes are keyed by URL path (the query is ignored except
/// <c>page</c>); anything unknown gets Canvas's 404. Every URL asked is kept in <see cref="Requested"/>.
/// </summary>
public sealed partial class FakeCanvas
{
    public const string Base = "https://canvas.test";
    /// <summary>The design's "now": Thu 25 Sep 2025, 10:24 in California.</summary>
    public static readonly DateTimeOffset DesignNow = new(2025, 9, 25, 17, 24, 0, TimeSpan.Zero);
    /// <summary>The design's time zone: California's.</summary>
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    /// <summary>One answer: status, body, paging link, and what the extension passes on from the headers.</summary>
    public sealed record Reply(int Status, byte[] Body, string Link = "", double? Rate = null, double? RetryAfter = null,
        string Error = "", bool SignedOut = false, string Type = "application/json; charset=utf-8");

    readonly Dictionary<string, Reply> routes = [];
    readonly Dictionary<string, Queue<Reply>> first = [];
    readonly Dictionary<string, Func<CanvasJob, CanvasResult>> custom = [];

    // A path answers one way at a time: setting a route replaces a custom answer and the other way round.
    FakeCanvas Set(string key, Reply reply)
    {
        custom.Remove(key);
        routes[key] = reply;
        return this;
    }

    /// <summary>Every URL the sync asked for, in order.</summary>
    public List<string> Requested { get; } = [];

    static readonly byte[] Missing = Encoding.UTF8.GetBytes("""{"errors":[{"message":"The specified resource does not exist."}]}""");

    /// <summary>A file under Fixtures/canvas, as text.</summary>
    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "canvas", name), Encoding.UTF8);

    static string JsonOf(string jsonOrFixture)
    {
        string t = jsonOrFixture.TrimStart();
        return t.StartsWith('[') || t.StartsWith('{') ? jsonOrFixture : Fixture(jsonOrFixture);
    }

    [GeneratedRegex("[?&]page=(\\d+)")]
    private static partial Regex PageParam();

    /// <summary>"https://canvas.test/api/v1/x?per_page=100&amp;page=2" → "/api/v1/x?page=2"; page 1 is the bare path.</summary>
    static string Key(string url)
    {
        var u = new Uri(url);
        return PageParam().Match(u.Query) is { Success: true } m && m.Groups[1].Value != "1" ? $"{u.AbsolutePath}?page={m.Groups[1].Value}" : u.AbsolutePath;
    }

    /// <summary>JSON (or the name of a fixture file) for a path.</summary>
    public FakeCanvas Json(string path, string jsonOrFixture, int status = 200) =>
        Set(path, new Reply(status, Encoding.UTF8.GetBytes(JsonOf(jsonOrFixture))));

    /// <summary>A listing in pages: each page links to the next, the way Canvas's Link header does.</summary>
    public FakeCanvas Pages(string path, params string[] pages)
    {
        for (int i = 0; i < pages.Length; i++)
            Set(i == 0 ? path : $"{path}?page={i + 1}", new Reply(200, Encoding.UTF8.GetBytes(JsonOf(pages[i])),
                i + 1 < pages.Length ? $"<{Base}{path}?page={i + 2}>; rel=\"next\"" : ""));
        return this;
    }

    /// <summary>Always this status and body.</summary>
    public FakeCanvas Status(string path, int code, string body = "", double? rate = null) =>
        Set(path, new Reply(code, Encoding.UTF8.GetBytes(body), Rate: rate, Type: body.TrimStart().StartsWith('{') ? "application/json" : "text/plain"));

    /// <summary>A file's bytes.</summary>
    public FakeCanvas Bytes(string path, byte[] bytes) => Set(path, new Reply(200, bytes, Type: "application/octet-stream"));

    /// <summary>The first <paramref name="n"/> asks for a path fail like this; later ones get its route.</summary>
    public FakeCanvas FailTimes(string path, int n, int status = 500, string body = "", double? retryAfter = null, double? rate = null)
    {
        if (!first.TryGetValue(path, out var q)) first[path] = q = new Queue<Reply>();
        for (int i = 0; i < n; i++) q.Enqueue(new Reply(status, Encoding.UTF8.GetBytes(body), RetryAfter: retryAfter, Rate: rate, Type: "text/plain"));
        return this;
    }

    /// <summary>Answer a path however a test likes (a sign-in bounce, a dropped connection).</summary>
    public FakeCanvas On(string path, Func<CanvasJob, CanvasResult> answer)
    {
        routes.Remove(path);
        custom[path] = answer;
        return this;
    }

    /// <summary>How many times a path (any page, any query) was asked for.</summary>
    public int Asked(string path) => Requested.Count(u => new Uri(u).AbsolutePath == path);

    /// <summary>What the extension would hand back for this job.</summary>
    public CanvasResult Answer(CanvasJob job)
    {
        Requested.Add(job.Url);
        string key = Key(job.Url);
        if (first.TryGetValue(key, out var q) && q.Count > 0) return Make(job, q.Dequeue());
        if (custom.TryGetValue(key, out var answer)) return answer(job);
        return Make(job, routes.GetValueOrDefault(key) ?? new Reply(404, Missing));
    }

    // Like the extension (protocol 2): a file Canvas sent comes back as base64, an error page for a file as its text,
    // JSON as text.
    static CanvasResult Make(CanvasJob job, Reply r)
    {
        bool file = job.Kind == "bytes" && r.Status is >= 200 and < 300;
        return new(job.Id, r.Status, r.Link, file ? "" : Encoding.UTF8.GetString(r.Body), file ? Convert.ToBase64String(r.Body) : "", r.Error, job.Url)
        {
            Rate = r.Rate, RetryAfter = r.RetryAfter, SignedOut = r.SignedOut, Type = r.Type,
        };
    }

    /// <summary>
    /// Be the extension until the sync is done: ask for work, answer it, hand the answers back. True when the sync
    /// finished (nothing queued, not running, not waiting to be filed); false when it stopped with work left (Canvas
    /// asked it to pause, or it ran out of rounds).
    /// </summary>
    public bool Run(CanvasSync sync, bool force = true, int maxRounds = 500)
    {
        for (int round = 0; round < maxRounds; round++)
        {
            var work = sync.Work(force && round == 0);
            if (work.Jobs.Count == 0) return !sync.Crawl.Active && !sync.Crawl.Ready;
            sync.Results(work.Jobs.Select(Answer).ToList());
        }
        return false;
    }

    /// <summary>COMP 101 (course 4201) as the design shows it: five assignments, Problem set 4 graded with a rubric and
    /// a comment, two modules, four announcements (one unread), a page and the files they link.</summary>
    public static FakeCanvas Cs101() => new FakeCanvas()
        .Json("/api/v1/courses/4201/assignments", "cs101-assignments.json")
        .Json("/api/v1/courses/4201/students/submissions", "cs101-submissions.json")
        .Json("/api/v1/courses/4201/modules", "cs101-modules.json")
        .Json("/api/v1/courses/4201/discussion_topics", "cs101-announcements.json")
        .Json("/api/v1/courses/4201/files/555", "cs101-file-555.json")
        .Json("/api/v1/courses/4201/pages/lab-3-instructions", "cs101-page-lab-3-instructions.json")
        .Bytes("/files/555/download", Encoding.UTF8.GetBytes("%PDF-1.4 recursion slides"))
        .Bytes("/files/8801/download", Encoding.UTF8.GetBytes("%PDF-1.4 ps4 answers"))
        .Bytes("/files/8802/download", Encoding.UTF8.GetBytes("def fact(n):\n    return 1 if n <= 1 else n * fact(n - 1)\n"))
        .Bytes("/files/8701/download", Encoding.UTF8.GetBytes("print('ps3')\n"))
        .Bytes("/files/8601/download", Encoding.UTF8.GetBytes("%PDF-1.4 lab 2"));

    /// <summary>A library whose classes are linked to these Canvas courses, synced by a <see cref="CanvasSync"/> on the
    /// given clock, in California's time zone. Class folders are under <c>pool/</c> in the temp folder.</summary>
    public static CanvasSync Library(TempDir dir, Func<DateTimeOffset> clock, params (string Class, long Course)[] courses)
    {
        CanvasSettings.Update(dir.Path, s =>
        {
            s.Url = Base;
            foreach (var (cls, id) in courses.Length > 0 ? courses : [("CS 101", 4201L)]) s.Courses[cls] = id;
        });
        return new CanvasSync(dir.Path, c => ClassDir(dir, c), _ => { }) { Clock = clock, Zone = Zone };
    }

    public static string ClassDir(TempDir dir, string cls)
    {
        string d = Path.Combine(dir["pool"], cls);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A class's Canvas folder.</summary>
    public static string CanvasRoot(TempDir dir, string cls = "CS 101") => Path.Combine(dir["pool"], cls, "Canvas");
}
