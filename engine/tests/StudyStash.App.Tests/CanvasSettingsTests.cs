using System.Net;
using StudyStash.App.Services;
using StudyStash.App.ViewModels;

namespace StudyStash.App.Tests;

public class CanvasSettingsTests
{
    static FakeLibrary Connected() => new FakeLibrary()
        .Json(HttpMethod.Get, "/api/v2/canvas/state", "state-connected")
        .Json(HttpMethod.Get, "/api/v2/canvas", "canvas")
        .Json(HttpMethod.Get, "/api/v2/canvas/classes", "classes")
        .Json(HttpMethod.Post, "/api/v2/canvas/courses", "canvas")
        .Json(HttpMethod.Post, "/api/v2/canvas/scout", "canvas")
        .Json(HttpMethod.Post, "/api/v2/canvas", "canvas");

    [Fact]
    public async Task LoadAsync_shows_connection_and_courses_as_mac_06_does()
    {
        var model = new CanvasSettingsModel(CanvasFixtures.Context(Connected()));
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(model.Say);
        Assert.Equal(CanvasStateKind.Connected, model.Status.Kind);
        Assert.Equal("school.instructure.com", model.School);
        Assert.Equal("Checked in 2 min ago · version 1.4", model.ExtensionLine);
        Assert.Equal("Every hour", model.PollLabel);
        Assert.Equal("Last sync · 10:24", model.LastSyncHeader);
        Assert.True(model.HasChanges);
        Assert.Equal(3, model.Changes.Count);
        Assert.Equal("New: CS 101 · Lab 3 · due Tue 11:59 PM", model.Changes[0].Text);
        Assert.Equal("add_circle", model.Changes[0].Glyph);
        Assert.Equal("Moved: CALC II · Quiz 3 practice · now Fri 9:00 AM", model.Changes[1].Text);
        Assert.Equal("Graded: CS 101 · Problem set 4 · 18/20", model.Changes[2].Text);

        Assert.Equal(4, model.Courses.Count);
        var cs101 = model.Courses[0];
        Assert.Equal("CS 101", cs101.Class);
        Assert.Equal("Scout done · 14 files saved · 20 Sep", cs101.ScoutLine);
        Assert.True(cs101.ScoutOk);
        Assert.False(cs101.ShowTryAgain);
        Assert.Equal("COMP 101 · Intro to Programming", cs101.Selected?.Label);

        var bio110 = model.Courses[1];
        Assert.True(bio110.ScoutSpinning);
        Assert.Equal("Scout exploring…", bio110.ScoutLine);

        var calc2 = model.Courses[2];
        Assert.True(calc2.ScoutWarn);
        Assert.True(calc2.ShowTryAgain);
        Assert.Equal("Scout didn’t finish", calc2.ScoutLine);

        var hist210 = model.Courses[3];
        Assert.False(hist210.ScoutOk);
        Assert.False(hist210.ScoutWarn);
        Assert.Equal(CanvasWords.NotMatched, hist210.ScoutLine);
        Assert.Equal("Choose a course…", hist210.Selected?.Label);
        Assert.Null(hist210.Selected?.Id);
        Assert.Contains(hist210.Choices, c => c.Id == "4204");
    }

    [Fact]
    public async Task SaveSchool_posts_the_new_address_and_reloads()
    {
        var fake = Connected();
        var model = new CanvasSettingsModel(CanvasFixtures.Context(fake));
        await model.LoadAsync(TestContext.Current.CancellationToken);

        model.ChangeSchoolCommand.Execute(null);
        Assert.True(model.EditingSchool);
        model.SchoolField = "other.instructure.com";
        await model.SaveSchoolCommand.ExecuteAsync(null);

        Assert.False(model.EditingSchool);
        var sent = Assert.Single(fake.Requests, r => r.Method == "POST" && r.Path == "/api/v2/canvas");
        Assert.Contains("\"url\":\"other.instructure.com\"", sent.Body);
    }

    [Fact]
    public async Task SaveSchool_shows_the_library_s_400_detail_and_keeps_editing()
    {
        var fake = new FakeLibrary().Status(HttpMethod.Post, "/api/v2/canvas", HttpStatusCode.BadRequest, "{\"detail\":\"That isn't a web address.\"}");
        var model = new CanvasSettingsModel(CanvasFixtures.Context(fake));

        model.ChangeSchoolCommand.Execute(null);
        model.SchoolField = "not a url";
        await model.SaveSchoolCommand.ExecuteAsync(null);

        Assert.Equal("That isn't a web address.", model.SchoolError);
        Assert.True(model.EditingSchool);
    }

