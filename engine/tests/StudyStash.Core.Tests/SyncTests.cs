using System.Text.Json.Nodes;

namespace StudyStash.Core.Tests;

/// <summary>tests/test_sync_web.py's sync tests: the library asks Granola for new lectures and queues them.</summary>
public class SyncTests
{
    /// <summary>Stands in for GranolaClient: the same calls, canned lectures.</summary>
    class FakeSource(List<Meeting> stubs, List<Meeting> full, string transcript = "") : ILectureSource
    {
        public List<(string Call, object? Arg)> Calls { get; } = [];

        sealed class NoSession : IGranolaSession
        {
            public Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken ct = default) => throw new NotSupportedException();
            public Task<ToolResult> CallToolAsync(string name, JsonObject args, CancellationToken ct = default) => throw new NotSupportedException();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public Task<IGranolaSession> SessionAsync(CancellationToken ct = default) => Task.FromResult<IGranolaSession>(new NoSession());

        public virtual Task<List<Meeting>> ListMeetingsAsync(IGranolaSession session, DateOnly? since = null, DateOnly? until = null, int limit = 50,
            int maxPages = 20, CancellationToken ct = default)
        {
            Calls.Add(("list", since));
            return Task.FromResult(stubs.Select(Copy).ToList());
        }

        public virtual Task<List<Meeting>> GetMeetingsAsync(IGranolaSession session, IReadOnlyList<string> ids, CancellationToken ct = default)
        {
            Calls.Add(("get", ids));
            return Task.FromResult(full.Where(m => ids.Contains(m.Id)).Select(Copy).ToList());
        }

        public Task<string> GetTranscriptAsync(IGranolaSession session, string meetingId, CancellationToken ct = default) => Task.FromResult(transcript);

        static Meeting Copy(Meeting m) => Granola.MeetingFromJson(JsonNode.Parse(Granola.MeetingJson(m)));
    }

    sealed class BrokenSource(List<Meeting> stubs) : FakeSource(stubs, [])
    {
        public override Task<List<Meeting>> GetMeetingsAsync(IGranolaSession session, IReadOnlyList<string> ids, CancellationToken ct = default) =>
            throw new InvalidOperationException("boom");
    }

    static Config CfgFor(TempDir dir) => new(dir["home"], dir["pool"])
    {
        OllamaEnabled = false,
        Classes = [new ClassDef("CS 101", ["cs101"]), new ClassDef("Bio 110", ["biology"])],
    };

    static readonly Action<string> Quiet = _ => { };

    [Fact]
    public async Task Sync_queues_new_lectures_and_skips_known_ones()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        List<Meeting> stubs = [new("a") { Title = "CS101 lec 1", Date = "2026-09-10" }, new("b") { Title = "Lunch", Date = "2026-09-11" }];
        List<Meeting> full =
        [
            new("a") { Title = "CS101 lec 1", Date = "2026-09-10", NotesMarkdown = "loops", Raw = new JsonObject { ["id"] = "a" } },
            new("b") { Title = "Lunch", Date = "2026-09-11", NotesMarkdown = "tacos", Raw = new JsonObject { ["id"] = "b" } },
        ];
        var client = new FakeSource(stubs, full, "hello transcript");
        int woke = 0;
        var report = await Sync.SyncOnceAsync(cfg, client, store, Quiet, () => woke++);
        Assert.Equal((2, 2, 0), (report.Listed, report.New, report.Errors.Count));
        Assert.Equal(["CS101 lec 1", "Lunch"], report.Queued);
        Assert.Equal(1, woke);
        Assert.Equal(1L, store.Get("a")!.HasTranscript);
        Assert.Equal(2, await new Pipeline(cfg, store, log: Quiet).RunPendingAsync());
        Assert.Equal(new Dictionary<string, string?> { ["a"] = "CS 101", ["b"] = Configs.Unsorted }, store.ListNotes().ToDictionary(r => r.Id, r => r.ClassName));
        Assert.Equal("[\n  {\n    \"id\": \"a\"\n  },\n  {\n    \"id\": \"b\"\n  }\n]",
            Py.ReadText(Path.Combine(cfg.DebugDir, "last_meetings.json")));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d{6})?\+00:00$", store.GetState("last_sync"));

        var again = await Sync.SyncOnceAsync(cfg, client, store, Quiet);
        Assert.Equal(0, again.New);
        Assert.Equal("list", client.Calls[^1].Call);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-Sync.OverlapDays), client.Calls[^1].Arg); // since the last sync
    }

    [Fact]
    public async Task Sync_falls_back_to_the_list_copy_when_get_fails()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var client = new BrokenSource([new("z") { Title = "biology lab", Date = "2026-09-12", NotesMarkdown = "stub notes" }]);
        var report = await Sync.SyncOnceAsync(cfg, client, store, Quiet);
        Assert.Equal(["get_meetings: boom"], report.Errors);
        Assert.Equal(["biology lab"], report.Queued);
        await new Pipeline(cfg, store, log: Quiet).RunPendingAsync();
        Assert.Contains("stub notes", Py.ReadText(store.Get("z")!.MdPath!));
    }

    [Fact]
    public void The_first_sync_looks_back_30_days_and_later_ones_overlap_by_two()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today).AddDays(-30), Sync.Since(store));
        store.SetState("last_sync", "2026-09-15T23:59:59.123456+00:00"); // as Python's isoformat() writes it
        Assert.Equal(new DateOnly(2026, 9, 13), Sync.Since(store));
        store.SetState("last_sync", "2026-09-15T00:00:00+00:00");
        Assert.Equal(new DateOnly(2026, 9, 13), Sync.Since(store));
    }

    [Fact]
    public async Task The_loop_keeps_going_after_a_failed_round_and_stops_when_told()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        cfg.PollIntervalSeconds = 0;
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var logs = new List<string>();
        using var stop = new CancellationTokenSource();
        var client = new CountingSource(stop);
        await Sync.RunLoopAsync(cfg, client, store, logs.Add, stop: stop.Token);
        Assert.Equal(3, client.Rounds);
        Assert.Contains("[sync] error: Granola is down", logs);
    }

    /// <summary>Granola is down on the second round; the third stops the loop.</summary>
    sealed class CountingSource(CancellationTokenSource stop) : FakeSource([], [])
    {
        public int Rounds { get; private set; }

        public override Task<List<Meeting>> ListMeetingsAsync(IGranolaSession session, DateOnly? since = null, DateOnly? until = null,
            int limit = 50, int maxPages = 20, CancellationToken ct = default)
        {
            Rounds++;
            if (Rounds == 2) throw new InvalidOperationException("Granola is down");
            if (Rounds == 3) stop.Cancel();
            return Task.FromResult(new List<Meeting>());
        }
    }
}
