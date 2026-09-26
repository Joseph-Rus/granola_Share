using System.Text.Json.Nodes;
using StudyStash.Library;

namespace StudyStash.Core.Tests;

/// <summary>
/// A lecture's whole way, without a microphone or Whisper: recorded on the laptop, sent to the library, filed under its
/// class with notes, and read by Claude. LiveWhisperTests does the same with real Whisper when a model is at hand.
/// </summary>
public class LectureFlowTests
{
    const string Notes = """
        ## Summary
        A recursive function solves a problem by calling itself on a smaller version of it. Each call gets its own frame.

        ## Key points
        - Every recursive function needs a base case that returns without calling itself.
        """;

    static readonly string[] Said =
    [
        "Okay, let's start with where we left off last week.",
        "A recursive function calls itself on a smaller version of the same problem.",
        "Think of each call as a plate on a stack in the cafeteria.",
        "When we hit the base case, the frames come off one by one.",
        "Forget the base case and the stack keeps growing until it overflows.",
        "The midterm will have recursion traces, so practice drawing the stack.",
    ];

    /// <summary>What Whisper wrote down: long enough for the library to write notes from.</summary>
    static List<Spoken> Heard()
    {
        var lines = new List<Spoken>();
        for (int i = 0; lines.Sum(s => s.Text.Length + 8) < Summarize.MinTranscriptChars + 200; i++)
            lines.Add(new Spoken(i * 30, i * 30 + 25, Said[i % Said.Length]));
        return lines;
    }

    static LaptopHost Through(TestSite site) => new()
    {
        Post = (url, body, key) => LibraryHttp.PostAsync(url, body, key, site.Client),
        Get = (url, key) => LibraryHttp.GetAsync(url, key, site.Client),
    };

    [Fact]
    public async Task A_recorded_lecture_is_filed_under_its_class_with_notes_and_claude_reads_it()
    {
        using var dir = new TempDir();
        var cfg = new Config(dir["library"], dir["pool"])
        {
            PoolName = "Sam's library", PoolPassword = "pw", OllamaEnabled = true,
            Classes = [new ClassDef("CS 101", ["cs101"]), new ClassDef("BIO 110", [])],
        };
        Directory.CreateDirectory(cfg.Home);
        using var db = new Store(cfg.DbPath, cfg.PoolDir);
        var pipeline = new Pipeline(cfg, db,
            (_, _, _) => Task.FromResult("""{"class_name":"CS 101","confidence":0.9,"lecture_title":"Recursion and the call stack","topics":["recursion"]}"""),
            (_, _) => Task.FromResult(Notes), log: _ => { });
        await using var site = await TestSite.StartAsync(b => LibraryWeb.Build(b, cfg, db, pipeline, new LibraryWebOptions
        {
            ListModels = _ => Task.FromResult<List<(string, double)>?>(null), Tailscale = () => new TailscaleInfo(), Latest = _ => Task.FromResult<Release?>(null),
        }));

        // The laptop: a lecture recorded for CS 101 that Whisper has finished writing down.
        var store = new LectureStore(dir["laptop"]);
        var heard = Heard();
        var lecture = store.Add(new Lecture
        {
            Id = "rec-20260923-100212-a1b2c3", Started = "2026-09-23T10:02:12-07:00", ClassName = "CS 101", State = LectureState.Sending,
            Seconds = heard[^1].End, TranscribedSeconds = heard[^1].End, Segments = heard, Language = "en",
        });
        Assert.True(lecture.Transcript().Length >= Summarize.MinTranscriptChars);
        var cc = new ClientConfig(dir["laptop"]) { ServerUrl = "http://localhost", PoolKey = "pw", PoolName = "Sam's library", DisplayName = "Sam" };
        var sender = new LectureSender(store, () => cc, Through(site));

        Assert.Equal(1, await sender.StepAsync());
        Assert.Equal(LectureState.Writing, store.Get(lecture.Id)!.State);
        Assert.Equal(0, await sender.StepAsync()); // still being written: asked again later
        Assert.Equal(1, await pipeline.RunPendingAsync());
        Assert.Equal(1, await sender.StepAsync());
        var filed = store.Get(lecture.Id)!;
        Assert.Equal(LectureState.Filed, filed.State);
        Assert.Equal("CS 101", filed.FiledClass);
        Assert.Equal("Recursion and the call stack", filed.FiledTitle);
        Assert.Equal("", filed.Error);

        // The library: filed by the class it was recorded for, with notes, in that class's folder.
        var row = db.Get(lecture.Id)!;
        Assert.Equal(("CS 101", "folder"), (row.ClassName, row.ClassifiedBy));
        Assert.Equal("Sam", row.Owner);
        Assert.Equal(1, row.HasTranscript);
        Assert.Equal(Notes, row.SummaryMd);
        Assert.Equal(Path.GetFullPath(Path.Combine(cfg.PoolDir, "CS 101")), Path.GetDirectoryName(Path.GetFullPath(row.MdPath!)));
        Assert.Contains("Every recursive function needs a base case", File.ReadAllText(row.MdPath!));

        // Claude: the same tools the MCP server gives it, over the library's API.
        var lib = new RemoteLibrary("http://localhost", "pw", site.Client);
        string read = await ClaudeTools.GetLectureAsync(lib, lecture.Id);
        Assert.Contains("# Recursion and the call stack", read);
        Assert.Contains("Class: CS 101", read);
        Assert.Contains("Recorded by: Sam", read);
        Assert.Contains("Every recursive function needs a base case that returns without calling itself.", read);
        string found = await ClaudeTools.SearchAsync(lib, "cafeteria plate", null, 10);
        Assert.Contains($"[{lecture.Id}] Recursion and the call stack (CS 101,", found);
        Assert.Contains("Think of each call as a plate on a stack in the cafeteria.", found);
    }

