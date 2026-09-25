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
        var old = new List<Assignment> { new("CS 101", 1, "Lab 1", "2026-09-30T23:59", 10, "open", null, "", ""), new("CS 101", 2, "Quiz", "", 5, "open", null, "", "") };
        var now = new List<Assignment> { new("CS 101", 1, "Lab 1", "2026-10-01T23:59", 10, "graded", 9, "", ""), new("CS 101", 3, "Lab 2", "", 10, "open", null, "", "") };
        var said = Assignments.Diff(old, now);
        Assert.Contains(said, s => s.StartsWith("Due date moved: CS 101 · Lab 1", StringComparison.Ordinal));
        Assert.Contains("Now graded: CS 101 · Lab 1 (9/10)", said);
        Assert.Contains(said, s => s.StartsWith("New: CS 101 · Lab 2", StringComparison.Ordinal));
        Assert.Contains("Removed: CS 101 · Quiz", said);
    }

    [Fact]
    public void A_canvas_address_is_cleaned_up_from_whatever_was_pasted()
    {
        Assert.Equal("https://school.instructure.com", CanvasSettings.CleanUrl("school.instructure.com/courses/12/"));
        Assert.Equal("https://school.instructure.com", CanvasSettings.CleanUrl(" https://school.instructure.com "));
        Assert.Null(CanvasSettings.CleanUrl("not a url"));
    }

    static CanvasResult Ok(CanvasJob j, string text, string link = "") => new(j.Id, 200, link, text, "", "", j.Url);

    [Fact]
    public async Task A_sync_mirrors_specs_feedback_modules_pages_files_and_announcements()
    {
        using var dir = new TempDir();
        string ClassDir(string c) { string d = Path.Combine(dir["pool"], c); Directory.CreateDirectory(d); return d; }
        CanvasSettings.Update(dir.Path, s => { s.Url = "https://canvas.test"; s.Courses["CS 101"] = 42; });
        var sync = new CanvasSync(dir.Path, ClassDir, _ => { });
        var work = sync.Work(force: true);
        Assert.Equal(4, work.Jobs.Count);
        Assert.True(work.Hot);
        var answers = new List<CanvasResult>();
        foreach (var j in work.Jobs)
        {
            if (j.Url.Contains("/assignments?", StringComparison.Ordinal))
                answers.Add(Ok(j, """[{"id":7,"name":"Lab 1: Recursion","due_at":"2026-09-30T06:59:00Z","points_possible":10,"published":true,"html_url":"https://canvas.test/courses/42/assignments/7","description":"<p>Write <b>fib</b>.</p><script>x()</script>","submission_types":["online_upload"],"submission":{}}]""",
                    "<https://canvas.test/api/v1/courses/42/assignments?page=2>; rel=\"next\""));
            else if (j.Url.Contains("/students/submissions", StringComparison.Ordinal))
                answers.Add(Ok(j, """[{"assignment":{"id":7,"name":"Lab 1: Recursion","points_possible":10},"score":9,"submitted_at":"2026-09-29T00:00:00Z","workflow_state":"graded","attempt":1,"attachments":[{"id":5,"display_name":"fib.py","url":"https://canvas.test/files/5/download","content-type":"text/x-python","size":20,"updated_at":"u1"}],"submission_comments":[{"author_name":"Dr. T","created_at":"2026-09-29T01:00:00Z","comment":"Nice"}]}]"""));
            else if (j.Url.Contains("/modules", StringComparison.Ordinal))
                answers.Add(Ok(j, """[{"position":1,"name":"Week 1","items":[{"type":"Page","title":"Syllabus","url":"https://canvas.test/api/v1/courses/42/pages/syllabus"},{"type":"ExternalUrl","title":"Book","external_url":"https://book.test"}]}]"""));
            else
                answers.Add(Ok(j, """[{"title":"Welcome","posted_at":"2026-08-25T00:00:00Z","author":{"display_name":"Dr. T"},"message":"<p>Hi all</p>"}]"""));
        }
        sync.Results(answers);
        string root = Path.Combine(dir["pool"], "CS 101", "Canvas");
        string spec = File.ReadAllText(Path.Combine(root, "assignments", "Lab 1- Recursion", "spec.md"));
        Assert.Contains("canvas_id: 7\n", spec);
        Assert.Contains("Write **fib**.", spec);
        Assert.DoesNotContain("x()", spec);
        Assert.Contains("- **Score:** 9/10", File.ReadAllText(Path.Combine(root, "assignments", "Lab 1- Recursion", "feedback.md")));
        Assert.Contains("[Syllabus](modules/01%20Week%201/Syllabus.md)", File.ReadAllText(Path.Combine(root, "modules.md")));
        Assert.Contains("Hi all", File.ReadAllText(Path.Combine(root, "announcements.md")));

        // The next page of assignments, the page and the submitted file come next.
        var more = sync.Work(force: false).Jobs;
        Assert.Equal(3, more.Count);
        sync.Results(more.Select(j => j.Kind == "bytes" ? new CanvasResult(j.Id, 200, "", "", Convert.ToBase64String("def fib(): ..."u8.ToArray()), "", j.Url)
            : j.Url.Contains("pages", StringComparison.Ordinal) ? Ok(j, """{"title":"Syllabus","body":"<h2>Grading</h2><ul><li>Labs 50%</li></ul>","updated_at":"2026-08-20T00:00:00Z"}""")
            : Ok(j, "[]")).ToList());
        Assert.Equal("def fib(): ...", File.ReadAllText(Path.Combine(root, "assignments", "Lab 1- Recursion", "submission", "fib.py")));
        Assert.Contains("## Grading", File.ReadAllText(Path.Combine(root, "modules", "01 Week 1", "Syllabus.md")));

        Assert.False(sync.Crawl.Active);
        var saved = Assignments.Load(dir.Path);
        Assert.Equal("Lab 1: Recursion", Assert.Single(saved).Name);
        Assert.Empty(sync.Work(force: false).Jobs); // synced just now: nothing until the next hour
        await Task.CompletedTask;
    }

    [Fact]
    public void Canvas_sending_the_extension_to_sign_in_stops_the_sync_and_says_so()
    {
        using var dir = new TempDir();
        CanvasSettings.Update(dir.Path, s => { s.Url = "https://canvas.test"; s.Courses["CS 101"] = 42; });
        var sync = new CanvasSync(dir.Path, c => dir[c], _ => { });
        var jobs = sync.Work(force: true).Jobs;
        sync.Results([new CanvasResult(jobs[0].Id, 401, "", "", "", "", "")]);
        sync.Work(force: false);
        var s = CanvasSettings.Load(dir.Path);
        Assert.True(s.NeedsLogin);
        Assert.Contains("sign in", s.Error, StringComparison.OrdinalIgnoreCase);
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
