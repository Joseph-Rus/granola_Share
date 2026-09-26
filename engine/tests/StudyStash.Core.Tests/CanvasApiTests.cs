using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using StudyStash.Core.Canvas;
using StudyStash.Library;

namespace StudyStash.Core.Tests;

/// <summary>The JSON the app's Canvas screens and Claude's tools read: state, classes, the Due list, one class's
/// assignments, one assignment's whole detail, modules, files, announcements, pages, notifications, and a raw file
/// download — all built from a real sync, read through the library's actual HTTP API.</summary>
public class CanvasApiTests
{
    const string Password = "maple otter";

    static (Config Cfg, Store Store, LibraryWebOptions Options) LibraryFor(TempDir dir, CanvasSync sync)
    {
        var cfg = new Config(dir.Path, dir["pool"])
        {
            PoolPassword = Password, OllamaEnabled = false,
            Classes = [new ClassDef("CS 101", ["cs101"]), new ClassDef("BIO 110", ["bio110"]), new ClassDef("CALC II", ["calc2"]), new ClassDef("HIST 210", ["hist210"])],
        };
        var store = new Store(cfg.DbPath, cfg.PoolDir);
        return (cfg, store, new LibraryWebOptions
        {
            Canvas = sync, ListModels = _ => Task.FromResult<List<(string, double)>?>(null), Tailscale = () => new TailscaleInfo(),
            Latest = _ => Task.FromResult<Release?>(null), RamGb = () => 16, HostName = () => "library-pc", Nonce = () => "NONCE",
        });
    }

    /// <summary>A synced library: all four of the design's classes, through a real sync against fixtures.</summary>
    static async Task<(TempDir Dir, TestSite Site, Func<DateTimeOffset> Now)> SyncedAsync()
    {
        var dir = new TempDir();
        var now = FakeCanvas.DesignNow;
        var sync = FakeCanvas.Library(dir, () => now, ("CS 101", 4201L), ("BIO 110", 4202L), ("CALC II", 4203L), ("HIST 210", 4204L));
        var canvas = FakeCanvas.Cs101()
            .Json("/api/v1/courses/4202/assignments", "bio110-assignments.json").Json("/api/v1/courses/4202/students/submissions", "[]")
            .Json("/api/v1/courses/4202/modules", "[]").Json("/api/v1/courses/4202/discussion_topics", "[]")
            .Json("/api/v1/courses/4203/assignments", "calc2-assignments.json").Json("/api/v1/courses/4203/students/submissions", "[]")
            .Json("/api/v1/courses/4203/modules", "[]").Json("/api/v1/courses/4203/discussion_topics", "[]")
            .Json("/api/v1/courses/4204/assignments", "hist210-assignments.json").Json("/api/v1/courses/4204/students/submissions", "[]")
            .Json("/api/v1/courses/4204/modules", "[]").Json("/api/v1/courses/4204/discussion_topics", "[]");
        Assert.True(canvas.Run(sync));
        var (cfg, store, options) = LibraryFor(dir, sync);
        var site = await TestSite.StartAsync(b => LibraryWeb.Build(b, cfg, store, new Pipeline(cfg, store, log: _ => { }), options));
        return (dir, site, () => now);
    }

