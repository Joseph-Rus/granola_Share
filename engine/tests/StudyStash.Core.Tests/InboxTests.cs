using StudyStash.Core.Ai;

namespace StudyStash.Core.Tests;

/// <summary>Capture: jotted down now, filed under its class later.</summary>
public class InboxTests
{
    [Fact]
    public void Captures_go_to_the_inbox_or_straight_to_a_class_s_notes()
    {
        using var dir = new TempDir();
        string lib = dir["library"];
        var inbox = new Inbox(lib, () => ["CS 101"], c => Path.Combine(lib, c), new AiJobs(dir["home"]), new History(lib), _ => { });
        var at = new DateTime(2026, 9, 25, 16, 13, 0);
        string kept = inbox.Capture("Ask about recursion depth!\nmore", "CS 101", at);
        Assert.Equal(Path.Combine(lib, "CS 101", "Notes", "2026-09-25 1613 Ask about recursion depth.md"), kept);
        Assert.Contains("Ask about recursion depth!\nmore", File.ReadAllText(kept));
        Assert.Empty(inbox.Waiting());
        string waiting = inbox.Capture("Something for later", null, at);
        Assert.Equal(Path.Combine(lib, "Inbox"), Path.GetDirectoryName(waiting));
        Assert.NotEqual(waiting, inbox.Capture("Something for later", null, at)); // the same minute and words: its own file
        Assert.Equal(2, inbox.Waiting().Count);
        Assert.Contains("[1] ", inbox.Prompt([("a", "one"), ("b", "two")]));
    }
}
