using System.Net;
using System.Net.Sockets;

namespace StudyStash.Core.Tests;

/// <summary>The library's pipeline while its notes engine is away: lectures wait in the queue, and get their notes
/// when it's back.</summary>
public class PipelineTests
{
    const string Line = "Recursion is a function calling itself on a smaller piece of the problem.\n";

    static string Lines(int n) => string.Concat(Enumerable.Repeat(Line, n));

    static Config CfgFor(TempDir dir) => new(dir["home"], dir["pool"])
    {
        OllamaModel = "small:1b", SummaryModel = "big:35b", Classes = [new ClassDef("CS 101"), new ClassDef("BIO 110")],
    };

    static readonly SortChatFn Sort = (_, _, _) =>
        Task.FromResult("""{"class_name": "CS 101", "confidence": 0.9, "lecture_title": "Recursion", "topics": ["recursion"]}""");

    static readonly Action<string> Quiet = _ => { };

    static HttpRequestException Refused() =>
        new("Connection refused (127.0.0.1:11434)", new SocketException((int)SocketError.ConnectionRefused));

    [Fact]
    public async Task An_engine_that_isnt_answering_leaves_the_lecture_queued_and_says_so()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        bool up = false;
        int asked = 0;
        var p = new Pipeline(cfg, store, Sort, (_, _) =>
        {
            asked++;
            return up ? Task.FromResult("## Summary\nRecursion, and the midterm.") : throw Refused();
        }, Quiet);
        store.Enqueue(new Meeting("l1") { Title = "CS 101 lecture", Transcript = Lines(40) });
        store.Enqueue(new Meeting("l2") { Title = "CS 101 lecture", Transcript = Lines(40) });

        Assert.Equal(0, await p.RunPendingAsync());
        // The pass ends at the first one: the second isn't tried against an engine that's away.
        Assert.Equal(1, asked);
        Assert.Equal("queued", store.Get("l1")!.Status);
        Assert.Equal("queued", store.Get("l2")!.Status);
        Assert.Null(store.Get("l1")!.MdPath);
        Assert.Equal("Ollama isn't answering on your library. New lectures wait and get their notes when it's back.", p.EngineProblem);
        Assert.Equal(TimeSpan.FromSeconds(60), p.Pause);

        up = true;
        Assert.Equal(2, await p.RunPendingAsync());
        var row = store.Get("l1")!;
        Assert.Equal(("done", "CS 101"), (row.Status, row.ClassName));
        Assert.StartsWith("## Summary", row.SummaryMd);
        Assert.Contains("Recursion, and the midterm.", Py.ReadText(row.MdPath!));
        Assert.Null(p.EngineProblem);
        Assert.Equal(TimeSpan.FromSeconds(30), p.Pause);
    }

    [Fact]
    public async Task No_answer_in_time_waits_too_and_the_engine_is_named()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var p = new Pipeline(cfg, store, Sort, (_, _) => throw new TimeoutException("Ollama did not answer within 600 s"), Quiet,
            notesEngine: () => "Ollama on the Mac mini");
        store.Enqueue(new Meeting("l1") { Title = "Lecture", Transcript = Lines(40) });
        await p.RunPendingAsync();
        Assert.Equal("queued", store.Get("l1")!.Status);
        Assert.StartsWith("Ollama on the Mac mini isn't answering", p.EngineProblem);
    }

    [Fact]
    public async Task An_engine_that_answers_no_still_files_the_lecture_without_notes()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        // A refusal with a status is an answer: the model isn't there, so the lecture is filed as before.
        var p = new Pipeline(cfg, store, Sort, (_, _) => throw new HttpRequestException("model 'big:35b' not found", null, HttpStatusCode.NotFound), Quiet);
        store.Enqueue(new Meeting("l1") { Title = "Lecture", Transcript = Lines(40) });
        Assert.Equal(1, await p.RunPendingAsync());
        var row = store.Get("l1")!;
        Assert.Equal("done", row.Status);
        Assert.Contains("not found", row.Error);
        Assert.Null(p.EngineProblem);
    }

    [Fact]
    public void What_counts_as_no_answer()
    {
        Assert.True(Pipeline.IsOffline(Refused()));
        Assert.True(Pipeline.IsOffline(new HttpRequestException("The response ended prematurely.")));
        Assert.True(Pipeline.IsOffline(new TimeoutException()));
        Assert.False(Pipeline.IsOffline(new HttpRequestException("Bad gateway", null, HttpStatusCode.BadGateway)));
        Assert.False(Pipeline.IsOffline(new InvalidOperationException("Claude Code: not signed in")));
    }

    [Fact]
    public async Task Running_on_its_own_it_waits_for_the_engine_and_a_wake_tries_again()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        bool up = false;
        var p = new Pipeline(cfg, store, Sort, (_, _) => up ? Task.FromResult("## Summary\nNotes.") : throw Refused(), Quiet);
        store.Enqueue(new Meeting("l1") { Title = "Lecture", Transcript = Lines(40) });
        using var stop = new CancellationTokenSource();
        var running = p.Start(stop.Token);
        try
        {
            for (int i = 0; i < 100 && p.EngineProblem is null; i++) await Task.Delay(50);
            Assert.NotNull(p.EngineProblem);
            Assert.Equal("queued", store.Get("l1")!.Status);
            up = true;
            p.Wake();
            for (int i = 0; i < 100 && store.Get("l1")!.Status != "done"; i++) await Task.Delay(50);
            Assert.Equal("done", store.Get("l1")!.Status);
            Assert.Null(p.EngineProblem);
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }
}