    static async Task<JsonObject> GetAsync(TestSite site, string path)
    {
        var ask = new HttpRequestMessage(HttpMethod.Get, path) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Password) } };
        var r = await site.Client.SendAsync(ask);
        Assert.True(r.IsSuccessStatusCode, $"{path}: {(int)r.StatusCode}");
        return JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject();
    }

    static async Task<HttpResponseMessage> PostAsync(TestSite site, string path, string body)
    {
        var ask = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Password) }, Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return await site.Client.SendAsync(ask);
    }

    [Fact]
    public async Task The_state_endpoint_says_connected_once_a_sync_has_read_something()
    {
        var (dir, site, _) = await SyncedAsync();
        await using var _1 = site;
        using var _2 = dir;
        var state = await GetAsync(site, "/api/v2/canvas/state");
        Assert.Equal("connected", state["state"]!.GetValue<string>());
        Assert.Equal("canvas.test", state["school"]!.GetValue<string>());
        Assert.Null(state["syncing"]);
        Assert.Null(state["error"]);
        Assert.Equal(60, state["poll_minutes"]!.GetValue<int>());
        Assert.NotEqual("", state["last_sync"]!.GetValue<string>());
        Assert.NotNull(state["next_sync"]);
        // Embedded in the app's settings answer too.
        var top = await GetAsync(site, "/api/v2/canvas");
        Assert.Equal("connected", top["state"]!["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("", "", false, false, "not_set_up")]
    [InlineData("https://canvas.test", "", false, false, "no_extension")]
    public void State_priority_before_anything_has_synced(string url, string extensionSeen, bool needsLogin, bool active, string want)
    {
        var s = new CanvasSettings { Url = url, ExtensionSeen = extensionSeen, NeedsLogin = needsLogin };
        if (url.Length > 0) s.Courses["CS 101"] = 4201;
        Assert.Equal(want, CanvasView.StateOf(s, active, FakeCanvas.DesignNow));
    }

    [Fact]
    public void State_priority_signed_out_beats_chrome_away_which_beats_error()
    {
        var now = FakeCanvas.DesignNow;
        var seenLately = now.AddMinutes(-1).ToString("o");
        var seenLongAgo = now.AddMinutes(-30).ToString("o");
        var s = new CanvasSettings { Url = "https://canvas.test", ExtensionSeen = seenLately, NeedsLogin = true, Error = "boom" };
        Assert.Equal("signed_out", CanvasView.StateOf(s, false, now));
        s.NeedsLogin = false;
        s.ExtensionSeen = seenLongAgo;
        Assert.Equal("chrome_away", CanvasView.StateOf(s, false, now));
        s.ExtensionSeen = seenLately;
        Assert.Equal("error", CanvasView.StateOf(s, false, now));
        Assert.Equal("syncing", CanvasView.StateOf(s, true, now)); // syncing outranks a stale error
        s.Error = "";
        Assert.Equal("connected", CanvasView.StateOf(s, false, now));
    }

    [Fact]
    public async Task Classes_lists_every_class_linked_or_not_with_a_suggestion_and_counts()
    {
        var dir = new TempDir();
        var now = FakeCanvas.DesignNow;
        // Only CS 101 is linked; the others show up unlinked with a suggestion (or none) and no counts.
        var sync = FakeCanvas.Library(dir, () => now, ("CS 101", 4201L));
        var canvas = FakeCanvas.Cs101();
        Assert.True(canvas.Run(sync));
        CanvasSettings.Update(dir.Path, s =>
        {
            s.Available["4203"] = "MATH 142 · Calculus II";
            s.CourseInfo["4203"] = new CourseInfo("MATH 142", "MATH 142 · Calculus II", "Fall 2025");
        });
        var (cfg, store, options) = LibraryFor(dir, sync);
        await using var site = await TestSite.StartAsync(b => LibraryWeb.Build(b, cfg, store, new Pipeline(cfg, store, log: _ => { }), options));
        using var _ = store;

        var raw = await site.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v2/canvas/classes") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Password) } });
        var arr = JsonNode.Parse(await raw.Content.ReadAsStringAsync())!.AsArray();
        var cs101 = arr.First(c => c!["class"]!.GetValue<string>() == "CS 101")!;
        Assert.True(cs101["linked"]!.GetValue<bool>());
        Assert.Equal(4201, cs101["canvas"]!["id"]!.GetValue<long>());
        Assert.Equal(5, cs101["counts"]!["to_hand_in"]!.GetValue<int>() + cs101["counts"]!["done"]!.GetValue<int>());
        Assert.False(cs101["files_hidden"]!.GetValue<bool>());
        Assert.Equal("never", cs101["scout"]!["state"]!.GetValue<string>()); // no scout is wired up in this test

        var calc = arr.First(c => c!["class"]!.GetValue<string>() == "CALC II")!;
        Assert.False(calc["linked"]!.GetValue<bool>());
        Assert.Null(calc["canvas"]);
        Assert.Equal("4203", calc["suggested"]!["id"]!.GetValue<string>()); // "CALC II" ~ "Calculus II" by the shared word
        Assert.Equal(0, calc["counts"]!["to_hand_in"]!.GetValue<int>());
    }

    [Fact]
    public void A_class_row_carries_the_scouts_own_report()
    {
        var s = new CanvasSettings { Url = "https://canvas.test" };
        s.Courses["CS 101"] = 4201;
        s.Scouts["CS 101"] = new ScoutReport(true, "14 files saved from Box and the syllabus page.", "2025-09-20T00:00:00Z", 14);
        var row = CanvasView.ClassRow("CS 101", s, null, new ScoutState("done", 14, "2025-09-20T00:00:00Z", "14 files saved from Box and the syllabus page."), FakeCanvas.DesignNow);
        Assert.Equal("done", row["scout"]!["state"]!.GetValue<string>());
        Assert.Equal(14, row["scout"]!["files"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_due_list_groups_every_class_the_way_the_design_does()
    {
        var (dir, site, _) = await SyncedAsync();
        await using var _1 = site;
        using var _2 = dir;
        var due = await GetAsync(site, "/api/v2/canvas/due");
        var groups = due["groups"]!.AsArray().ToDictionary(g => g!["key"]!.GetValue<string>(), g => g!["items"]!.AsArray());

        // Overdue: HIST 210's reading response, due Mon 22 Sep, still not handed in.
        Assert.Contains(groups["overdue"]!, i => i!["name"]!.GetValue<string>().Contains("Treaty of Versailles"));
        Assert.True(groups["overdue"]![0]!["missing"]!.GetValue<bool>());

        // This week: CALC II's quiz (tomorrow) and CS 101's Lab 3 (Tue).
        var week = groups["week"]!.Select(i => i!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Quiz 3 practice", week);
        Assert.Contains("Lab 3: recursion traces", week);

        // Handed in (within the last week): BIO 110's lab report and CS 101's Problem set 4.
        var handedIn = groups["handed_in"]!.Select(i => i!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("Osmosis lab report", handedIn);
        Assert.Contains("Problem set 4", handedIn);

        Assert.True(due["to_hand_in"]!.GetValue<int>() >= 3);
        Assert.NotNull(due["next"]);
    }

    [Fact]
    public async Task One_classs_assignments_split_into_to_hand_in_and_done()
    {
        var (dir, site, _) = await SyncedAsync();
        await using var _1 = site;
        using var _2 = dir;
        var page = await GetAsync(site, "/api/v2/canvas/assignments?class=CS%20101");
        Assert.Equal("CS 101", page["class"]!.GetValue<string>());
        var toHandIn = page["to_hand_in"]!.AsArray();
        var done = page["done"]!.AsArray();
        Assert.Single(toHandIn); // only Lab 3 is still open
        Assert.Equal("Lab 3: recursion traces", toHandIn[0]!["name"]!.GetValue<string>());
        Assert.Equal(4, done.Count); // problem sets 3 and 4, lab 2, the syllabus quiz
        Assert.Contains(done, a => a!["name"]!.GetValue<string>() == "Problem set 4" && a["score_text"]!.GetValue<string>() == "18/20");
    }

    [Fact]
    public async Task One_assignments_whole_detail_has_its_rubric_marks_and_the_graders_comment()
    {
        var (dir, site, _) = await SyncedAsync();
        await using var _1 = site;
        using var _2 = dir;
        var a = await GetAsync(site, "/api/v2/canvas/assignment?class=CS%20101&id=9002");
        Assert.Equal("Problem set 4", a["name"]!.GetValue<string>());
        Assert.Equal("18/20", a["score_text"]!.GetValue<string>());
        Assert.Contains("stack frames", a["instructions"]!.GetValue<string>());
        var rubric = a["rubric"]!.AsArray();
        var stackTraces = rubric.First(r => r!["criterion"]!.GetValue<string>() == "Stack traces")!;
        Assert.Equal(8, stackTraces["mark"]!["points"]!.GetValue<double>());
        Assert.Contains("n = 1", stackTraces["mark"]!["comment"]!.GetValue<string>());
        var comments = a["comments"]!.AsArray();
        Assert.Contains(comments, c => c!["author"]!.GetValue<string>() == "Dr. Okafor" && c["text"]!.GetValue<string>().Contains("Watch the last frame"));
        var files = a["submission"]!["files"]!.AsArray();
        Assert.Contains(files, f => f!["name"]!.GetValue<string>() == "ps4-answers.pdf" && f["format"]!.GetValue<string>() == "PDF");
        Assert.Equal("Canvas/assignments/Problem set 4/spec.md", a["spec"]!.GetValue<string>());
        Assert.Equal("Canvas/assignments/Problem set 4/feedback.md", a["feedback"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unknown_assignment_id_is_a_404()
    {
        var (dir, site, _) = await SyncedAsync();
        await using var _1 = site;
        using var _2 = dir;
        var ask = new HttpRequestMessage(HttpMethod.Get, "/api/v2/canvas/assignment?class=CS%20101&id=99999") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Password) } };
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await site.Client.SendAsync(ask)).StatusCode);
    }

    [Fact]
    public async Task Modules_and_files_answer_the_shape_the_design_expects()
    {
        var (dir, site, _) = await SyncedAsync();
        await using var _1 = site;
        using var _2 = dir;
        var modules = await GetAsync(site, "/api/v2/canvas/modules?class=CS%20101");
        Assert.True(modules.ContainsKey("count"));
        Assert.True(modules["modules"] is JsonArray);
        var files = await GetAsync(site, "/api/v2/canvas/files?class=CS%20101");
        Assert.True(files["allowed"]!.GetValue<bool>());
        Assert.True(files.ContainsKey("count"));
    }

    [Fact]
    public async Task Announcements_seen_in_study_stash_stop_counting_as_new()
    {
        var (dir, site, _) = await SyncedAsync();
        await using var _1 = site;
        using var _2 = dir;
        var before = await GetAsync(site, "/api/v2/canvas/announcements?class=CS%20101");
        // The listing isn't populated by the sync yet (a later task's work): the shape is right regardless.
        Assert.True(before.ContainsKey("count"));
        Assert.True(before.ContainsKey("new"));
        var seen = await PostAsync(site, "/api/v2/canvas/announcements/seen", """{"class":"CS 101","ids":[1,2]}""");
        Assert.True(seen.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Notifications_carry_due_soon_once_and_can_be_marked_seen()
    {
        // The first sync's finds are never news (a class's whole assignment list is new the first time), so the
        // only notification a freshly-synced library has is CALC II's quiz, due inside a day of the design's "now".
        var (dir, site, _) = await SyncedAsync();
        await using var _1 = site;
        using var _2 = dir;

        var first = await GetAsync(site, "/api/v2/canvas/notifications");
        long last = first["last"]!.GetValue<long>();
        var items = first["items"]!.AsArray();
        Assert.Contains(items, i => i!["kind"]!.GetValue<string>() == "due_soon" && i["text"]!.GetValue<string>().Contains("Quiz 3 practice"));
        Assert.All(items, i => Assert.False(i!["seen"]!.GetValue<bool>()));

        // Asking again doesn't add a second "due soon" for the same assignment.
        var again = await GetAsync(site, "/api/v2/canvas/notifications");
        Assert.Equal(items.Count, again["items"]!.AsArray().Count);

        var marked = await PostAsync(site, "/api/v2/canvas/notifications/seen", $$"""{"up_to":{{last}}}""");
        Assert.True(marked.IsSuccessStatusCode);
        var afterSeen = await GetAsync(site, "/api/v2/canvas/notifications");
        Assert.All(afterSeen["items"]!.AsArray(), i => Assert.True(i!["seen"]!.GetValue<bool>()));
        Assert.Empty((await GetAsync(site, "/api/v2/canvas/notifications?after=" + last))["items"]!.AsArray());
    }

    [Fact]
    public void State_says_syncing_with_how_many_classes_are_left()
    {
        var dir = new TempDir();
        var now = FakeCanvas.DesignNow;
        var sync = FakeCanvas.Library(dir, () => now, ("CS 101", 4201L), ("BIO 110", 4202L));
        var canvas = FakeCanvas.Cs101()
            .Json("/api/v1/courses/4202/assignments", "bio110-assignments.json").Json("/api/v1/courses/4202/students/submissions", "[]")
            .Json("/api/v1/courses/4202/modules", "[]").Json("/api/v1/courses/4202/discussion_topics", "[]");
        var initial = sync.Work(force: true).Jobs; // starts the sync; nothing answered yet, so both classes are still "reading"
        Assert.True(sync.Crawl.Active);
        var state = CanvasView.State(sync, now);
        Assert.Equal("syncing", state["state"]!.GetValue<string>());
        Assert.Equal((2, 2), (state["syncing"]!["left"]!.GetValue<int>(), state["syncing"]!["total"]!.GetValue<int>()));
        var left = state["syncing"]!["classes"]!.AsArray().Select(c => c!.GetValue<string>()).ToList();
        Assert.Contains("CS 101", left);
        Assert.Contains("BIO 110", left);

        // Answer what's already out, then let the rest of the sync run to completion: "syncing" is gone.
        sync.Results(initial.Select(canvas.Answer).ToList());
        Assert.True(canvas.Run(sync, force: false));
        Assert.Null(CanvasView.State(sync, now)["syncing"]);
    }

    [Fact]
    public async Task Notifications_after_a_second_sync_carry_a_new_assignment_and_a_new_score()
    {
        var dir = new TempDir();
        var now = FakeCanvas.DesignNow;
        var sync = FakeCanvas.Library(dir, () => now, ("CS 101", 4201L));
        var canvas = FakeCanvas.Cs101();
        Assert.True(canvas.Run(sync));

        // A new assignment appears, and Problem set 4's score changes (the submissions listing is what the
        // promoted index's submission comes from once it's read "ok": T3's CanvasIndex.Submissions).
        var updated = JsonNode.Parse(FakeCanvas.Fixture("cs101-submissions.json"))!.AsArray();
        var ps4 = updated.First(a => a!["assignment_id"]!.GetValue<long>() == 9002)!.AsObject();
        ps4["score"] = 19.0;
        ps4["grade"] = "19";
        canvas.Json("/api/v1/courses/4201/students/submissions", updated.ToJsonString());
        var assignments = JsonNode.Parse(FakeCanvas.Fixture("cs101-assignments.json"))!.AsArray();
        assignments.Add(JsonNode.Parse("""
            {"id":9010,"name":"Lab 4: dynamic programming","description":"<p>Coming soon.</p>","course_id":4201,
             "due_at":"2025-10-08T06:59:00Z","points_possible":20.0,"grading_type":"points","submission_types":["online_upload"],
             "allowed_attempts":-1,"published":true,"has_submitted_submissions":false,
             "html_url":"https://canvas.test/courses/4201/assignments/9010",
             "submission":{"id":89010,"assignment_id":9010,"user_id":5501,"workflow_state":"unsubmitted","submitted_at":null,
                 "graded_at":null,"score":null,"grade":null,"attempt":null,"late":false,"missing":false,"excused":false,"points_deducted":null}}
            """));
        canvas.Json("/api/v1/courses/4201/assignments", assignments.ToJsonString());
        Assert.True(canvas.Run(sync));

        var (cfg, store, options) = LibraryFor(dir, sync);
        await using var site = await TestSite.StartAsync(b => LibraryWeb.Build(b, cfg, store, new Pipeline(cfg, store, log: _ => { }), options));
        using var _ = store;
        var notes = await GetAsync(site, "/api/v2/canvas/notifications");
        var kinds = notes["items"]!.AsArray().Select(i => i!["kind"]!.GetValue<string>()).ToList();
        Assert.Contains("new_assignment", kinds);
        Assert.Contains("new_score", kinds);
        Assert.Contains(notes["items"]!.AsArray(), i => i!["kind"]!.GetValue<string>() == "new_score" && i["text"]!.GetValue<string>().Contains("19"));

        long last = notes["last"]!.GetValue<long>();
        Assert.True((await PostAsync(site, "/api/v2/canvas/notifications/seen", $$"""{"up_to":{{last}}}""")).IsSuccessStatusCode);
        Assert.Empty((await GetAsync(site, "/api/v2/canvas/notifications?after=" + last))["items"]!.AsArray());
    }

    [Fact]
    public async Task Raw_file_serves_a_saved_file_and_refuses_a_path_that_leaves_the_class_folder()
    {
        var (dir, site, _) = await SyncedAsync();
        await using var _1 = site;
        using var _2 = dir;
        var ok = new HttpRequestMessage(HttpMethod.Get, "/api/v2/files/raw?class=CS%20101&path=Canvas%2Fassignments%2FProblem%20set%204%2Fsubmission%2Fps4-answers.pdf")
        { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Password) } };
        var okReply = await site.Client.SendAsync(ok);
        Assert.True(okReply.IsSuccessStatusCode);
        Assert.Equal("%PDF-1.4 ps4 answers", await okReply.Content.ReadAsStringAsync());

        var escape = new HttpRequestMessage(HttpMethod.Get, "/api/v2/files/raw?class=CS%20101&path=..%2F..%2Fcanvas.json")
        { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Password) } };
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await site.Client.SendAsync(escape)).StatusCode);
    }

    [Fact]
    public async Task The_old_keys_the_app_already_reads_are_still_there()
    {
        var (dir, site, _) = await SyncedAsync();
        await using var _1 = site;
        using var _2 = dir;
        var top = await GetAsync(site, "/api/v2/canvas");
        foreach (string key in new[] { "url", "available", "courses", "error", "extension_seen", "syncing", "left", "last_sync" }) Assert.True(top.ContainsKey(key), key);
        var list = (await site.Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v2/assignments?class=CS%20101") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Password) } }));
        var arr = JsonNode.Parse(await list.Content.ReadAsStringAsync())!.AsArray();
        var one = arr.First(a => a!["name"]!.GetValue<string>() == "Problem set 4")!.AsObject();
        foreach (string key in new[] { "class", "id", "name", "due", "points", "status", "score", "submitted", "url", "done", "folder" }) Assert.True(one.ContainsKey(key), key);
    }
}
