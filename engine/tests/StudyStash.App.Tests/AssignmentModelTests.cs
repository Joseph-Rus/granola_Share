using StudyStash.App.Services;
using StudyStash.App.ViewModels;

namespace StudyStash.App.Tests;

/// <summary>An assignment's detail (design 09/10): Lab 3 (still to hand in, no submission) and Problem set 4
/// (graded, with a rubric marked up, a comment and files) — the two fixtures the design itself draws.</summary>
public class AssignmentModelTests
{
    [Fact]
    public void Lab_3_reads_as_mac_09_s_detail()
    {
        var model = new AssignmentModel(CanvasFixtures.Context());
        model.Show(CanvasFixtures.Load<CanvasApi.AssignmentDetail>("assignment-9001"));

        Assert.Equal("CS 101 · Assignment", model.Meta);
        Assert.Equal("Lab 3: recursion traces", model.Title);
        Assert.Equal("Tue 30 Sep, 11:59 PM", model.DueValue);
        Assert.Equal("20", model.PointsValue);
        Assert.Equal("Status", model.ThirdLabel);
        Assert.Equal("To do", model.ThirdValue);

        Assert.Equal(3, model.Rubric.Count);
        Assert.Equal(new RubricRow("Correctness", "10 pts", null), model.Rubric[0]);
        Assert.Equal(new RubricRow("Style", "6 pts", null), model.Rubric[1]);
        Assert.Equal(new RubricRow("Write-up", "4 pts", null), model.Rubric[2]);

        Assert.Equal("Nothing handed in yet", model.SubmissionStatus);
        Assert.Equal("Due in 5 days", model.SubmissionDetail);
        Assert.False(model.HasComment);
        Assert.True(model.ShowHandInLink);
        Assert.False(model.HasFiles);
    }

    [Fact]
    public void Problem_set_4_reads_graded_with_its_rubric_marks_and_files()
    {
        var model = new AssignmentModel(CanvasFixtures.Context());
        model.Show(CanvasFixtures.Load<CanvasApi.AssignmentDetail>("assignment-9002"));

        Assert.Equal("Problem set 4", model.Title);
        Assert.Equal("Tue 16 Sep, 11:59 PM", model.DueValue);
        Assert.Equal("Score", model.ThirdLabel);
        Assert.Equal("18 / 20", model.ThirdValue);

        Assert.Equal(3, model.Rubric.Count);
        Assert.Equal(new RubricRow("Base case", "5 / 5", null), model.Rubric[0]);
        Assert.Equal(new RubricRow("Recursive case", "8 / 10", "“The frame for n = 1 is missing in 3b.”"), model.Rubric[1]);
        Assert.Equal(new RubricRow("Helper functions", "5 / 5", "“Name helpers after what they return.”"), model.Rubric[2]);

        Assert.Equal("Graded · 18 / 20", model.SubmissionStatus);
        Assert.Equal("Submitted Tue 16 Sep, 9:41 PM", model.SubmissionDetail);
        Assert.True(model.HasComment);
        Assert.StartsWith("Good work overall.", model.CommentText);
        Assert.Equal("Dr. Okafor", model.CommentAuthor);
        Assert.False(model.ShowHandInLink);

        Assert.Equal(2, model.Files.Count);
        Assert.Equal("ps4-answers.pdf", model.Files[0].Name);
        Assert.Equal("picture_as_pdf", model.Files[0].Glyph);
        Assert.Equal("1.2 MB", model.Files[0].SizeText);
        Assert.Equal("Problem set 4/ps4-answers.pdf", model.Files[0].Path);
        Assert.Equal("ps4.py", model.Files[1].Name);
        Assert.Equal("code", model.Files[1].Glyph);
        Assert.Equal("4 KB", model.Files[1].SizeText);
    }

    [Fact]
    public void OpenInCanvas_and_HandIn_open_the_assignment_s_url()
    {
        List<(string What, string Arg)> log = [];
        var model = new AssignmentModel(CanvasFixtures.Context(log: log));
        model.Show(CanvasFixtures.Load<CanvasApi.AssignmentDetail>("assignment-9001"));

        model.OpenInCanvasCommand.Execute(null);
        model.HandInCommand.Execute(null);

        Assert.Equal(2, log.Count(e => e.What == "OpenUrl" && e.Arg == "https://school.instructure.com/courses/4201/assignments/9001"));
    }

    [Fact]
    public void Search_calls_OnSearch()
    {
        var model = new AssignmentModel(CanvasFixtures.Context());
        bool searched = false;
        model.OnSearch = () => searched = true;

        model.SearchCommand.Execute(null);

        Assert.True(searched);
    }

    [Fact]
    public async Task Opening_a_chip_downloads_it_into_the_home_and_opens_it()
    {
        using var home = new TempHome();
        List<(string What, string Arg)> log = [];
        byte[] bytes = "%PDF-fake"u8.ToArray();
        var fake = new FakeLibrary().Bytes(HttpMethod.Get, "/api/v2/files/raw", bytes);
        var model = new AssignmentModel(CanvasFixtures.Context(fake, home.Path, log));
        model.Show(CanvasFixtures.Load<CanvasApi.AssignmentDetail>("assignment-9002"));

        model.Files[0].OpenCommand.Execute(null);
        await Task.Delay(50, TestContext.Current.CancellationToken); // Open fires the download without awaiting it

        string dest = System.IO.Path.Combine(home.Path, "cache", "canvas", "CS 101", "Problem set 4", "ps4-answers.pdf");
        Assert.True(System.IO.File.Exists(dest));
        Assert.Equal(bytes, await System.IO.File.ReadAllBytesAsync(dest, TestContext.Current.CancellationToken));
        Assert.Contains(log, e => e.What == "OpenFile" && e.Arg == dest);

        var sent = Assert.Single(fake.Requests, r => r.Path == "/api/v2/files/raw");
        Assert.Contains("class=CS%20101", sent.Query);
        Assert.Contains("path=Problem%20set%204%2Fps4-answers.pdf", sent.Query);
    }

    [Fact]
    public async Task LoadAsync_reads_the_assignment_by_class_and_id()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/assignment", "assignment-9001");
        var model = new AssignmentModel(CanvasFixtures.Context(fake));

        await model.LoadAsync("CS 101", "9001", TestContext.Current.CancellationToken);

        Assert.Equal("Lab 3: recursion traces", model.Title);
        Assert.Equal("?class=CS%20101&id=9001", Assert.Single(fake.Requests).Query);
    }
}
