using System.Text.Json.Nodes;
using StudyStash.Core.Canvas;

namespace StudyStash.Core.Tests;

/// <summary>Every assignment's status, rubric, submission and feedback, as the class's index keeps them and as
/// spec.md and feedback.md say them; and what changed between syncs, in the design's words.</summary>
public class CanvasAssignmentTests
{
    const string AssignmentsPath = "/api/v1/courses/4201/assignments";
    const string SubmissionsPath = "/api/v1/courses/4201/students/submissions";

    static JsonObject A(string json) => JsonNode.Parse(json)!.AsObject();

    [Theory]
    [InlineData("""{"submission":{"excused":true}}""", "excused", "Excused", "")]
    [InlineData("""{"points_possible":10,"submission":{"workflow_state":"graded","score":9}}""", "graded", "Graded", "9/10")]
    [InlineData("""{"submission":{"missing":true}}""", "missing", "Missing", "")]
    [InlineData("""{"submission":{"submitted_at":"2025-09-20T00:00:00Z","late":true}}""", "late", "Submitted late", "")]
    [InlineData("""{"submission":{"workflow_state":"submitted","submitted_at":"2025-09-20T00:00:00Z"}}""", "submitted", "Submitted", "")]
    [InlineData("""{"submission":{"workflow_state":"pending_review","submitted_at":null}}""", "submitted", "Submitted", "")]
    [InlineData("""{"submission_types":["on_paper"]}""", "no submission", "Nothing to hand in", "")]
    [InlineData("""{"submission_types":["online_upload"],"due_at":"2025-09-20T00:00:00Z"}""", "past due", "Past due", "")]
    [InlineData("""{"submission_types":["online_upload"],"due_at":"2025-10-20T00:00:00Z"}""", "open", "To do", "")]
    // Pass/fail and letter grades: the grade is the mark, with or without a score.
    [InlineData("""{"grading_type":"pass_fail","points_possible":5,"submission":{"workflow_state":"graded","score":5,"grade":"complete"}}""", "graded", "Graded", "Complete")]
    [InlineData("""{"grading_type":"pass_fail","submission":{"workflow_state":"graded","score":null,"grade":"incomplete"}}""", "graded", "Graded", "Incomplete")]
    [InlineData("""{"grading_type":"letter_grade","points_possible":20,"submission":{"workflow_state":"graded","score":18.5,"grade":"A-"}}""", "graded", "Graded", "A-")]
    // Graded late work still says it was late (the design's "Submitted late · 17/20").
    [InlineData("""{"points_possible":20,"submission":{"workflow_state":"graded","score":17,"submitted_at":"2025-09-12T15:20:00Z","late":true}}""", "graded", "Submitted late", "17/20")]
    public void An_assignment_s_status_label_and_mark_read_like_the_design(string json, string status, string label, string mark)
    {
        var (info, _) = AssignmentInfo.From(A(json), "", HtmlContext.None);
        var row = Assignments.From("CS 101", info, FakeCanvas.DesignNow, FakeCanvas.Zone);
        Assert.Equal(status, row.Status);
        Assert.Equal(status, Assignments.StatusOf(A(json), FakeCanvas.DesignNow));
        Assert.Equal(label, Assignments.Label(row.Status, row.Late));
        Assert.Equal(mark, Assignments.ScoreText(row));
    }

    [Theory]
    [InlineData(0L, "0 bytes")]
    [InlineData(4096L, "4 KB")]
    [InlineData(2310L, "2 KB")]
    [InlineData(1258291L, "1.2 MB")]
    [InlineData(62914560L, "60 MB")]
    public void File_sizes_read_like_the_design(long bytes, string said) => Assert.Equal(said, CanvasMarkdown.Size(bytes));

    static (TempDir Dir, CanvasSync Sync, FakeCanvas Canvas) Synced()
    {
        var dir = new TempDir();
        var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        var canvas = FakeCanvas.Cs101();
        Assert.True(canvas.Run(sync));
        return (dir, sync, canvas);
    }

