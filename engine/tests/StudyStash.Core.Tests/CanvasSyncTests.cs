using System.Text;
using System.Text.Json.Nodes;
using StudyStash.Core.Canvas;

namespace StudyStash.Core.Tests;

/// <summary>A Canvas sync that survives what Canvas really does: hidden tabs, sign-outs, rate limits, server errors,
/// listings over several pages, and a second sync of the same thing.</summary>
public class CanvasSyncTests
{
    const string AssignmentsPath = "/api/v1/courses/4201/assignments";
    const string ModulesPath = "/api/v1/courses/4201/modules";
    const string AnnouncementsPath = "/api/v1/courses/4201/discussion_topics";
    const string Unauthorized = """{"status":"unauthorized","errors":[{"message":"user not authorized to perform that action"}]}""";
    const string RateLimitExceeded = "403 Forbidden (Rate Limit Exceeded)\n";

    static CanvasResult Result(int status, string text = "", string error = "", bool signedOut = false, double? rate = null) =>
        new("1", status, "", text, "", error, "") { SignedOut = signedOut, Rate = rate };

    [Theory]
    [InlineData(200, "[]", "", false, null, CanvasAnswer.Ok)]
    [InlineData(401, Unauthorized, "", false, null, CanvasAnswer.Hidden)]
    [InlineData(401, """{"status":"unauthenticated","errors":[{"message":"user authorization required"}]}""", "", false, null, CanvasAnswer.SignedOut)]
    [InlineData(401, "", "", false, null, CanvasAnswer.SignedOut)]
    [InlineData(401, "<!DOCTYPE html><title>Log In to Canvas</title>", "", false, null, CanvasAnswer.SignedOut)]
    [InlineData(200, "[]", "", true, null, CanvasAnswer.SignedOut)]
    [InlineData(403, RateLimitExceeded, "", false, null, CanvasAnswer.RateLimited)]
    [InlineData(403, "", "", false, 0.0, CanvasAnswer.RateLimited)]
    [InlineData(429, "", "", false, null, CanvasAnswer.RateLimited)]
    [InlineData(403, Unauthorized, "", false, 512.5, CanvasAnswer.Hidden)]
    [InlineData(404, """{"errors":[{"message":"The specified resource does not exist."}]}""", "", false, null, CanvasAnswer.Hidden)]
    [InlineData(500, "", "", false, null, CanvasAnswer.Transient)]
    [InlineData(503, "Service Unavailable", "", false, null, CanvasAnswer.Transient)]
    [InlineData(0, "", "TypeError: Failed to fetch", false, null, CanvasAnswer.Transient)]
    [InlineData(0, "", "refused: not a Canvas URL", false, null, CanvasAnswer.Failed)]
    [InlineData(200, "", "too big", false, null, CanvasAnswer.Failed)]
    [InlineData(400, """{"errors":[{"message":"invalid parameter"}]}""", "", false, null, CanvasAnswer.Failed)]
    public void Canvas_answers_are_told_apart(int status, string text, string error, bool signedOut, double? rate, CanvasAnswer answer) =>
        Assert.Equal(answer, Crawl.Classify(Result(status, text, error, signedOut, rate)));