    [Fact]
    public async Task A_lecture_recorded_with_no_class_goes_under_the_class_the_timetable_says_was_on()
    {
        using var dir = new TempDir();
        // Bio lab starts at midnight on Friday 11 Sep; the timetable has Bio 110 then.
        new Timetable { Classes = [new TimetableClass("Bio 110", ClassTime.ParseMany("Fri 0:00-1:00")!)] }.Save(dir.Path);
        var store = new LectureStore(dir.Path);
        var said = new List<Spoken> { new(0, 4, "Today: cell membranes.") };
        foreach (var (id, started, cls) in new[]
        {
            ("rec-20260911-001000-aaaaaa", "2026-09-11T00:10:00-07:00", ""),
            ("rec-20260910-001000-bbbbbb", "2026-09-10T00:10:00-07:00", ""),
            ("rec-20260911-170000-dddddd", "2026-09-11T17:00:00-07:00", ""),
            ("rec-20260911-002000-cccccc", "2026-09-11T00:20:00-07:00", "CS 101"),
        })
            store.Add(new Lecture { Id = id, Started = started, ClassName = cls, State = LectureState.Sending, Seconds = 4, TranscribedSeconds = 4, Segments = said });
        var posted = new Dictionary<string, JsonObject>();
        var host = new LaptopHost
        {
            Post = (url, body, key) =>
            {
                var sent = (JsonObject)JsonNode.Parse(body)!;
                posted[sent["id"].S()] = sent;
                return Task.FromResult(new JsonObject { ["ok"] = true });
            },
            Get = (url, key) => Task.FromResult<JsonObject?>(new JsonObject { ["status"] = "queued" }),
        };
        var cc = new ClientConfig(dir.Path) { ServerUrl = "http://mini:8787", PoolKey = "pw" };

        Assert.Equal(4, await new LectureSender(store, () => cc, host).StepAsync());
        Assert.Equal("Bio 110", posted["rec-20260911-001000-aaaaaa"]["folder"].S()); // ten minutes into Bio lab
        Assert.Equal("", posted["rec-20260910-001000-bbbbbb"]["folder"].S()); // Thursday, nothing on: the library sorts it
        Assert.Equal("", posted["rec-20260911-170000-dddddd"]["folder"].S()); // Friday evening, long after it
        Assert.Equal("CS 101", posted["rec-20260911-002000-cccccc"]["folder"].S()); // recorded for a class: it stays there
        Assert.Equal("", store.Get("rec-20260911-001000-aaaaaa")!.ClassName); // the laptop's own record is left as recorded
    }

    [Fact]
    public async Task The_library_answers_the_laptop_the_way_httpx_did()
    {
        var library = new FakeOllama((path, _) => path switch
        {
            "/api/ingest" => JsonNode.Parse("""{"ok": true, "class_name": "CS 101"}""")!,
            "/api/notes/a%2Fb/status" => JsonNode.Parse("""{"status": "done"}""")!,
            _ => (System.Net.HttpStatusCode.NotFound, """{"detail": "Not Found"}"""),
        });
        Assert.Equal("CS 101", (await LibraryHttp.PostAsync("http://mini:8787/api/ingest", "{}", "pw", library.Client()))["class_name"].S());
        Assert.Equal("done", (await LibraryHttp.GetAsync("http://mini:8787/api/notes/a%2Fb/status", "pw", library.Client()))!["status"].S());
        Assert.Null(await LibraryHttp.GetAsync("http://mini:8787/api/notes/x/status", "pw", library.Client())); // a library too old to say
        var refused = new FakeOllama((_, _) => (System.Net.HttpStatusCode.Unauthorized, """{"detail": "bad pool password"}"""));
        var e = await Assert.ThrowsAsync<LibraryRefusedException>(() => LibraryHttp.PostAsync("http://mini:8787/api/ingest", "{}", "x", refused.Client()));
        Assert.Equal(401, e.Status);
        Assert.Equal("Client error '401 Unauthorized' for url 'http://mini:8787/api/ingest'\n"
            + "For more information check: https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/401", e.Message);
    }
}