    [Fact]
    public void The_index_has_problem_set_4_and_lab_3_as_the_design_shows_them()
    {
        var (dir, _, _) = Synced();
        using var _dir = dir;
        var index = CourseIndex.Load(dir.Path, "CS 101")!;
        Assert.Equal((4201L, "CS 101"), (index.CourseId, index.Class));
        Assert.Equal("ok", index.Sections["assignments"]);
        Assert.Equal([9005L, 9003, 9004, 9002, 9001], index.Assignments.Select(a => a.Id));

        // mac-10: "Graded · 18 / 20 · Submitted Tue 16 Sep, 9:41 PM", rubric "8 / 10" with its comment, the two files.
        var ps4 = index.Assignments.Single(a => a.Id == 9002);
        Assert.Equal(("Canvas/assignments/Problem set 4", "2025-09-17T06:59:00Z", 20.0), (ps4.Folder, ps4.DueAt, ps4.Points));
        Assert.Equal("Five short problems on stack frames and scope. Show your working for 3a and 3b.", ps4.Instructions);
        var s = ps4.Submission!;
        Assert.Equal(("graded", 18.0, "18", "2025-09-17T04:41:00Z", "2025-09-22T18:00:00Z"), (s.State, s.Score!.Value, s.Grade, s.SubmittedAt, s.GradedAt));
        Assert.Equal("Tue 16 Sep 2025, 9:41 PM", CanvasMarkdown.When(s.SubmittedAt, FakeCanvas.Zone));
        Assert.Equal(["ps4-answers.pdf 1.2 MB Canvas/assignments/Problem set 4/submission/ps4-answers.pdf", "ps4.py 4 KB Canvas/assignments/Problem set 4/submission/ps4.py"],
            s.Files.Select(f => $"{f.Name} {CanvasMarkdown.Size(f.Size)} {f.Local}"));
        Assert.Equal("https://canvas.test/files/8801/download", s.Files[0].Url); // never the one-time verifier
        var marks = s.Marks["_2002"];
        Assert.Equal((8.0, "_2002b", "The frame for n = 1 is missing in 3b."), (marks.Points!.Value, marks.RatingId, marks.Comment));
        Assert.Equal(10, ps4.Rubric.Single(r => r.Id == "_2002").Points);
        var comment = Assert.Single(s.Comments);
        Assert.Equal(("Dr. Okafor", "Good work overall. Watch the last frame in 3b, it’s the one people drop.", false), (comment.Author, comment.Text, comment.Mine));
        Assert.Equal(1, Assert.Single(s.Attempts).Attempt);

        // mac-09: Lab 3's rubric "10 pts / 6 pts / 4 pts", "Nothing handed in yet".
        var lab3 = index.Assignments.Single(a => a.Id == 9001);
        Assert.Equal(["Correct traces 10", "Stack diagram at the deepest point 6", "Return values labelled 4"], lab3.Rubric.Select(r => $"{r.Description} {r.Points}"));
        Assert.Equal("unsubmitted", lab3.Submission!.State);
        Assert.Null(lab3.Submission.SubmittedAt);
        Assert.Equal("To do", Assignments.Label(Assignments.StatusOf(lab3, FakeCanvas.DesignNow)));
        Assert.Equal(("quiz", 7001L, 2), (index.Assignments[0].Kind, index.Assignments[0].QuizId!.Value, index.Assignments[0].AllowedAttempts!.Value));

        // The flat list for the Due page carries the new facts.
        var rows = Assignments.Load(dir.Path);
        var lab2 = rows.Single(a => a.Id == 9004);
        Assert.Equal(("graded", true, "17/20", "Submitted late", 2), (lab2.Status, lab2.Late, Assignments.ScoreText(lab2), Assignments.Label(lab2.Status, lab2.Late), lab2.Comments!.Value));
        Assert.Equal(("2025-09-22T18:00:00Z", "points", "2025-09-17T06:59:00Z", "2025-09-16T23:59"), (rows.Single(a => a.Id == 9002).GradedAt, rows.Single(a => a.Id == 9002).GradingType,
            rows.Single(a => a.Id == 9002).DueAt, rows.Single(a => a.Id == 9002).Due));
        Assert.True(rows.Single(a => a.Id == 9005).Excused);
    }

