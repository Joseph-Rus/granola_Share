using StudyStash.App.Services;
using StudyStash.App.ViewModels;

namespace StudyStash.App.Tests;

/// <summary>The Due list (design 09): its header, the fixture's three groups read exactly as the design's words, and
/// selecting a row.</summary>
public class CanvasDueTests
{
    static CanvasApi.DueResponse Due() => CanvasFixtures.Load<CanvasApi.DueResponse>("due");

    [Fact]
    public void Show_reads_the_fixture_in_mac_09_s_order_and_words()
    {
        var model = new CanvasDueModel(CanvasFixtures.Context());
        model.Show(Due());

        Assert.Equal("Due", CanvasDueModel.Header);
        Assert.Equal("3 to hand in · synced 10:24", model.SubHeader);
        Assert.False(model.IsEmpty);
        Assert.Equal(3, model.Groups.Count);

        var overdue = model.Groups[0];
        Assert.Equal("Overdue", overdue.Label);
        Assert.True(overdue.Accent);
        var missing = Assert.Single(overdue.Rows);
        Assert.Equal("Reading response: Treaty of Versailles", missing.Title);
        Assert.Equal("Missing", missing.Right);
        Assert.True(missing.Strong);
        Assert.Equal("HIST 210 · Was due Mon 22 Sep, 11:59 PM", missing.Sub);

        var week = model.Groups[1];
        Assert.Equal("This week", week.Label);
        Assert.False(week.Accent);
        Assert.Equal(2, week.Rows.Count);
        Assert.Equal("Quiz 3 practice", week.Rows[0].Title);
        Assert.Equal("To do", week.Rows[0].Right);
        Assert.False(week.Rows[0].Strong);
        Assert.Equal("CALC II · Tomorrow, 9:00 AM", week.Rows[0].Sub);
        Assert.Equal("Lab 3: recursion traces", week.Rows[1].Title);
        Assert.Equal("CS 101 · Tue 30 Sep, 11:59 PM", week.Rows[1].Sub);

        var handedIn = model.Groups[2];
        Assert.Equal("Handed in", handedIn.Label);
        Assert.Equal("Osmosis lab report", handedIn.Rows[0].Title);
        Assert.Equal("Submitted", handedIn.Rows[0].Right);
        Assert.Equal("BIO 110 · Submitted Wed 24 Sep", handedIn.Rows[0].Sub);
        Assert.Equal("Problem set 4", handedIn.Rows[1].Title);
        Assert.Equal("18/20", handedIn.Rows[1].Right);
        Assert.Equal("CS 101 · Graded Mon 22 Sep", handedIn.Rows[1].Sub);
    }

    [Fact]
    public void Selecting_a_row_calls_OnSelect_and_only_that_row_lights_up()
    {
        var model = new CanvasDueModel(CanvasFixtures.Context());
        model.Show(Due());
        (string Class, string Id)? picked = null;
        model.OnSelect = (cls, id) => picked = (cls, id);

        var lab3 = model.Groups[1].Rows[1];
        lab3.SelectCommand.Execute(null);

        Assert.Equal(("CS 101", "9001"), picked);
        Assert.True(lab3.Selected);
        Assert.Same(lab3, model.SelectedRow);
        Assert.False(model.Groups[0].Rows[0].Selected);

        var quiz = model.Groups[1].Rows[0];
        quiz.SelectCommand.Execute(null);
        Assert.False(lab3.Selected);
        Assert.True(quiz.Selected);
    }

    [Fact]
    public void SelectItem_finds_the_row_by_class_and_id()
    {
        var model = new CanvasDueModel(CanvasFixtures.Context());
        model.Show(Due());

        model.SelectItem("CS 101", "9001");

        Assert.NotNull(model.SelectedRow);
        Assert.Equal("Lab 3: recursion traces", model.SelectedRow!.Title);
    }

    [Fact]
    public void An_empty_due_list_says_nothing_to_hand_in()
    {
        var model = new CanvasDueModel(CanvasFixtures.Context());
        model.Show(new CanvasApi.DueResponse { Synced = CanvasFixtures.Now, ToHandIn = 0, Groups = [] });

        Assert.True(model.IsEmpty);
        Assert.Equal("Nothing to hand in · synced 10:24", model.SubHeader);
    }

    [Fact]
    public async Task LoadAsync_reads_the_library_s_due_list()
    {
        var fake = new FakeLibrary().Json(HttpMethod.Get, "/api/v2/canvas/due", "due");
        var model = new CanvasDueModel(CanvasFixtures.Context(fake));

        await model.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, model.Groups.Count);
    }
}
