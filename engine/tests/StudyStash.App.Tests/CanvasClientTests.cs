using System.Net;
using StudyStash.App.Services;

namespace StudyStash.App.Tests;

public class CanvasClientTests
{
    static CanvasClient Client(FakeLibrary fake) => new("https://library.test", "test-key", fake.Client());

    // ---- canvas/state ----

    [Fact]
    public async Task StateAsync_sends_the_bearer_key_to_the_right_path()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/state", "state-connected");
        await Client(fake).StateAsync(TestContext.Current.CancellationToken);

        var sent = Assert.Single(fake.Requests);
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/api/v2/canvas/state", sent.Path);
        Assert.Equal("Bearer test-key", sent.Authorization);
    }

    [Fact]
    public async Task StateAsync_parses_the_design_s_connected_state()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/state", "state-connected");
        var s = await Client(fake).StateAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(s);
        Assert.Equal("connected", s.Status);
        Assert.Equal("1.4", s.Extension?.Version);
        Assert.Equal(new DateTimeOffset(2025, 9, 25, 17, 22, 0, TimeSpan.Zero), s.Extension?.Seen);
        Assert.Equal(new DateTimeOffset(2025, 9, 25, 17, 24, 0, TimeSpan.Zero), s.LastSync);
        Assert.Equal(new DateTimeOffset(2025, 9, 25, 18, 24, 0, TimeSpan.Zero), s.NextSync);
        Assert.Equal(60, s.PollMinutes);
        Assert.Null(s.Syncing);
        Assert.Null(s.Error);
    }

    [Fact]
    public async Task StateAsync_parses_syncing_with_its_classes()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/state", "state-syncing");
        var s = await Client(fake).StateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("syncing", s!.Status);
        Assert.Equal(3, s.Syncing?.Left);
        Assert.Equal(5, s.Syncing?.Total);
        Assert.Equal(["BIO 110", "CALC II", "HIST 210"], s.Syncing?.Classes);
    }

    [Fact]
    public async Task StateAsync_parses_the_extension_s_own_update()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/state", "state-updated");
        var s = await Client(fake).StateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1.3", s!.Extension?.Updated?.From);
        Assert.Equal("1.4", s.Extension?.Updated?.To);
    }

    [Fact]
    public async Task StateAsync_parses_an_error_and_when_it_happened()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/state", "state-error");
        var s = await Client(fake).StateAsync(TestContext.Current.CancellationToken);

        Assert.Equal("error", s!.Status);
        Assert.Contains("didn", s.Error?.Text);
        Assert.Equal(new DateTimeOffset(2025, 9, 25, 17, 24, 0, TimeSpan.Zero), s.Error?.At);
    }

    [Theory]
    [InlineData("{}", "not_set_up")]
    [InlineData("""{"url": "https://school.instructure.com"}""", "no_extension")]
    [InlineData("""{"url": "https://school.instructure.com", "extension_seen": "2025-09-25T17:20:00Z", "needs_login": true}""", "signed_out")]
    [InlineData("""{"url": "https://school.instructure.com", "extension_seen": "2025-09-25T17:20:00Z", "syncing": true, "left": 3}""", "syncing")]
    [InlineData("""{"url": "https://school.instructure.com", "extension_seen": "2025-09-25T17:20:00Z", "error": "Couldn't reach Canvas."}""", "error")]
    [InlineData("""{"url": "https://school.instructure.com", "extension_seen": "2025-09-25T17:20:00Z"}""", "connected")]
    [InlineData("""{"url": "https://school.instructure.com", "extension_seen": "2025-09-25T17:20:00Z", "extension_update": {"from": "1.3", "to": "1.4", "at": "2025-09-25T17:00:00Z"}}""", "updated")]
    public async Task StateAsync_derives_a_state_from_an_older_library_s_old_keys(string oldOverview, string expected)
    {
        var fake = new FakeLibrary()
            .Status(HttpMethod.Get, "/api/v2/canvas/state", HttpStatusCode.NotFound)
            .Json(HttpMethod.Get, "/api/v2/canvas", oldOverview);
        var s = await Client(fake).StateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, s!.Status);
    }

    [Fact]
    public async Task StateAsync_carries_the_syncing_count_from_the_old_left_key()
    {
        var fake = new FakeLibrary()
            .Status(HttpMethod.Get, "/api/v2/canvas/state", HttpStatusCode.NotFound)
            .Json(HttpMethod.Get, "/api/v2/canvas", """{"url": "https://x", "extension_seen": "2025-09-25T17:20:00Z", "syncing": true, "left": 3}""");
        var s = await Client(fake).StateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, s!.Syncing?.Left);
    }

    [Fact]
    public async Task StateAsync_is_null_when_the_library_has_no_canvas_endpoint_at_all()
    {
        var fake = new FakeLibrary()
            .Status(HttpMethod.Get, "/api/v2/canvas/state", HttpStatusCode.NotFound)
            .Status(HttpMethod.Get, "/api/v2/canvas", HttpStatusCode.NotFound);
        Assert.Null(await Client(fake).StateAsync(TestContext.Current.CancellationToken));
    }

    // ---- canvas/classes ----

    [Fact]
    public async Task ClassesAsync_parses_all_four_rows()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/classes", "classes");
        var rows = await Client(fake).ClassesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, rows!.Count);
        var cs101 = rows.Single(r => r.Class == "CS 101");
        Assert.True(cs101.Linked);
        Assert.Equal("COMP 101", cs101.Canvas?.Code);
        Assert.Equal("done", cs101.Scout?.State);
        Assert.Equal(14, cs101.Scout?.Files);
        Assert.Equal(1, cs101.Counts.ToHandIn);
        Assert.Equal(23, cs101.Counts.Files);

        var hist210 = rows.Single(r => r.Class == "HIST 210");
        Assert.False(hist210.Linked);
        Assert.Null(hist210.Canvas);
        Assert.Equal("4204", hist210.Suggested);

        var bio110 = rows.Single(r => r.Class == "BIO 110");
        Assert.Equal("exploring", bio110.Scout?.State);
    }

    [Fact]
    public async Task ClassesAsync_asks_for_no_query()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/classes", "classes");
        await Client(fake).ClassesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("", Assert.Single(fake.Requests).Query);
    }

    // ---- canvas/due ----

    [Fact]
    public async Task DueAsync_parses_the_groups_and_the_next_item()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/due", "due");
        var due = await Client(fake).DueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, due!.ToHandIn);
        Assert.Equal("Quiz 3 practice", due.Next?.Name);
        Assert.Equal(3, due.Groups.Count);
        var overdue = due.Groups.Single(g => g.Key == "overdue");
        Assert.True(overdue.Items.Single().Missing);
        var handedIn = due.Groups.Single(g => g.Key == "handed_in");
        Assert.Equal("18/20", handedIn.Items.Single(i => i.Name == "Problem set 4").ScoreText);
    }

    // ---- canvas/assignments, canvas/assignment ----

    [Fact]
    public async Task AssignmentsAsync_escapes_a_class_name_with_a_space()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/assignments", "assignments-cs101");
        await Client(fake).AssignmentsAsync("CS 101", TestContext.Current.CancellationToken);

        Assert.Equal("?class=CS%20101", Assert.Single(fake.Requests).Query);
    }

    [Fact]
    public async Task AssignmentsAsync_escapes_a_class_name_with_a_slash_too()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/assignments", "assignments-cs101");
        await Client(fake).AssignmentsAsync("CS 101/A", TestContext.Current.CancellationToken);

        Assert.Equal("?class=CS%20101%2FA", Assert.Single(fake.Requests).Query);
    }

    [Fact]
    public async Task AssignmentsAsync_parses_to_hand_in_and_done()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/assignments", "assignments-cs101");
        var a = await Client(fake).AssignmentsAsync("CS 101", TestContext.Current.CancellationToken);

        Assert.Equal("CS 101", a!.Class);
        Assert.Equal("Lab 3: recursion traces", Assert.Single(a.ToHandIn).Name);
        Assert.Equal(4, a.Done.Count);
        Assert.Contains(a.Done, d => d.Name == "Syllabus quiz" && d.Excused);
        Assert.Contains(a.Done, d => d.Name == "Lab 2: tracing loops" && d.Late && d.ScoreText == "17/20");
    }

    [Fact]
    public async Task AssignmentAsync_sends_both_class_and_id()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/assignment", "assignment-9001");
        await Client(fake).AssignmentAsync("CS 101", "9001", TestContext.Current.CancellationToken);

        Assert.Equal("?class=CS%20101&id=9001", Assert.Single(fake.Requests).Query);
    }

    [Fact]
    public async Task AssignmentAsync_parses_a_rubric_with_no_marks_yet()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/assignment", "assignment-9001");
        var a = await Client(fake).AssignmentAsync("CS 101", "9001", TestContext.Current.CancellationToken);

        Assert.Equal(3, a!.Rubric.Count);
        Assert.All(a.Rubric, r => Assert.Null(r.Mark));
        Assert.Equal(10, a.Rubric[0].Points);
        Assert.Null(a.Submission);
    }

    [Fact]
    public async Task AssignmentAsync_parses_rubric_marks_a_comment_and_the_submission()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/assignment", "assignment-9002");
        var a = await Client(fake).AssignmentAsync("CS 101", "9002", TestContext.Current.CancellationToken);

        var graded = a!.Rubric.Single(r => r.Criterion == "Recursive case");
        Assert.Equal(8, graded.Mark?.Points);
        Assert.Equal("The frame for n = 1 is missing in 3b.", graded.Mark?.Comment);
        Assert.Equal(18, a.Submission?.Score);
        Assert.Equal(2, a.Files.Count);
        Assert.Contains(a.Files, f => f.Name == "ps4-answers.pdf" && f.Size == 1258291);
        var comment = Assert.Single(a.Comments);
        Assert.Equal("Dr. Okafor", comment.Author);
    }

    // ---- canvas/modules, canvas/files, canvas/announcements ----

    [Fact]
    public async Task ModulesAsync_parses_a_module_s_items_and_their_sources()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/modules", "modules-cs101");
        var m = await Client(fake).ModulesAsync("CS 101", TestContext.Current.CancellationToken);

        Assert.Equal(8, m!.Count);
        var week4 = m.Modules.Single(x => x.Name == "Week 4 · Recursion");
        Assert.Equal(4, week4.Items.Count);
        Assert.Contains(week4.Items, i => i.Title == "Tracing worksheet" && i.Source == "box");
        Assert.Contains(week4.Items, i => i.Title == "Stack diagrams handout" && i.Source == "drive");
        var week3 = m.Modules.Single(x => x.Name == "Week 3 · Scope");
        Assert.Equal(5, week3.Items.Count);
    }

    [Fact]
    public async Task FilesAsync_parses_the_count_and_the_files()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/files", "files-cs101");
        var f = await Client(fake).FilesAsync("CS 101", TestContext.Current.CancellationToken);

        Assert.True(f!.Allowed);
        Assert.Equal(23, f.Count);
        Assert.Contains(f.Files, x => x.Folder == "Slides" && x.Name == "week1-intro.pdf");
    }

    [Fact]
    public async Task AnnouncementsAsync_parses_the_new_count_and_the_items()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/announcements", "announcements-cs101");
        var a = await Client(fake).AnnouncementsAsync("CS 101", TestContext.Current.CancellationToken);

        Assert.Equal(4, a!.Count);
        Assert.Equal(1, a.New);
        Assert.Single(a.Items, x => x.New);
    }

    [Fact]
    public async Task MarkAnnouncementsSeenAsync_posts_the_class_and_the_ids()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Post, "/api/v2/canvas/announcements/seen", "{}");
        await Client(fake).MarkAnnouncementsSeenAsync("CS 101", ["an1", "an2"], TestContext.Current.CancellationToken);

        var sent = Assert.Single(fake.Requests);
        Assert.Contains("\"class\":\"CS 101\"", sent.Body);
        Assert.Contains("\"an1\"", sent.Body);
        Assert.Contains("\"an2\"", sent.Body);
    }

    // ---- canvas/notifications ----

    [Fact]
    public async Task NotificationsAsync_parses_the_items()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/notifications", "notifications");
        var n = await Client(fake).NotificationsAsync(stop: TestContext.Current.CancellationToken);

        Assert.Equal(3, n!.Items.Count);
        Assert.Contains(n.Items, x => x.Kind == "new_score" && x.Text!.Contains("18/20"));
    }

    [Fact]
    public async Task NotificationsAsync_sends_after_as_a_query()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/notifications", "notifications");
        await Client(fake).NotificationsAsync(new DateTimeOffset(2025, 9, 24, 0, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        Assert.StartsWith("?after=2025-09-24", Assert.Single(fake.Requests).Query);
    }

    [Fact]
    public async Task MarkNotificationsSeenAsync_posts_up_to()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Post, "/api/v2/canvas/notifications/seen", "{}");
        await Client(fake).MarkNotificationsSeenAsync("n3", TestContext.Current.CancellationToken);

        Assert.Contains("\"up_to\":\"n3\"", Assert.Single(fake.Requests).Body);
    }

    // ---- files/raw, files ----

    [Fact]
    public async Task TextAsync_reads_the_text_field()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/files", """{"text": "Office hours: MW 2-3pm."}""");
        Assert.Equal("Office hours: MW 2-3pm.", await Client(fake).TextAsync("CS 101", "notes.md", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TextAsync_is_null_on_a_404()
    {
        var fake = new FakeLibrary().Status(HttpMethod.Get, "/api/v2/files", HttpStatusCode.NotFound);
        Assert.Null(await Client(fake).TextAsync("CS 101", "missing.md", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadAsync_writes_the_bytes_under_a_temp_home()
    {
        using var home = new TempHome();
        byte[] bytes = "%PDF-fake"u8.ToArray();
        var fake = new FakeLibrary().Bytes(HttpMethod.Get, "/api/v2/files/raw", bytes);
        string dest = home["ps4-answers.pdf"];

        Assert.True(await Client(fake).DownloadAsync("CS 101", "ps4-answers.pdf", dest, TestContext.Current.CancellationToken));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(dest, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadAsync_is_false_on_a_404()
    {
        using var home = new TempHome();
        var fake = new FakeLibrary().Status(HttpMethod.Get, "/api/v2/files/raw", HttpStatusCode.NotFound);
        Assert.False(await Client(fake).DownloadAsync("CS 101", "gone.pdf", home["gone.pdf"], TestContext.Current.CancellationToken));
    }

    // ---- canvas (overview), canvas/extension, canvas/courses, canvas/scout ----

    [Fact]
    public async Task OverviewAsync_parses_the_old_overview_s_courses_and_changes()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas", "canvas");
        var o = await Client(fake).OverviewAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4201, o!.Courses["CS 101"]);
        Assert.Equal(0, o.Courses["HIST 210"]);
        Assert.Equal("COMP 101 · Intro to Programming", o.Available["4201"]);
        Assert.Single(o.CourseInfo);
        Assert.Single(o.LastChanges);
        Assert.Equal(60, o.PollMinutes);
    }

    [Fact]
    public async Task ExtensionAsync_parses_the_key_and_version()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/extension", "extension");
        var e = await Client(fake).ExtensionAsync(TestContext.Current.CancellationToken);

        Assert.Equal("test-key-abc123", e!.Key);
        Assert.Equal("1.3", e.Version);
    }

    [Fact]
    public async Task FindCoursesAsync_posts_with_no_body()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Post, "/api/v2/canvas/courses", "canvas");
        await Client(fake).FindCoursesAsync(TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(fake.Requests).Body);
    }

    [Fact]
    public async Task ScoutAsync_posts_the_class()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Post, "/api/v2/canvas/scout", "{}");
        await Client(fake).ScoutAsync("BIO 110", TestContext.Current.CancellationToken);

        Assert.Contains("\"class\":\"BIO 110\"", Assert.Single(fake.Requests).Body);
    }

    [Fact]
    public async Task SaveAsync_sends_only_the_fields_given()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Post, "/api/v2/canvas", "canvas");
        await Client(fake).SaveAsync(sync: true, stop: TestContext.Current.CancellationToken);

        string body = Assert.Single(fake.Requests).Body!;
        Assert.Contains("\"sync\":true", body);
        Assert.DoesNotContain("url", body);
        Assert.DoesNotContain("poll_minutes", body);
    }

    [Fact]
    public async Task SaveAsync_throws_with_the_library_s_own_detail_on_a_bad_address()
    {
        var fake = new FakeLibrary().Status(HttpMethod.Post, "/api/v2/canvas", HttpStatusCode.BadRequest, """{"detail": "That isn't a web address."}""");
        var e = await Assert.ThrowsAsync<CanvasLibraryException>(() => Client(fake).SaveAsync(url: "not a url", stop: TestContext.Current.CancellationToken));

        Assert.Equal("That isn't a web address.", e.Message);
        Assert.Equal(400, e.Status);
    }
}