    [Fact]
    public void Problem_set_4_s_feedback_has_the_design_s_mark_rubric_and_comment()
    {
        var (dir, _, _) = Synced();
        using var _dir = dir;
        string folder = Path.Combine(FakeCanvas.CanvasRoot(dir), "assignments", "Problem set 4");
        string feedback = File.ReadAllText(Path.Combine(folder, "feedback.md"));
        Assert.Contains("canvas_id: 9002\n", feedback);
        Assert.Contains("url: https://canvas.test/courses/4201/assignments/9002\n", feedback);
        Assert.Contains(Crawl.Generated, feedback);
        Assert.Contains("- **Score:** 18/20\n", feedback);
        Assert.Contains("- **Status:** Graded\n", feedback);
        Assert.Contains("- **Submitted:** Tue 16 Sep 2025, 9:41 PM\n", feedback);
        Assert.Contains("- **Graded:** Mon 22 Sep 2025, 11:00 AM\n", feedback);
        Assert.Contains("- **Attempt:** 1\n", feedback);
        Assert.Contains("- **Files:** [ps4-answers.pdf](submission/ps4-answers.pdf) (1.2 MB), [ps4.py](submission/ps4.py) (4 KB)\n", feedback);
        Assert.Contains("| Criterion | Mark | Rating | Comment |\n|---|---|---|---|\n"
            + "| Base cases | 5 / 5 | Full marks |  |\n"
            + "| Stack traces | 8 / 10 | One frame missing | The frame for n = 1 is missing in 3b. |\n"
            + "| Style | 5 / 5 | Full marks | Name helpers after what they return. |\n", feedback);
        Assert.Contains("- **Dr. Okafor** (Mon 22 Sep 2025, 11:00 AM): Good work overall. Watch the last frame in 3b, it’s the one people drop.\n", feedback);
        Assert.DoesNotContain("## Attempts", feedback); // only one
        Assert.DoesNotContain("Late penalty", feedback);

        string spec = File.ReadAllText(Path.Combine(folder, "spec.md"));
        Assert.Contains("**Due** Tue 16 Sep 2025, 11:59 PM · **20 points**\n", spec);
        Assert.Contains("Available from Tue 9 Sep 2025, 12:00 AM\n", spec);
        Assert.Contains("| Stack traces (A frame for every call, in order.) | 10 | Full marks (10); One frame missing (8); No marks (0) |", spec);
        string lab3 = File.ReadAllText(Path.Combine(FakeCanvas.CanvasRoot(dir), "assignments", "Lab 3- recursion traces", "spec.md"));
        Assert.Contains("| Correct traces (Every call and every return value is right.) | 10 | Full marks (10); Partial (5); No marks (0) |", lab3);
        string quiz = File.ReadAllText(Path.Combine(FakeCanvas.CanvasRoot(dir), "assignments", "Syllabus quiz", "spec.md"));
        Assert.Contains("**Due** Fri 5 Sep 2025, 11:59 PM · **5 points** · 2 attempts\n", quiz);
    }