    [Fact]
    public async Task PickPoll_posts_the_minutes_and_reloads()
    {
        var fake = Connected();
        var model = new CanvasSettingsModel(CanvasFixtures.Context(fake));
        await model.LoadAsync(TestContext.Current.CancellationToken);

        await model.PickPollCommand.ExecuteAsync(180);

        var sent = Assert.Single(fake.Requests, r => r.Method == "POST" && r.Path == "/api/v2/canvas");
        Assert.Contains("\"poll_minutes\":180", sent.Body);
    }

    [Fact]
    public async Task Picking_a_course_posts_the_class_and_its_new_course_id()
    {
        var fake = Connected();
        var model = new CanvasSettingsModel(CanvasFixtures.Context(fake));
        await model.LoadAsync(TestContext.Current.CancellationToken);

        var hist210 = model.Courses[3];
        var choice = Assert.Single(hist210.Choices, c => c.Id == "4204");
        hist210.PickCommand.Execute(choice);
        await Task.Delay(20, TestContext.Current.CancellationToken); // Pick fires the save-and-reload without awaiting it

        var sent = Assert.Single(fake.Requests, r => r.Method == "POST" && r.Path == "/api/v2/canvas");
        Assert.Equal("{\"courses\":{\"HIST 210\":4204}}", sent.Body);
    }

    [Fact]
    public async Task TryAgain_scouts_the_class_again()
    {
        var fake = Connected();
        var model = new CanvasSettingsModel(CanvasFixtures.Context(fake));
        await model.LoadAsync(TestContext.Current.CancellationToken);

        model.Courses[2].TryAgainCommand.Execute(null); // CALC II, the one that failed
        await Task.Delay(20, TestContext.Current.CancellationToken);

        var sent = Assert.Single(fake.Requests, r => r.Method == "POST" && r.Path == "/api/v2/canvas/scout");
        Assert.Equal("{\"class\":\"CALC II\"}", sent.Body);
    }

    [Fact]
    public async Task FindCourses_posts_and_says_how_many_it_found()
    {
        var fake = Connected();
        var model = new CanvasSettingsModel(CanvasFixtures.Context(fake));
        await model.FindCoursesCommand.ExecuteAsync(null);

        Assert.Equal("Found 4 courses.", model.CoursesSay);
        Assert.Contains(fake.Requests, r => r.Method == "POST" && r.Path == "/api/v2/canvas/courses");
    }

    [Fact]
    public async Task FindCourses_shows_the_library_s_error()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Post, "/api/v2/canvas/courses", "{\"error\":\"Set your school first.\"}");
        var model = new CanvasSettingsModel(CanvasFixtures.Context(fake));

        await model.FindCoursesCommand.ExecuteAsync(null);

        Assert.Equal("Set your school first.", model.CoursesSay);
    }

    [Fact]
    public async Task An_older_library_says_so_instead_of_loading_rows()
    {
        var model = new CanvasSettingsModel(CanvasFixtures.Context(new FakeLibrary())); // nothing routed: every call 404s
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Your library runs an older Study Stash: update it to use Canvas.", model.Say);
        Assert.Empty(model.Courses);
    }

    [Fact]
    public async Task A_library_that_errors_out_says_it_didn_t_answer()
    {
        var fake = new FakeLibrary().Status(HttpMethod.Get, "/api/v2/canvas/state", HttpStatusCode.InternalServerError);
        var model = new CanvasSettingsModel(CanvasFixtures.Context(fake));
        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Your library didn't answer.", model.Say);
    }

    [Fact]
    public void No_library_at_all_says_it_didn_t_answer()
    {
        var model = new CanvasSettingsModel(CanvasFixtures.Context());
        _ = model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Your library didn't answer.", model.Say);
    }

    [Fact]
    public async Task The_watch_s_Changed_refreshes_the_header_s_status()
    {
        var fake = Connected();
        var watch = new CanvasWatch(CanvasFixtures.Context(fake));
        var model = new CanvasSettingsModel(CanvasFixtures.Context(fake), watch);

        await watch.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CanvasStateKind.Connected, model.Status.Kind);
    }
}
