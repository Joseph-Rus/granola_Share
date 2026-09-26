using System.Net.Http.Headers;
using StudyStash.Core.Canvas;
using StudyStash.Library;

namespace StudyStash.Core.Tests;

/// <summary>Claude's Canvas tools: due_assignments (grouped and labelled), one assignment's whole story (found by id
/// or by words of its name), a class's modules/files/announcements, and the deny-list that keeps AI reads off other
/// people's Canvas data — each exercised through LocalLibrary (in the library itself) and RemoteLibrary (over its
/// API, the way a laptop's <c>Study Stash mcp</c> reads a library).</summary>
public class CanvasToolsTests
{
    const string Password = "maple otter";

    static async Task<(TempDir Dir, CanvasSync Sync)> SyncedAsync()
    {
        var dir = new TempDir();
        var now = FakeCanvas.DesignNow;
        var sync = FakeCanvas.Library(dir, () => now, ("CS 101", 4201L));
        Assert.True(FakeCanvas.Cs101().Run(sync));
        return (dir, sync);
    }

    static LocalLibrary Local(TempDir dir, CanvasSync sync)
    {
        var cfg = new Config(dir.Path, dir["pool"]);
        var store = new Store(cfg.DbPath, cfg.PoolDir);
        return new LocalLibrary(new LibraryReader(cfg, store), sync, dir.Path);
    }

    static async Task<(TestSite Site, RemoteLibrary Lib)> RemoteAsync(TempDir dir, CanvasSync sync)
    {
        var cfg = new Config(dir.Path, dir["pool"]) { PoolPassword = Password, OllamaEnabled = false, Classes = [new ClassDef("CS 101", ["cs101"])] };
        var store = new Store(cfg.DbPath, cfg.PoolDir);
        var options = new LibraryWebOptions
        {
            Canvas = sync, ListModels = _ => Task.FromResult<List<(string, double)>?>(null), Tailscale = () => new TailscaleInfo(),
            Latest = _ => Task.FromResult<Release?>(null), RamGb = () => 16, HostName = () => "library-pc", Nonce = () => "NONCE",
        };
        var site = await TestSite.StartAsync(b => LibraryWeb.Build(b, cfg, store, new Pipeline(cfg, store, log: _ => { }), options));
        return (site, new RemoteLibrary("http://localhost", Password, site.Client));
    }

    [Fact]
    public void The_canvas_tools_are_named_and_read_only_except_the_download()
    {
        using var dir = new TempDir();
        var cfg = new Config(dir.Path, dir["pool"]);
        var store = new Store(cfg.DbPath, cfg.PoolDir);
        var lib = new LocalLibrary(new LibraryReader(cfg, store), new CanvasSync(dir.Path, c => dir[c]), dir.Path);
        var canvasTools = ClaudeTools.Tools(lib)
            .Where(t => t.ProtocolTool.Name.StartsWith("canvas_", StringComparison.Ordinal) || t.ProtocolTool.Name is "due_assignments"
                or "get_assignment" or "class_modules" or "class_files" or "class_announcements")
            .ToList();
        Assert.Equal(
            ["canvas_api", "canvas_courses", "canvas_download", "canvas_page", "class_announcements", "class_files", "class_modules", "due_assignments", "get_assignment"],
            canvasTools.Select(t => t.ProtocolTool.Name).Order());
        foreach (var t in canvasTools)
            Assert.Equal(t.ProtocolTool.Name != "canvas_download", t.ProtocolTool.Annotations?.ReadOnlyHint ?? false);
    }

    [Fact]
    public async Task Get_assignment_finds_problem_set_4_by_words_of_its_name_through_local_and_remote()
    {
        var (dir, sync) = await SyncedAsync();
        using var _1 = dir;
        string local = await ClaudeTools.GetAssignmentAsync(Local(dir, sync), "CS 101", "problem set 4");
        Assert.Contains("Stack traces: 8 / 10 · The frame for n = 1 is missing in 3b.", local);
        Assert.Contains("Points: 20", local);
        Assert.Contains("Status: Graded · 18/20", local);
        Assert.Contains("Base cases: 5 / 5", local);

        var (site, remote) = await RemoteAsync(dir, sync);
        await using var _2 = site;
        string remoteText = await ClaudeTools.GetAssignmentAsync(remote, "CS 101", "problem set 4");
        Assert.Contains("Stack traces: 8 / 10 · The frame for n = 1 is missing in 3b.", remoteText);
        Assert.Contains("Status: Graded · 18/20", remoteText);
    }