    [Fact]
    public void Every_attempt_and_the_grader_s_files_are_saved_and_videos_stay_on_canvas()
    {
        var (dir, sync, canvas) = Synced();
        using var _dir = dir;
        string lab2 = Path.Combine(FakeCanvas.CanvasRoot(dir), "assignments", "Lab 2- tracing loops");
        Assert.Equal("%PDF-1.4 lab 2", File.ReadAllText(Path.Combine(lab2, "submission", "lab2-traces.pdf")));
        Assert.Equal("%PDF-1.4 lab 2 draft", File.ReadAllText(Path.Combine(lab2, "submission", "attempt 1", "lab2-draft.pdf")));
        Assert.Equal("%PDF-1.4 lab 2 marked up", File.ReadAllText(Path.Combine(lab2, "feedback", "lab2-marked.pdf")));
        // A video handed in, and a video comment, stay on Canvas: never asked for.
        Assert.False(File.Exists(Path.Combine(lab2, "submission", "attempt 1", "lab2-walkthrough.mp4")));
        Assert.DoesNotContain(canvas.Requested, u => u.Contains("/files/8603/", StringComparison.Ordinal) || u.Contains("media_download", StringComparison.Ordinal));
        Assert.Equal(1, canvas.Requested.Count(u => u.Contains("/files/8601/", StringComparison.Ordinal))); // latest attempt's file, once

        var info = CourseIndex.Load(dir.Path, "CS 101")!.Assignments.Single(a => a.Id == 9004);
        var s = info.Submission!;
        Assert.Equal([1, 2], s.Attempts.Select(t => t.Attempt));
        Assert.Equal(["Canvas/assignments/Lab 2- tracing loops/submission/attempt 1/lab2-draft.pdf", null], s.Attempts[0].Files.Select(f => f.Local));
        Assert.Equal(("video", "lab2-walkthrough.mp4"), (s.Attempts[0].Files[1].Skipped, s.Attempts[0].Files[1].Name));
        Assert.Equal(s.Files[0].Local, s.Attempts[1].Files[0].Local);
        Assert.Equal("Canvas/assignments/Lab 2- tracing loops/feedback/lab2-marked.pdf", s.Comments[0].Files.Single().Local);
        Assert.Equal("https://canvas.test/users/self/media_download?entryId=m-lab2&type=mp4&redirect=1", s.Comments[1].MediaUrl);
        Assert.Equal([false, false, true], s.Comments.Select(c => c.Mine));

        string feedback = File.ReadAllText(Path.Combine(lab2, "feedback.md"));
        Assert.Contains("- **Score:** 17/20\n", feedback);
        Assert.Contains("- **Status:** Submitted late\n", feedback);
        Assert.Contains("- **Attempt:** 2\n", feedback);
        Assert.Contains("- **Late penalty:** −2 points\n", feedback);
        Assert.Contains("  - Attached: [lab2-marked.pdf](feedback/lab2-marked.pdf) (310 KB)\n", feedback);
        Assert.Contains("  - [Audio or video comment, on Canvas](https://canvas.test/users/self/media_download?entryId=m-lab2&type=mp4&redirect=1)\n", feedback);
        Assert.Contains("- **Sam Rivera** (Thu 18 Sep 2025, 12:30 PM): Thanks, I see it now.\n", feedback);
        Assert.Contains("## Attempts\n\n"
            + "- Attempt 2 (latest), submitted Fri 12 Sep 2025, 8:20 AM, late: [lab2-traces.pdf](submission/lab2-traces.pdf) (86 KB)\n"
            + "- Attempt 1, submitted Thu 11 Sep 2025, 11:40 PM: [lab2-draft.pdf](submission/attempt%201/lab2-draft.pdf) (40 KB), "
            + "[lab2-walkthrough.mp4](https://canvas.test/files/8603/download) (60 MB, not saved: video, on Canvas)\n", feedback);

        // Synced again: nothing is downloaded twice.
        int asked = canvas.Requested.Count;
        Assert.True(canvas.Run(sync));
        Assert.DoesNotContain(canvas.Requested.Skip(asked), u => u.Contains("/download", StringComparison.Ordinal));
    }

    [Fact]
    public void The_index_keeps_no_one_s_ids_avatars_or_emails()
    {
        var (dir, _, _) = Synced();
        using var _dir = dir;
        string json = File.ReadAllText(CourseIndex.PathIn(dir.Path, "CS 101"));
        foreach (string never in new[] { "avatar", "author_id", "user_id", "\"author\":{", "5501", "3301", "verifier", "preview_url", "pronouns" })
            Assert.DoesNotContain(never, json);
        Assert.Contains("\"author\":\"Dr. Okafor\"", json);
        Assert.False(File.Exists(CourseIndex.StagedPathIn(dir.Path, "CS 101"))); // the staged copy is gone once promoted
        Assert.DoesNotContain("assignments", JsonNode.Parse(File.ReadAllText(Path.Combine(dir.Path, "crawl.json")))!.AsObject().Select(kv => kv.Key));
    }

