using System.Text.Json.Nodes;
using StudyStash.Core.Canvas;

namespace StudyStash.Core.Tests;

/// <summary>Canvas through the Chrome extension: the mirror of each class, the assignments list, and reads for AIs.</summary>
public class CanvasTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    static JsonObject A(string json) => JsonNode.Parse(json)!.AsObject();

    [Theory]
    [InlineData("""{"submission":{"excused":true}}""", "excused")]
    [InlineData("""{"submission":{"workflow_state":"graded","score":9}}""", "graded")]
    [InlineData("""{"submission":{"missing":true}}""", "missing")]
    [InlineData("""{"submission":{"submitted_at":"2026-09-20T00:00:00Z","late":true}}""", "late")]
    [InlineData("""{"submission_types":["on_paper"]}""", "no submission")]
    [InlineData("""{"submission_types":["online_upload"],"due_at":"2026-09-20T00:00:00Z"}""", "past due")]
    [InlineData("""{"submission_types":["online_upload"],"due_at":"2026-10-20T00:00:00Z"}""", "open")]
    public void An_assignment_s_status_reads_like_canvas(string json, string status) =>
        Assert.Equal(status, Assignments.StatusOf(A(json), Now));

    [Fact]
    public void Changes_between_syncs_are_said_in_words()
    {
        // The design's now: Thu 25 Sep 2025, 10:24 in California.
        var now = new DateTime(2025, 9, 25, 10, 24, 0);
        var old = new List<Assignment>
        {
            new("CS 101", 9002, "Problem set 4", "2025-09-16T23:59", 20, "submitted", null, "2025-09-16T21:41", "", Comments: 0),
            new("CALC II", 9301, "Quiz 3 practice", "2025-09-25T23:59", 10, "open", null, "", ""),
            new("BIO 110", 9201, "Osmosis lab report", "2025-09-24T23:59", 20, "submitted", null, "2025-09-24T20:15", ""),
        };
        var after = new List<Assignment>
        {
            new("CS 101", 9002, "Problem set 4", "2025-09-16T23:59", 20, "graded", 18, "2025-09-16T21:41", "", Grade: "18", Comments: 1),
            new("CALC II", 9301, "Quiz 3 practice", "2025-09-26T09:00", 10, "open", null, "", ""),
            new("CS 101", 9001, "Lab 3: recursion traces", "2025-09-30T23:59", 20, "open", null, "", ""),
        };
        var said = Assignments.Diff(old, after, now);
        Assert.Equal(
        [
            "Graded: CS 101 · Problem set 4 · 18/20",
            "Feedback: CS 101 · Problem set 4 · 1 new comment",
            "Moved: CALC II · Quiz 3 practice · now Fri 9:00 AM",
            "New: CS 101 · Lab 3: recursion traces · due Tue 11:59 PM",
            "Removed: BIO 110 · Osmosis lab report",
        ], said.Select(c => c.Text));
        Assert.Equal(["graded", "feedback", "moved", "new", "removed"], said.Select(c => c.Kind));
        Assert.All(said, c => Assert.Null(c.AnnouncementId));
        Assert.Equal((9002L, "CS 101", "Problem set 4"), (said[0].AssignmentId!.Value, said[0].Class, said[0].Name));
    }

    [Fact]
    public void A_canvas_address_is_cleaned_up_from_whatever_was_pasted()
    {
        Assert.Equal("https://school.instructure.com", CanvasSettings.CleanUrl("school.instructure.com/courses/12/"));
        Assert.Equal("https://school.instructure.com", CanvasSettings.CleanUrl(" https://school.instructure.com "));
        Assert.Null(CanvasSettings.CleanUrl("not a url"));
    }

    [Fact]
    public void A_sync_mirrors_specs_feedback_modules_pages_files_and_announcements()
    {
        using var dir = new TempDir();
        var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        var canvas = FakeCanvas.Cs101();
        Assert.True(canvas.Run(sync));

        // Everything is looked at once the sync has finished: Markdown may be written at the end.
        string root = FakeCanvas.CanvasRoot(dir);
        string spec = File.ReadAllText(Path.Combine(root, "assignments", "Lab 3- recursion traces", "spec.md"));
        Assert.Contains("canvas_id: 9001\n", spec);
        Assert.Contains("generated_by: study-stash", spec);
        Assert.Contains("Trace factorial(4) and fib(5) by hand.", spec);
        Assert.Contains("| Correct traces (Every call and every return value is right.) | 10 |", spec);
        string feedback = File.ReadAllText(Path.Combine(root, "assignments", "Problem set 4", "feedback.md"));
        Assert.Contains("- **Score:** 18/20", feedback);
        Assert.Contains("| Stack traces | 8 / 10 | One frame missing | The frame for n = 1 is missing in 3b. |", feedback);
        Assert.Contains("**Dr. Okafor**", feedback);
        Assert.Contains("Watch the last frame in 3b, it’s the one people drop.", feedback);
        Assert.Equal("%PDF-1.4 ps4 answers", File.ReadAllText(Path.Combine(root, "assignments", "Problem set 4", "submission", "ps4-answers.pdf")));
        Assert.StartsWith("def fact(n):", File.ReadAllText(Path.Combine(root, "assignments", "Problem set 4", "submission", "ps4.py")));
        Assert.False(File.Exists(Path.Combine(root, "assignments", "Lab 3- recursion traces", "feedback.md"))); // nothing handed in yet

        string modules = File.ReadAllText(Path.Combine(root, "modules.md"));
        Assert.True(modules.IndexOf("## Week 3 · Scope", StringComparison.Ordinal) < modules.IndexOf("## Week 4 · Recursion", StringComparison.Ordinal));
        Assert.Contains("[Lab 3 instructions](modules/04%20Week%204%20·%20Recursion/Lab%203%20instructions.md)", modules);
        Assert.Contains("[Tracing worksheet](https://app.box.com/s/abc123) (link)", modules);
        string week4 = Path.Combine(root, "modules", "04 Week 4 · Recursion");
        Assert.Contains("## What to hand in", File.ReadAllText(Path.Combine(week4, "Lab 3 instructions.md")));
        Assert.Equal("%PDF-1.4 recursion slides", File.ReadAllText(Path.Combine(week4, "recursion-slides.pdf")));

        string news = File.ReadAllText(Path.Combine(root, "announcements.md"));
        Assert.True(news.IndexOf("## Lab 3 is up", StringComparison.Ordinal) < news.IndexOf("## Welcome to COMP 101", StringComparison.Ordinal));
        Assert.Contains("## Office hours move to Thursday this week", news);
        Assert.Contains("**Tuesday 30 September at 11:59 PM**", news);

        var saved = Assignments.Load(dir.Path);
        Assert.Equal(["Syllabus quiz", "Problem set 3", "Lab 2: tracing loops", "Problem set 4", "Lab 3: recursion traces"], saved.Select(a => a.Name));
        Assert.Equal("open", saved.Single(a => a.Id == 9001).Status);
        Assert.Equal(18, saved.Single(a => a.Id == 9002).Score);
        Assert.Equal("excused", saved.Single(a => a.Id == 9005).Status);
        Assert.Equal("Canvas/assignments/Problem set 4", sync.Crawl.AssignmentFolder("CS 101", 9002));

        var s = CanvasSettings.Load(dir.Path);
        Assert.Equal("", s.Error);
        Assert.False(s.NeedsLogin);
        Assert.All(sync.Crawl.Sections["CS 101"].Values, state => Assert.Equal("ok", state));
        // The whole term's announcements, from the course's own list: never the 28-day /api/v1/announcements window.
        Assert.DoesNotContain(canvas.Requested, u => u.Contains("/api/v1/announcements", StringComparison.Ordinal));
        Assert.Contains(canvas.Requested, u => u.EndsWith("/discussion_topics?only_announcements=true&per_page=100", StringComparison.Ordinal));
        Assert.All(canvas.Requested, u => Assert.StartsWith(FakeCanvas.Base + "/", u));
        Assert.Empty(sync.Work(force: false).Jobs); // synced just now: nothing until the next hour
    }

    [Theory]
    [InlineData("unauthenticated")]
    [InlineData("bounced to sign in")]
    [InlineData("extension says signed out")]
    public void Unauthenticated_or_a_bounce_to_sign_in_stops_the_sync(string how)
    {
        using var dir = new TempDir();
        var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        var canvas = FakeCanvas.Cs101();
        const string path = "/api/v1/courses/4201/assignments";
        _ = how switch
        {
            "unauthenticated" => canvas.Status(path, 401, """{"status":"unauthenticated","errors":[{"message":"user authorization required"}]}"""),
            // An older extension reports Canvas's sign-in page as a bare 401.
            "bounced to sign in" => canvas.On(path, j => new CanvasResult(j.Id, 401, "", "", "", "", FakeCanvas.Base + "/login/canvas")),
            _ => canvas.On(path, j => new CanvasResult(j.Id, 401, "", "", "", "", FakeCanvas.Base + "/login/saml") { SignedOut = true }),
        };
        Assert.True(canvas.Run(sync));
        Assert.Equal(4, canvas.Requested.Count); // the first four asks, then nothing more
        Assert.False(sync.Crawl.Active);
        Assert.False(Directory.Exists(Path.Combine(FakeCanvas.CanvasRoot(dir), "assignments")));
        var s = CanvasSettings.Load(dir.Path);
        Assert.True(s.NeedsLogin);
        Assert.Contains("sign in", s.Error, StringComparison.OrdinalIgnoreCase);

        // Signed in again: the next sync reads everything and the warning goes.
        canvas.Json(path, "cs101-assignments.json");
        Assert.True(canvas.Run(sync));
        s = CanvasSettings.Load(dir.Path);
        Assert.False(s.NeedsLogin);
        Assert.Equal("", s.Error);
        Assert.Equal(5, Assignments.Load(dir.Path).Count);
    }

    [Fact]
    public async Task An_ai_reads_canvas_through_the_extension_and_saves_files_only_in_a_class_folder()
    {
        using var dir = new TempDir();
        CanvasSettings.Update(dir.Path, s => { s.Url = "https://canvas.test"; s.Courses["CS 101"] = 42; });
        var sync = new CanvasSync(dir.Path, c => Path.Combine(dir["pool"], c), _ => { });
        Assert.Equal("Only Canvas addresses (or /api/v1/... paths) can be read.", (await sync.FetchAsync("https://evil.test/x", "json"))["error"]!.GetValue<string>());
        Assert.NotNull((await sync.FetchAsync("/files/1", "bytes", "CS 101/../../etc/passwd"))["error"]);

        var asking = sync.FetchAsync("/api/v1/courses/42/pages", "json");
        var work = sync.Work(force: false);
        Assert.True(work.Hot);
        var job = Assert.Single(work.Jobs);
        Assert.Equal("https://canvas.test/api/v1/courses/42/pages", job.Url);
        sync.Results([new CanvasResult(job.Id, 200, "<https://canvas.test/next>; rel=\"next\"", "[1]", "", "", job.Url)]);
        var r = await asking;
        Assert.Equal("[1]", r["json"]!.GetValue<string>());
        Assert.Equal("https://canvas.test/next", r["next_page"]!.GetValue<string>());

        var saving = sync.FetchAsync("/files/9/download", "bytes", "CS 101/Canvas/files/notes.txt");
        job = Assert.Single(sync.Work(false).Jobs);
        sync.Results([new CanvasResult(job.Id, 200, "", "", Convert.ToBase64String("hi"u8.ToArray()), "", job.Url)]);
        Assert.Equal(2, (await saving)["bytes"]!.GetValue<int>());
        Assert.Equal("hi", File.ReadAllText(Path.Combine(dir["pool"], "CS 101", "Canvas", "files", "notes.txt")));
    }

    [Fact]
    public void The_extension_folder_may_reach_only_this_canvas_and_this_library()
    {
        using var dir = new TempDir();
        Extension.Prepare(dir.Path, "http://mini.tail.ts.net:8787", "k3y", "https://canvas.test/");
        var manifest = JsonNode.Parse(File.ReadAllText(dir["manifest.json"]))!;
        Assert.Equal(["https://canvas.test/*", "https://*.inscloudgate.net/*", "http://mini.tail.ts.net:8787/*"],
            manifest["host_permissions"]!.AsArray().Select(h => h!.GetValue<string>()));
        Assert.Contains("\"key\":\"k3y\"", File.ReadAllText(dir["config.js"]));
        Assert.True(File.Exists(dir["background.js"]));
        Assert.Equal(manifest["version"]!.GetValue<string>(), Extension.Version());
    }
}