    [Fact]
    public async Task Get_assignment_by_id_reads_the_same_assignment()
    {
        var (dir, sync) = await SyncedAsync();
        using var _1 = dir;
        string text = await ClaudeTools.GetAssignmentAsync(Local(dir, sync), "CS 101", "9002");
        Assert.Contains("# Problem set 4 — CS 101", text);
    }

    [Fact]
    public async Task Several_matching_assignments_are_listed_instead_of_guessed()
    {
        var (dir, sync) = await SyncedAsync();
        using var _1 = dir;
        string text = await ClaudeTools.GetAssignmentAsync(Local(dir, sync), "CS 101", "problem set");
        Assert.Contains("Several assignments match:", text);
        Assert.Contains("Problem set 3", text);
        Assert.Contains("Problem set 4", text);
    }

    [Fact]
    public async Task An_unknown_assignment_name_says_so()
    {
        var (dir, sync) = await SyncedAsync();
        using var _1 = dir;
        string text = await ClaudeTools.GetAssignmentAsync(Local(dir, sync), "CS 101", "final exam");
        Assert.Contains("Couldn't:", text);
        Assert.Contains("No assignment", text);
    }

    [Fact]
    public async Task Due_assignments_are_grouped_and_labelled()
    {
        var (dir, sync) = await SyncedAsync();
        using var _1 = dir;
        string text = await ClaudeTools.DueAssignmentsAsync(Local(dir, sync), "CS 101", 14);
        Assert.Contains("Due soon:", text);
        Assert.Contains("CS 101 · Lab 3: recursion traces · To do", text);
    }

    // Crawl.cs's own module and announcement listings (Modules/Announcements) render only the .md mirror today and
    // never fill CourseIndex.Modules/.Announcements (a gap in T5/T6, outside T8), so these two tests build the index
    // directly — exactly what a finished sync would leave — to test our formatting rather than that gap.
    [Fact]
    public async Task Class_modules_show_kind_and_where_a_link_was_saved_from()
    {
        using var dir = new TempDir();
        new CourseIndex
        {
            Class = "CS 101",
            Modules =
            [
                new ModuleInfo
                {
                    Id = 302, Name = "Week 4 · Recursion", Position = 4,
                    Items =
                    [
                        new ModuleItemInfo { Id = 3021, Type = "File", Kind = "file", Title = "recursion-slides.pdf", Format = "PDF" },
                        new ModuleItemInfo { Id = 3023, Type = "ExternalUrl", Kind = "link", Title = "Tracing worksheet", Source = "box", Saved = false, ExternalUrl = "https://app.box.com/s/abc123" },
                    ],
                },
            ],
        }.Save(dir.Path);
        string text = await ClaudeTools.ClassModulesAsync(Local(dir, new CanvasSync(dir.Path, c => dir[c])), "CS 101");
        Assert.Contains("Week 4 · Recursion", text);
        Assert.Contains("recursion-slides.pdf (PDF)", text);
        // Not yet saved by the scout: a Box link the mirror can't reach on its own isn't claimed as "saved from Box".
        Assert.Contains("Tracing worksheet (link)", text);
    }

    [Fact]
    public async Task Class_files_says_when_canvas_hides_the_files_area()
    {
        using var dir = new TempDir();
        new CourseIndex { Class = "CS 101", FilesHidden = true }.Save(dir.Path);
        string text = await ClaudeTools.ClassFilesAsync(Local(dir, new CanvasSync(dir.Path, c => dir[c])), "CS 101");
        Assert.Contains("Canvas hides this class's Files area", text);
    }