    [Fact]
    public void A_class_s_first_sync_is_not_news()
    {
        using var dir = new TempDir();
        var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        var said = new List<CanvasChange>();
        sync.Finished += said.AddRange;
        Assert.True(FakeCanvas.Cs101().Run(sync));
        Assert.Empty(said);
        var s = CanvasSettings.Load(dir.Path);
        Assert.Empty(s.Changes);
        Assert.Empty(s.LastChanges);
        Assert.Equal(5, Assignments.Load(dir.Path).Count);
    }

    /// <summary>A fixture list with one item changed.</summary>
    static string Changed(string fixture, long id, Action<JsonObject> change)
    {
        var list = JsonNode.Parse(FakeCanvas.Fixture(fixture))!.AsArray();
        change(list.OfType<JsonObject>().Single(o => (o["assignment_id"] ?? o["id"])!.GetValue<long>() == id));
        return list.ToJsonString();
    }

    static string Without(string fixture, long id)
    {
        var list = JsonNode.Parse(FakeCanvas.Fixture(fixture))!.AsArray();
        list.Remove(list.OfType<JsonObject>().Single(o => (o["assignment_id"] ?? o["id"])!.GetValue<long>() == id));
        return list.ToJsonString();
    }

    [Theory]
    [InlineData("new", "New: CS 101 · Lab 4: trees · due Tue 7 Oct, 11:59 PM")]
    [InlineData("moved", "Moved: CS 101 · Lab 3: recursion traces · now Wed 11:59 PM")]
    [InlineData("graded", "Graded: CS 101 · Lab 3: recursion traces · 19/20")]
    [InlineData("feedback", "Feedback: CS 101 · Problem set 4 · 1 new comment")]
    [InlineData("missing", "Missing: CS 101 · Lab 3: recursion traces · was due Tue 23 Sep, 11:59 PM")]
    [InlineData("removed", "Removed: CS 101 · Syllabus quiz")]
    public void What_changed_on_canvas_is_said_in_the_design_s_words(string kind, string text)
    {
        var (dir, sync, canvas) = Synced();
        using var _dir = dir;
        var said = new List<CanvasChange>();
        sync.Finished += said.AddRange;
        switch (kind)
        {
            case "new":
                var list = JsonNode.Parse(FakeCanvas.Fixture("cs101-assignments.json"))!.AsArray();
                list.Add(new JsonObject
                {
                    ["id"] = 9006, ["name"] = "Lab 4: trees", ["due_at"] = "2025-10-08T06:59:00Z", ["points_possible"] = 20,
                    ["submission_types"] = new JsonArray("online_upload"), ["published"] = true, ["html_url"] = "https://canvas.test/courses/4201/assignments/9006",
                });
                canvas.Json(AssignmentsPath, list.ToJsonString());
                break;
            case "moved":
                canvas.Json(AssignmentsPath, Changed("cs101-assignments.json", 9001, a => a["due_at"] = "2025-10-02T06:59:00Z"));
                break;
            case "graded":
                canvas.Json(SubmissionsPath, Changed("cs101-submissions.json", 9001, s =>
                {
                    s["workflow_state"] = "graded";
                    s["score"] = 19;
                    s["grade"] = "19";
                    s["submitted_at"] = "2025-09-25T16:00:00Z";
                }));
                break;
            case "feedback":
                canvas.Json(SubmissionsPath, Changed("cs101-submissions.json", 9002, s => s["submission_comments"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = 51005, ["author_id"] = 3301, ["author_name"] = "Dr. Okafor", ["comment"] = "See me about 3b.", ["created_at"] = "2025-09-25T16:00:00Z",
                })));
                break;
            case "missing":
                canvas.Json(AssignmentsPath, Changed("cs101-assignments.json", 9001, a => a["due_at"] = "2025-09-24T06:59:00Z"));
                canvas.Json(SubmissionsPath, Changed("cs101-submissions.json", 9001, s => s["missing"] = true));
                break;
            case "removed":
                canvas.Json(AssignmentsPath, Without("cs101-assignments.json", 9005)).Json(SubmissionsPath, Without("cs101-submissions.json", 9005));
                break;
        }
        Assert.True(canvas.Run(sync));