    [Fact]
    public void A_file_canvas_hides_is_told_from_a_sign_out_by_its_body()
    {
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Unauthorized));
        Assert.Equal(CanvasAnswer.Hidden, Crawl.Classify(new CanvasResult("1", 401, "", "", b64, "", "")));
        Assert.Equal(CanvasAnswer.SignedOut, Crawl.Classify(new CanvasResult("1", 401, "", "", "", "", "")));
    }

    [Fact]
    public void The_extension_s_answer_carries_sign_in_rate_and_type()
    {
        var r = CanvasResult.From(JsonNode.Parse("""{"id":"7","status":401,"signed_out":true,"rate":"699.5","retry_after":"30","type":"text/html"}""")!.AsObject());
        Assert.Equal(("7", 401, true, 699.5, 30.0, "text/html"), (r.Id, r.Status, r.SignedOut, r.Rate, r.RetryAfter, r.Type));
        // An older extension sends none of it.
        var old = CanvasResult.From(JsonNode.Parse("""{"id":8,"status":200,"text":"[]"}""")!.AsObject());
        Assert.Equal(("8", false, null, null, ""), (old.Id, old.SignedOut, old.Rate, old.RetryAfter, old.Type));
    }

    [Fact]
    public void A_hidden_tab_401_does_not_sign_you_out()
    {
        using var dir = new TempDir();
        var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        var canvas = FakeCanvas.Cs101().Status(ModulesPath, 401, Unauthorized).Status(AnnouncementsPath, 404, """{"errors":[{"message":"The specified resource does not exist."}]}""");
        Assert.True(canvas.Run(sync));

        var s = CanvasSettings.Load(dir.Path);
        Assert.False(s.NeedsLogin);
        Assert.Equal("", s.Error); // a tab the student can't see isn't a problem to report
        var sections = sync.Crawl.Sections["CS 101"];
        Assert.Equal(("hidden", "hidden", "ok", "ok"), (sections["modules"], sections["announcements"], sections["assignments"], sections["submissions"]));
        string root = FakeCanvas.CanvasRoot(dir);
        Assert.True(File.Exists(Path.Combine(root, "assignments", "Lab 3- recursion traces", "spec.md")));
        Assert.True(File.Exists(Path.Combine(root, "assignments", "Problem set 4", "submission", "ps4-answers.pdf")));
        Assert.False(File.Exists(Path.Combine(root, "modules.md")));
        Assert.Equal(5, Assignments.Load(dir.Path).Count);
    }

    [Fact]
    public void Rate_limited_canvas_pauses_then_carries_on_losing_nothing()
    {
        using var dir = new TempDir();
        var now = FakeCanvas.DesignNow;
        var sync = FakeCanvas.Library(dir, () => now);
        var canvas = FakeCanvas.Cs101().FailTimes(AssignmentsPath, 2, 403, RateLimitExceeded);

        // The first ask is refused: nothing goes out for 30 seconds, and the extension isn't told to hurry back.
        Assert.False(canvas.Run(sync));
        Assert.Equal(now.AddSeconds(30), sync.Crawl.PausedUntil);
        var work = sync.Work(force: false);
        Assert.Empty(work.Jobs);
        Assert.False(work.Hot);
        now = now.AddSeconds(29);
        work = sync.Work(force: false);
        Assert.Empty(work.Jobs);
        Assert.False(work.Hot);

        // Once the pause is over the refused ask goes first; refused again, the pause doubles.
        now = now.AddSeconds(2);
        Assert.Null(sync.Crawl.PausedUntil);
        int asked = canvas.Requested.Count;
        Assert.False(canvas.Run(sync, force: false));
        Assert.Contains("/assignments?", canvas.Requested[asked], StringComparison.Ordinal);
        Assert.Equal(now.AddSeconds(60), sync.Crawl.PausedUntil);

        now = now.AddSeconds(61);
        Assert.True(canvas.Run(sync, force: false));
        Assert.Equal(3, canvas.Asked(AssignmentsPath));
        Assert.Equal(5, Assignments.Load(dir.Path).Count);
        Assert.True(File.Exists(Path.Combine(FakeCanvas.CanvasRoot(dir), "assignments", "Lab 3- recursion traces", "spec.md")));
        Assert.Equal("", CanvasSettings.Load(dir.Path).Error);
        Assert.All(sync.Crawl.Sections["CS 101"].Values, state => Assert.Equal("ok", state));

        // Calm again, the next refusal starts from 30 seconds, unless Canvas asks for longer.
        canvas.FailTimes(AssignmentsPath, 1, 429, "", retryAfter: 120);
        Assert.False(canvas.Run(sync));
        Assert.Equal(now.AddSeconds(120), sync.Crawl.PausedUntil);
        now = now.AddSeconds(121);
        Assert.True(canvas.Run(sync, force: false));
        canvas.FailTimes(AssignmentsPath, 1, 403, "", rate: 0);
        Assert.False(canvas.Run(sync));
        Assert.Equal(now.AddSeconds(30), sync.Crawl.PausedUntil);
    }

    [Fact]
    public void A_server_error_is_retried_then_reported()
    {
        // Twice 500, then fine: nobody hears about it.
        using (var dir = new TempDir())
        {
            var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
            var canvas = FakeCanvas.Cs101().FailTimes(ModulesPath, 2, 500);
            Assert.True(canvas.Run(sync));
            Assert.Equal(3, canvas.Asked(ModulesPath));
            Assert.True(File.Exists(Path.Combine(FakeCanvas.CanvasRoot(dir), "modules.md")));
            Assert.Equal("", CanvasSettings.Load(dir.Path).Error);
        }

        // Canvas keeps failing, or the connection keeps dropping: asked three times, then reported by name.
        using (var dir = new TempDir())
        {
            var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
            var canvas = FakeCanvas.Cs101().Status(ModulesPath, 503, "Service Unavailable")
                .On(AnnouncementsPath, j => new CanvasResult(j.Id, 0, "", "", "", "TypeError: Failed to fetch", ""));
            Assert.True(canvas.Run(sync));
            Assert.Equal(3, canvas.Asked(ModulesPath));
            Assert.Equal(3, canvas.Asked(AnnouncementsPath));
            var sections = sync.Crawl.Sections["CS 101"];
            Assert.Equal(("failed", "failed", "ok"), (sections["modules"], sections["announcements"], sections["assignments"]));
            string error = CanvasSettings.Load(dir.Path).Error;
            Assert.Contains("CS 101 modules", error);
            Assert.Contains("CS 101 announcements", error);
            Assert.Contains("503", error);
            Assert.False(CanvasSettings.Load(dir.Path).NeedsLogin);
            Assert.True(File.Exists(Path.Combine(FakeCanvas.CanvasRoot(dir), "assignments", "Problem set 4", "feedback.md")));
            Assert.False(File.Exists(Path.Combine(FakeCanvas.CanvasRoot(dir), "modules.md")));
        }
    }

    [Fact]
    public async Task An_ai_reading_a_hidden_tab_is_told_so_not_that_chrome_is_signed_out()
    {
        using var dir = new TempDir();
        var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        var canvas = new FakeCanvas().Status("/api/v1/courses/4201/files", 401, Unauthorized).Status("/files/9/download", 404, "Not found")
            .On("/api/v1/courses/4201/users", j => new CanvasResult(j.Id, 401, "", "", "", "", FakeCanvas.Base + "/login/canvas"));
        async Task<JsonObject> Read(string path, string kind = "json", string saveTo = "")
        {
            var reading = sync.FetchAsync(path, kind, saveTo);
            sync.Results(sync.Work(force: false).Jobs.Select(canvas.Answer).ToList());
            return await reading;
        }

        var hidden = await Read("/api/v1/courses/4201/files");
        Assert.Null(hidden["error"]);
        Assert.Equal(401, hidden["status"]!.GetValue<int>());
        Assert.Contains("not authorized", hidden["json"]!.GetValue<string>());
        Assert.Equal("Chrome isn't signed in to Canvas.", (await Read("/api/v1/courses/4201/users"))["error"]!.GetValue<string>());
        var missing = await Read("/files/9/download", "bytes", "CS 101/Canvas/files/notes.pdf");
        Assert.Contains("404", missing["error"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(FakeCanvas.CanvasRoot(dir), "files", "notes.pdf")));
    }

    /// <summary>A fixture's items split into pages of these sizes, as JSON arrays.</summary>
    static string[] Split(string fixture, params int[] sizes)
    {
        var all = JsonNode.Parse(FakeCanvas.Fixture(fixture))!.AsArray().Select(n => n!.DeepClone()).ToList();
        var pages = new List<string>();
        int at = 0;
        foreach (int n in sizes)
        {
            pages.Add(new JsonArray(all.Skip(at).Take(n).ToArray()).ToJsonString());
            at += n;
        }
        return [.. pages];
    }

    [Fact]
    public void Every_page_of_announcements_and_modules_is_kept()
    {
        using var dir = new TempDir();
        var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        var canvas = FakeCanvas.Cs101()
            .Pages(ModulesPath, Split("cs101-modules.json", 1, 1))
            .Pages(AnnouncementsPath, Split("cs101-announcements.json", 2, 1, 1));
        Assert.True(canvas.Run(sync));
        Assert.Equal(2, canvas.Asked(ModulesPath));
        Assert.Equal(3, canvas.Asked(AnnouncementsPath));

        string root = FakeCanvas.CanvasRoot(dir);
        string modules = File.ReadAllText(Path.Combine(root, "modules.md"));
        Assert.Contains("## Week 3 · Scope", modules);
        Assert.Contains("## Week 4 · Recursion", modules);
        Assert.Contains("[Lab 3 instructions]", modules);
        Assert.True(File.Exists(Path.Combine(root, "modules", "04 Week 4 · Recursion", "recursion-slides.pdf")));
        string news = File.ReadAllText(Path.Combine(root, "announcements.md"));
        string[] titles = ["## Lab 3 is up", "## Office hours move to Thursday this week", "## Problem set 3 solutions", "## Welcome to COMP 101"];
        Assert.All(titles, t => Assert.Contains(t, news));
        Assert.Equal(titles, titles.OrderBy(t => news.IndexOf(t, StringComparison.Ordinal))); // newest first, across pages

        // A page Canvas garbles makes the listing fail: the next sync never writes the outline from the pages before it.
        canvas.Pages(ModulesPath, Split("cs101-modules.json", 1)[0], "[{\"id\":1,\"name\":\"Week 1\"");
        Assert.True(canvas.Run(sync));
        Assert.Equal("failed", sync.Crawl.Sections["CS 101"]["modules"]);
        Assert.Equal(modules, File.ReadAllText(Path.Combine(root, "modules.md")));
    }

    [Theory]
    [InlineData("the whole listing fails")]
    [InlineData("its second page fails")]
    public void A_failed_assignments_listing_keeps_what_was_known(string how)
    {
        using var dir = new TempDir();
        var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        var canvas = FakeCanvas.Cs101().Pages(AssignmentsPath, Split("cs101-assignments.json", 3, 2));
        Assert.True(canvas.Run(sync));
        byte[] known = File.ReadAllBytes(Assignments.PathIn(dir.Path));
        Assert.Equal(5, Assignments.Load(dir.Path).Count);

        if (how == "the whole listing fails") canvas.Status(AssignmentsPath, 500);
        else canvas.Status(AssignmentsPath + "?page=2", 502, "Bad Gateway");
        var said = new List<string>();
        sync.Finished += said.AddRange;
        Assert.True(canvas.Run(sync));

        var s = CanvasSettings.Load(dir.Path);
        Assert.DoesNotContain(s.Changes, c => c.StartsWith("Removed", StringComparison.Ordinal));
        Assert.Empty(said);
        Assert.Equal(known, File.ReadAllBytes(Assignments.PathIn(dir.Path)));
        Assert.Contains("CS 101 assignments", s.Error);
        Assert.Equal("failed", sync.Crawl.Sections["CS 101"]["assignments"]);
    }

    [Fact]
    public void A_hand_written_spec_is_left_alone()
    {
        using var dir = new TempDir();
        string root = FakeCanvas.CanvasRoot(dir);
        // Written by hand before any sync, naming the assignment by its Canvas address: that folder is the assignment's.
        string mine = Path.Combine(root, "assignments", "Lab 3 (my plan)");
        Directory.CreateDirectory(mine);
        const string plan = "# Lab 3\n\nMy plan: https://canvas.test/courses/4201/assignments/9001\n\n- trace fib(5) first\n";
        File.WriteAllText(Path.Combine(mine, "spec.md"), plan);
        var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        var canvas = FakeCanvas.Cs101();
        Assert.True(canvas.Run(sync));
        Assert.Equal(plan, File.ReadAllText(Path.Combine(mine, "spec.md")));
        Assert.Equal("Canvas/assignments/Lab 3 (my plan)", sync.Crawl.AssignmentFolder("CS 101", 9001));
        Assert.False(Directory.Exists(Path.Combine(root, "assignments", "Lab 3- recursion traces")));

        // One the sync wrote, then rewritten by hand, stays as the student left it when Canvas changes.
        string ps4 = Path.Combine(root, "assignments", "Problem set 4", "spec.md");
        Assert.Contains(Crawl.Generated, File.ReadAllText(ps4));
        File.WriteAllText(ps4, "# Problem set 4\n\nMy notes: 3b needs the n = 1 frame.\n");
        canvas.Json(AssignmentsPath, FakeCanvas.Fixture("cs101-assignments.json").Replace("Five short problems", "Six short problems", StringComparison.Ordinal));
        Assert.True(canvas.Run(sync));
        Assert.Equal("# Problem set 4\n\nMy notes: 3b needs the n = 1 frame.\n", File.ReadAllText(ps4));
        Assert.False(Directory.Exists(Path.Combine(root, "assignments", "Problem set 4 2")));
        Assert.True(File.Exists(Path.Combine(root, "assignments", "Problem set 4", "feedback.md")));
    }

    /// <summary>Every file under a folder, with its bytes and when it was written.</summary>
    static Dictionary<string, (string Bytes, DateTime Written)> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => (Convert.ToBase64String(File.ReadAllBytes(f)), File.GetLastWriteTimeUtc(f)));

    [Fact]
    public void Syncing_the_same_canvas_twice_changes_nothing()
    {
        using var dir = new TempDir();
        var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        var canvas = FakeCanvas.Cs101();
        Assert.True(canvas.Run(sync));
        var files = Snapshot(dir["pool"]);
        byte[] list = File.ReadAllBytes(Assignments.PathIn(dir.Path));
        Assert.Contains(files.Keys, f => f.EndsWith("ps4-answers.pdf", StringComparison.Ordinal));
        int asked = canvas.Requested.Count;
        var said = new List<string>();
        sync.Finished += said.AddRange;

        Assert.True(canvas.Run(sync));
        Assert.Equal(files, Snapshot(dir["pool"]));
        Assert.Equal(list, File.ReadAllBytes(Assignments.PathIn(dir.Path)));
        // Files already here, at the same version, aren't downloaded again.
        Assert.DoesNotContain(canvas.Requested.Skip(asked), u => u.Contains("/download", StringComparison.Ordinal));
        Assert.Empty(said);
        var s = CanvasSettings.Load(dir.Path);
        Assert.Empty(s.Changes);
        Assert.Equal("", s.Error);
    }
}