    [Fact]
    public async Task Class_announcements_are_newest_first_with_short_bodies()
    {
        using var dir = new TempDir();
        new CourseIndex
        {
            Class = "CS 101",
            Announcements =
            [
                new AnnouncementInfo { Id = 1, Title = "Welcome to COMP 101", PostedAt = "2025-08-25T15:00:00Z", Author = "Dr. Okafor", ReadOnCanvas = true, Body = "Welcome!" },
                new AnnouncementInfo { Id = 2, Title = "Lab 3 is up", PostedAt = "2025-09-23T16:05:00Z", Author = "Dr. Okafor", ReadOnCanvas = false, Body = "Lab 3 is now posted." },
            ],
        }.Save(dir.Path);
        string text = await ClaudeTools.ClassAnnouncementsAsync(Local(dir, new CanvasSync(dir.Path, c => dir[c])), "CS 101", 10);
        int labUp = text.IndexOf("Lab 3 is up", StringComparison.Ordinal), welcome = text.IndexOf("Welcome to COMP 101", StringComparison.Ordinal);
        Assert.True(labUp >= 0 && welcome >= 0 && labUp < welcome); // newest first
        Assert.Contains("Lab 3 is up — Dr. Okafor", text);
        Assert.Contains("(new)", text); // unread on Canvas, never opened in Study Stash
    }

    [Theory]
    [InlineData("/api/v1/courses/4201/users", true)]
    [InlineData("/api/v1/users/12345", true)]
    [InlineData("/api/v1/users/self", false)]
    [InlineData("/api/v1/users/self/todo", false)]
    [InlineData("/api/v1/courses/4201/enrollments", true)]
    [InlineData("/api/v1/courses/4201/assignments/9001/peer_reviews", true)]
    [InlineData("/api/v1/search/recipients", true)]
    [InlineData("/api/v1/conversations", true)]
    [InlineData("/api/v1/courses/4201/discussion_topics/501/entries", true)]
    [InlineData("/api/v1/courses/4201/discussion_topics/501/view", true)]
    [InlineData("/api/v1/courses/4201/discussion_topics/501/entry_list", true)]
    [InlineData("/api/v1/courses/4201/discussion_topics", false)]
    [InlineData("/api/v1/courses/4201/discussion_topics/501", false)]
    [InlineData("/api/v1/courses/4201/students", true)]
    [InlineData("/api/v1/courses/4201/students/submissions?student_ids[]=self", false)]
    [InlineData("/api/v1/groups/12/users", true)]
    [InlineData("/api/v1/courses/4201/assignments", false)]
    [InlineData("/api/v1/courses/4201/modules", false)]
    public void The_deny_list_refuses_other_peoples_data(string path, bool denied) =>
        Assert.Equal(denied, CanvasSync.DeniesOtherPeople(FakeCanvas.Base + path));

    [Fact]
    public async Task An_ai_read_of_the_roster_is_refused_before_canvas_is_ever_asked()
    {
        using var dir = new TempDir();
        var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        var canvas = new FakeCanvas();
        var reading = sync.FetchAsync("/api/v1/courses/4201/users", "json");
        sync.Results(sync.Work(force: false).Jobs.Select(canvas.Answer).ToList());
        var result = await reading;
        Assert.Equal("Study Stash doesn't read other people's Canvas data.", result["error"]!.GetValue<string>());
        Assert.DoesNotContain("/api/v1/courses/4201/users", canvas.Requested);
    }

    [Fact]
    public void The_scout_prompt_says_what_the_sync_now_saves_and_the_module_naming_contract()
    {
        string prompt = Scout.Prompt("CS 101", 4201, recipeExists: false);
        Assert.Contains("syllabus.md", prompt);
        Assert.Contains("pages/*.md", prompt);
        Assert.Contains("quizzes/*.md and discussions/*.md", prompt);
        Assert.Contains("module's own folder", prompt);
        Assert.Contains("named exactly after the item's title", prompt);
        Assert.Contains("Saved from Box", prompt);
        Assert.Contains("Never change the lecture notes, or any file the sync writes", prompt);
    }
}