        var change = Assert.Single(said, c => c.Kind == kind);
        Assert.Equal(text, change.Text);
        Assert.Equal("CS 101", change.Class);
        var s = CanvasSettings.Load(dir.Path);
        Assert.Contains(text, s.Changes);
        Assert.Contains(s.LastChanges, c => c == change);
        // The index follows Canvas: a removed assignment is gone from it (its folder stays on disk).
        var ids = CourseIndex.Load(dir.Path, "CS 101")!.Assignments.Select(a => a.Id).ToList();
        Assert.Equal(kind != "removed", ids.Contains(9005));
        Assert.Equal(kind == "removed", !Assignments.Load(dir.Path).Any(a => a.Id == 9005));
        Assert.True(File.Exists(Path.Combine(FakeCanvas.CanvasRoot(dir), "assignments", "Syllabus quiz", "spec.md")));
    }

    [Fact]
    public void A_sync_cut_off_part_way_carries_on_from_what_it_had_read()
    {
        using var dir = new TempDir();
        var canvas = FakeCanvas.Cs101();
        var first = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        var work = first.Work(force: true);
        // The assignments listing is filed; then the library stops before the rest comes back.
        var assignments = work.Jobs.Single(j => j.Url.Contains("/assignments?", StringComparison.Ordinal));
        first.Results([canvas.Answer(assignments)]);
        Assert.Equal(5, CourseIndex.LoadStaged(dir.Path, "CS 101")!.Assignments.Count);
        Assert.Null(CourseIndex.Load(dir.Path, "CS 101"));

        // A new library on the same folder: the extension's other answers arrive, and the sync finishes.
        var again = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        again.Results(work.Jobs.Where(j => j != assignments).Select(canvas.Answer).ToList());
        Assert.True(canvas.Run(again, force: false));
        var index = CourseIndex.Load(dir.Path, "CS 101")!;
        Assert.Equal(5, index.Assignments.Count);
        Assert.Equal(18, index.Assignments.Single(a => a.Id == 9002).Submission!.Score);
        Assert.True(File.Exists(Path.Combine(FakeCanvas.CanvasRoot(dir), "assignments", "Problem set 4", "feedback.md")));
        Assert.Equal(5, Assignments.Load(dir.Path).Count);
    }

    [Fact]
    public void Two_assignments_with_one_name_get_a_folder_each()
    {
        using var dir = new TempDir();
        var list = JsonNode.Parse(FakeCanvas.Fixture("cs101-assignments.json"))!.AsArray();
        list.OfType<JsonObject>().Single(a => a["id"]!.GetValue<long>() == 9003)["name"] = "Problem set 4";
        var canvas = FakeCanvas.Cs101().Json(AssignmentsPath, list.ToJsonString());
        var sync = FakeCanvas.Library(dir, () => FakeCanvas.DesignNow);
        Assert.True(canvas.Run(sync));
        Assert.Equal("Canvas/assignments/Problem set 4", sync.Crawl.AssignmentFolder("CS 101", 9003));
        Assert.Equal("Canvas/assignments/Problem set 4 2", sync.Crawl.AssignmentFolder("CS 101", 9002));
        string root = FakeCanvas.CanvasRoot(dir);
        Assert.Contains("canvas_id: 9003\n", File.ReadAllText(Path.Combine(root, "assignments", "Problem set 4", "spec.md")));
        Assert.Contains("canvas_id: 9002\n", File.ReadAllText(Path.Combine(root, "assignments", "Problem set 4 2", "spec.md")));
        Assert.True(File.Exists(Path.Combine(root, "assignments", "Problem set 4 2", "submission", "ps4-answers.pdf")));
    }

    static CourseIndex Index(params AssignmentInfo[] assignments) => new() { Class = "CS 101", CourseId = 4201, Assignments = [.. assignments] };

    static AssignmentInfo Info(long id, string name, SubmissionInfo? s = null) => new() { Id = id, Name = name, Folder = $"Canvas/assignments/{name}", Submission = s };

    [Fact]
    public void Each_listing_is_promoted_on_its_own()
    {
        var detailed = new SubmissionInfo
        {
            State = "graded", Score = 18, Comments = [new CommentInfo { Author = "Dr. Okafor", Text = "Good work overall." }],
            Files = [new FileRef { Id = 8801, Name = "ps4-answers.pdf", Local = "Canvas/assignments/Problem set 4/submission/ps4-answers.pdf" }],
        };
        var prev = Index(Info(9002, "Problem set 4", detailed), Info(9005, "Syllabus quiz"));
        prev.ReadAt["assignments"] = "2025-09-24T10:00:00-07:00";
        // This sync: the assignments listing has Problem set 4 regraded (19) and no Syllabus quiz; submissions failed.
        var staged = Index(Info(9002, "Problem set 4", new SubmissionInfo { State = "graded", Score = 19 }));
        staged.Staged = new SyncParts();
        const string at = "2025-09-25T10:24:00-07:00";

        var ok = CourseIndex.Promoted(prev, staged, new Dictionary<string, string> { ["assignments"] = "ok", ["submissions"] = "failed" }, at);
        var ps4 = Assert.Single(ok.Assignments); // what Canvas no longer lists is gone
        Assert.Equal(19, ps4.Submission!.Score); // the fresh summary…
        Assert.Equal("Good work overall.", Assert.Single(ps4.Submission.Comments).Text); // …with the details as last read
        Assert.Equal(("ok", "failed", at), (ok.Sections["assignments"], ok.Sections["submissions"], ok.ReadAt["assignments"]));

        var failed = CourseIndex.Promoted(prev, staged, new Dictionary<string, string> { ["assignments"] = "failed", ["submissions"] = "failed" }, at);
        Assert.Equal([9002L, 9005], failed.Assignments.Select(a => a.Id)); // what was known stays
        Assert.Equal(18, failed.Assignments[0].Submission!.Score);
        Assert.Equal("2025-09-24T10:00:00-07:00", failed.ReadAt["assignments"]);

        var hidden = CourseIndex.Promoted(prev, staged, new Dictionary<string, string> { ["assignments"] = "ok", ["submissions"] = "hidden" }, at);
        Assert.Empty(hidden.Assignments.Single().Submission!.Comments);
        Assert.Empty(CourseIndex.Promoted(prev, staged, new Dictionary<string, string> { ["assignments"] = "hidden" }, at).Assignments);

        // Submissions read, assignments not: the submissions replace what was known, and one only they name is added.
        staged.Staged.Submissions[9002] = new SubmissionInfo { State = "graded", Score = 20 };
        staged.Staged.Assignments[9007] = Info(9007, "Reading quiz");
        staged.Staged.Submissions[9007] = new SubmissionInfo { State = "submitted" };
        var subs = CourseIndex.Promoted(prev, staged, new Dictionary<string, string> { ["assignments"] = "failed", ["submissions"] = "ok" }, at);
        Assert.Equal([9002L, 9005, 9007], subs.Assignments.Select(a => a.Id));
        Assert.Equal(20, subs.Assignments[0].Submission!.Score);
        Assert.Equal("submitted", subs.Assignments[2].Submission!.State);
        Assert.Null(subs.Staged); // the staging part never reaches the promoted index
    }
}
