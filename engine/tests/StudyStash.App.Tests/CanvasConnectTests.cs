using System.Net;
using System.Text.Json;
using StudyStash.App.Services;
using StudyStash.App.ViewModels;
using StudyStash.Core;

namespace StudyStash.App.Tests;

public class CanvasConnectTests
{
    static CanvasConnectModel Model(FakeLibrary handler, string? home = null, List<(string What, string Arg)>? log = null)
    {
        var context = CanvasFixtures.Context(handler, home, log);
        return new CanvasConnectModel(context, new CanvasWatch(context));
    }

    [Theory]
    [InlineData("state-not-set-up", 1)]
    [InlineData("state-no-extension", 2)]
    [InlineData("state-signed-out", 3)]
    public async Task StartAsync_opens_at_the_step_the_state_still_needs(string fixture, int step)
    {
        var handler = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/extension", "extension");
        var m = Model(handler);
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>(fixture), [], TestContext.Current.CancellationToken);
        Assert.Equal(step, m.Current);
    }

    [Fact]
    public async Task With_no_class_linked_it_finds_courses_itself_and_lands_on_match()
    {
        var classes = new List<CanvasApi.ClassRow> { new() { Class = "CS 101", Linked = false, Suggested = "4201" } };
        var handler = new FakeLibrary()
            .Json(HttpMethod.Post, "/api/v2/canvas/courses", "canvas")
            .Json(HttpMethod.Get, "/api/v2/canvas/classes", JsonSerializer.Serialize(classes, CanvasApi.Json));
        var m = Model(handler);

        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-connected"), classes, TestContext.Current.CancellationToken);

        Assert.Equal(4, m.Current);
        Assert.Equal("Found 4 courses.", m.CoursesSay);
        var row = Assert.Single(m.Courses);
        Assert.Equal("4201", row.Selected?.Id); // the suggested course, preselected
    }

    [Fact]
    public async Task With_every_class_already_linked_it_goes_straight_to_sync_and_shows_the_last_one()
    {
        var classes = CanvasFixtures.Load<List<CanvasApi.ClassRow>>("classes"); // three of four linked
        var m = Model(new FakeLibrary());

        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-connected"), classes, TestContext.Current.CancellationToken);

        Assert.Equal(5, m.Current);
        Assert.Equal("Canvas synced 10:24.", m.SyncedLine);
    }

    [Fact]
    public async Task Continue_saves_the_school_and_moves_to_the_extension()
    {
        var log = new List<(string What, string Arg)>();
        var handler = new FakeLibrary()
            .Json(HttpMethod.Post, "/api/v2/canvas", "canvas")
            .Json(HttpMethod.Get, "/api/v2/canvas/state", "state-no-extension")
            .Json(HttpMethod.Get, "/api/v2/canvas/classes", "[]")
            .Json(HttpMethod.Get, "/api/v2/canvas/extension", "extension");
        var m = Model(handler, log: log);
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-not-set-up"), [], TestContext.Current.CancellationToken);

        m.SchoolField = "school.instructure.com";
        await m.ContinueCommand.ExecuteAsync(null);

        Assert.Equal(2, m.Current);
        var sent = Assert.Single(handler.Requests, r => r.Method == "POST" && r.Path == "/api/v2/canvas");
        Assert.Equal("{\"url\":\"school.instructure.com\"}", sent.Body);
        Assert.Contains(log, l => l.What == "OpenChromeExtensions");
    }

    [Fact]
    public async Task Continue_shows_the_librarys_detail_on_a_bad_address()
    {
        var handler = new FakeLibrary().Status(HttpMethod.Post, "/api/v2/canvas", HttpStatusCode.BadRequest, "{\"detail\":\"That isn't a web address.\"}");
        var m = Model(handler);
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-not-set-up"), [], TestContext.Current.CancellationToken);

        m.SchoolField = "not a web address";
        await m.ContinueCommand.ExecuteAsync(null);

        Assert.Equal("That isn't a web address.", m.SchoolError);
        Assert.Equal(1, m.Current); // stays put — nothing to move on to yet
    }

    [Fact]
    public async Task Entering_the_extension_step_prepares_it_and_opens_chromes_extensions_page()
    {
        var log = new List<(string What, string Arg)>();
        var handler = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/extension", "extension");
        var m = Model(handler, home: "the-home", log: log);

        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-no-extension"), [], TestContext.Current.CancellationToken);

        Assert.True(m.WaitingForExtension);
        Assert.Equal(Path.Combine("the-home", "chrome-extension"), m.ExtensionFolder);
        Assert.Contains(log, l => l.What == "PrepareExtension" && l.Arg == "test-key-abc123 https://school.instructure.com");
        Assert.Contains(log, l => l.What == "OpenChromeExtensions");
    }

    [Fact]
    public async Task Show_folder_reveals_the_folder_the_extension_step_prepared()
    {
        var log = new List<(string What, string Arg)>();
        var handler = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/extension", "extension");
        var m = Model(handler, home: "the-home", log: log);
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-no-extension"), [], TestContext.Current.CancellationToken);
        log.Clear();

        m.ShowFolderCommand.Execute(null);

        Assert.Contains(log, l => l.What == "RevealFolder" && l.Arg == Path.Combine("the-home", "chrome-extension"));
    }

    [Fact]
    public async Task The_extension_turning_up_moves_the_flow_past_waiting()
    {
        var handler = new FakeLibrary()
            .Json(HttpMethod.Get, "/api/v2/canvas/extension", "extension")
            .Json(HttpMethod.Get, "/api/v2/canvas/state", "state-connected")
            .Json(HttpMethod.Get, "/api/v2/canvas/classes", "classes");
        var context = CanvasFixtures.Context(handler);
        var watch = new CanvasWatch(context);
        var m = new CanvasConnectModel(context, watch);
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-no-extension"), [], TestContext.Current.CancellationToken);
        Assert.Equal(2, m.Current);

        await watch.RefreshAsync(TestContext.Current.CancellationToken); // the library now says "connected"
        await Task.Delay(20, TestContext.Current.CancellationToken); // Changed fires the advance without awaiting it

        Assert.True(m.Current >= 3, "should have moved past the extension step once it showed up");
    }

    [Fact]
    public async Task Signed_out_shows_the_sign_in_line_and_opens_canvas_in_chrome()
    {
        var log = new List<(string What, string Arg)>();
        var m = Model(new FakeLibrary(), log: log);

        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-signed-out"), [], TestContext.Current.CancellationToken);
        Assert.True(m.SignedOut);

        m.OpenCanvasCommand.Execute(null);
        Assert.Contains(log, l => l.What == "OpenInChrome" && l.Arg == "https://school.instructure.com");
    }

    [Fact]
    public async Task Signing_in_retries_finding_courses_on_its_own()
    {
        var classes = new List<CanvasApi.ClassRow> { new() { Class = "CS 101", Linked = false, Suggested = "4201" } };
        var handler = new FakeLibrary()
            .Json(HttpMethod.Get, "/api/v2/canvas/state", "state-connected")
            .Json(HttpMethod.Post, "/api/v2/canvas/courses", "canvas")
            .Json(HttpMethod.Get, "/api/v2/canvas/classes", JsonSerializer.Serialize(classes, CanvasApi.Json));
        var context = CanvasFixtures.Context(handler);
        var watch = new CanvasWatch(context);
        var m = new CanvasConnectModel(context, watch);
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-signed-out"), classes, TestContext.Current.CancellationToken);
        Assert.Equal(3, m.Current);
        Assert.True(m.SignedOut);

        await watch.RefreshAsync(TestContext.Current.CancellationToken); // now "connected" — signed in
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.False(m.SignedOut);
        Assert.Equal(4, m.Current);
    }

    [Fact]
    public async Task Find_courses_builds_rows_with_the_suggested_course_preselected()
    {
        var classes = new List<CanvasApi.ClassRow>
        {
            new() { Class = "CS 101", Linked = false, Suggested = "4201" },
            new() { Class = "HIST 210", Linked = false, Suggested = null },
        };
        var handler = new FakeLibrary()
            .Json(HttpMethod.Post, "/api/v2/canvas/courses", "canvas")
            .Json(HttpMethod.Get, "/api/v2/canvas/classes", JsonSerializer.Serialize(classes, CanvasApi.Json));
        var m = Model(handler);

        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-connected"), classes, TestContext.Current.CancellationToken);

        Assert.Equal(4, m.Current);
        Assert.Equal(2, m.Courses.Count);
        Assert.Equal("4201", m.Courses[0].Selected?.Id);
        Assert.Equal("Choose a course…", m.Courses[1].Selected?.Label);
        Assert.Contains(m.Courses[0].Choices, c => c.Id == "4204"); // every found course offered, not just the suggested one
    }

    [Fact]
    public async Task Link_these_posts_every_rows_choice_including_the_ones_left_unmatched()
    {
        var classes = new List<CanvasApi.ClassRow>
        {
            new() { Class = "CS 101", Linked = false, Suggested = "4201" },
            new() { Class = "HIST 210", Linked = false, Suggested = null },
        };
        var handler = new FakeLibrary()
            .Json(HttpMethod.Post, "/api/v2/canvas/courses", "canvas")
            .Json(HttpMethod.Get, "/api/v2/canvas/classes", JsonSerializer.Serialize(classes, CanvasApi.Json))
            .Json(HttpMethod.Post, "/api/v2/canvas", "canvas");
        var m = Model(handler);
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-connected"), classes, TestContext.Current.CancellationToken);

        await m.LinkTheseCommand.ExecuteAsync(null);

        Assert.Equal(5, m.Current);
        var sent = handler.Requests.Where(r => r.Method == "POST" && r.Path == "/api/v2/canvas").ToList();
        var courses = Assert.Single(sent, r => r.Body!.Contains("courses"));
        Assert.Equal("{\"courses\":{\"CS 101\":4201,\"HIST 210\":0}}", courses.Body);
    }

    [Fact]
    public async Task Sync_now_posts_sync_true()
    {
        var handler = new FakeLibrary()
            .Json(HttpMethod.Post, "/api/v2/canvas", "canvas");
        var classes = CanvasFixtures.Load<List<CanvasApi.ClassRow>>("classes");
        var m = Model(handler);
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-connected"), classes, TestContext.Current.CancellationToken);
        Assert.Equal(5, m.Current);

        await m.SyncNowCommand.ExecuteAsync(null);

        Assert.True(m.Syncing);
        var sent = Assert.Single(handler.Requests, r => r.Method == "POST" && r.Path == "/api/v2/canvas");
        Assert.Equal("{\"sync\":true}", sent.Body);
    }

    [Fact]
    public async Task Change_reopens_the_school_step_from_anywhere()
    {
        var handler = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/extension", "extension");
        var m = Model(handler);
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-no-extension"), [], TestContext.Current.CancellationToken);
        Assert.Equal(2, m.Current);

        m.ChangeSchoolCommand.Execute(null);

        Assert.Equal(1, m.Current);
        Assert.Equal("school.instructure.com", m.SchoolField);
    }

    [Fact]
    public async Task CanFinish_is_false_while_any_step_is_busy()
    {
        var m = Model(new FakeLibrary());
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-not-set-up"), [], TestContext.Current.CancellationToken);
        Assert.True(m.CanFinish);

        m.Syncing = true;
        Assert.False(m.CanFinish);
        m.Syncing = false;
        Assert.True(m.CanFinish);
    }

    [Fact]
    public async Task Skip_back_and_finish_call_the_host_back()
    {
        var m = Model(new FakeLibrary());
        await m.StartAsync(CanvasFixtures.Load<CanvasApi.State>("state-not-set-up"), [], TestContext.Current.CancellationToken);
        bool skipped = false, back = false, finished = false;
        m.OnSkip = () => skipped = true;
        m.OnBack = () => back = true;
        m.OnFinish = () => finished = true;

        m.SkipCommand.Execute(null);
        m.BackCommand.Execute(null);
        m.FinishCommand.Execute(null);

        Assert.True(skipped);
        Assert.True(back);
        Assert.True(finished);
    }

    // ---- Chrome.cs: never launches anything real in a test ----

    [Fact]
    public void Chrome_reports_when_nothing_could_be_started()
    {
        Assert.Equal(Chrome.NotInstalled, Chrome.Open(null, (_, _, _) => null));
    }

    [Fact]
    public void Chrome_asks_for_the_url_it_was_given()
    {
        string? exe = null;
        IReadOnlyList<string>? args = null;
        string? result = Chrome.Open("https://school.instructure.com", (e, a, _) =>
        {
            exe = e;
            args = a;
            return new ProcResult(0, "");
        });

        Assert.Null(result);
        Assert.NotNull(exe);
        Assert.Contains("https://school.instructure.com", args!);
    }

    [Fact]
    public void Chrome_extensions_opens_the_extensions_page()
    {
        IReadOnlyList<string>? args = null;
        Chrome.OpenExtensions((_, a, _) =>
        {
            args = a;
            return new ProcResult(0, "");
        });

        Assert.Contains("chrome://extensions", args!);
    }
}
