using StudyStash.Core.Ai;

namespace StudyStash.Core.Tests;

/// <summary>Undo for what the AI changes: one change per chat turn, listed newest first, each one undoable.</summary>
public class HistoryTests
{
    [Fact]
    public void An_ai_s_new_file_and_its_edit_to_a_lecture_can_both_be_undone()
    {
        if (AiProvider.Which("git") is null) return; // nothing to undo with
        using var dir = new TempDir();
        string lib = dir["library"];
        Directory.CreateDirectory(Path.Combine(lib, "CS 101"));
        string lecture = Path.Combine(lib, "CS 101", "2026-09-24 Recursion.md");
        File.WriteAllText(lecture, "# Recursion\n");
        var h = new History(lib);

        // A turn: the lecture as it was is kept first, so undoing an edit restores it instead of deleting it.
        h.Baseline();
        var before = h.Snapshot();
        File.WriteAllText(lecture, "# Recursion\nchanged by the AI\n");
        File.WriteAllText(Path.Combine(lib, "CS 101", "Cheat sheet.md"), "# Cheat sheet\n");
        var changed = h.ChangedSince(before);
        Assert.Equal(["CS 101/2026-09-24 Recursion.md", "CS 101/Cheat sheet.md"], changed);
        string sha = h.Commit(changed, "Make me a cheat sheet", "Claude");
        Assert.NotEqual("", sha);

        var log = h.Log();
        Assert.Equal(("Make me a cheat sheet", "Claude"), (log[0].Title, log[0].By));
        Assert.Equal(2, log[0].Files.Count);

        Assert.True(h.Undo(sha).Ok);
        Assert.Equal("# Recursion\n", File.ReadAllText(lecture));
        Assert.False(File.Exists(Path.Combine(lib, "CS 101", "Cheat sheet.md")));
        Assert.Equal("undo", h.Log()[0].By);
    }

    [Fact]
    public void Nothing_changed_means_nothing_to_keep()
    {
        using var dir = new TempDir();
        Assert.Equal("", new History(dir.Path).Commit([], "nothing", "Claude"));
    }
}
