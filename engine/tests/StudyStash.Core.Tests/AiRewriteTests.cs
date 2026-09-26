using StudyStash.Core.Ai;
using StudyStash.Library;

namespace StudyStash.Core.Tests;

/// <summary>/api/v2/ai/rewrite/* end to end, through TestSite and AiRemote: a rewrite runs with the engine asked
/// for, the current notes stay until the student chooses, and it survives a restart. No test here ever touches a
/// real AI account, a real terminal, or Ollama: every engine is a <see cref="ScriptedAi"/>.</summary>
public class AiRewriteTests
{
    // Longer than Summarize.MinTranscriptChars, so Start doesn't refuse it as "too short to write from".
    const string Sentence = "Today we covered the cell membrane and how osmosis moves water across it, and worked "
        + "through the sodium-potassium pump and why it needs energy to run against the gradient. ";
    const string Transcript = Sentence + Sentence + Sentence + Sentence + Sentence + Sentence + Sentence + Sentence
        + Sentence + Sentence + Sentence + Sentence;

    static Config Cfg(TempDir dir) => new(dir["home"], dir["pool"]) { PoolPassword = "pw", OllamaEnabled = true, OllamaModel = "qwen3:8b" };

    static async Task<TestSite> Site(Config cfg, Store store, AiJobs ai) => await TestSite.StartAsync(b => LibraryWeb.Build(b, cfg, store,
        new Pipeline(cfg, store, log: _ => { }), new LibraryWebOptions
        {
            ListModels = _ => Task.FromResult<List<(string, double)>?>(null), Tailscale = () => new TailscaleInfo(),
            Latest = _ => Task.FromResult<Release?>(null), Ai = ai,
        }));

    static void Seed(Store store, string id = "lec-1", string transcript = Transcript) => store.Save(
        new Meeting(id) { Title = "Cell membranes", Date = "2026-09-01T09:00:00", Owner = "Sam", Transcript = transcript },
        new Classification("Bio 110", 0.9, "ollama", "Cell membranes", ["cells"]),
        summaryMd: "Old notes about cells.", summaryModel: "qwen3:8b");

    /// <summary>Waits (no more than 5s, polling) for the rewrite to reach a state the test is looking for, instead
    /// of a fixed sleep — the job runs on its own background Task.</summary>
    static async Task<RewriteInfo> UntilAsync(AiRemote remote, string id, Func<RewriteInfo, bool> matches)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var info = await remote.RewriteAsync(id) ?? throw new InvalidOperationException("the library doesn't know /rewrite");
            if (matches(info)) return info;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"never got there — last state was '{info.State}'");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_rewrite_runs_then_the_old_notes_stay_until_you_use_the_new_ones()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        Seed(store);
        var gate = new TaskCompletionSource<bool>();
        var claude = new ScriptedAi("claude", "Claude") { Gate = gate, Script = [new AiEvent("final", "New, better notes.")] };
        var ai = new AiJobs(cfg.Home) { Providers = _ => claude, Checks = new FakeChecks().Installed("claude").Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var started = await remote.RewriteStartAsync("lec-1", "claude");
        Assert.NotNull(started);
        Assert.Equal("working", started!.State);
        Assert.Equal("claude", started.Engine);

        var working = await UntilAsync(remote, "lec-1", i => i.State != "none");
        Assert.Equal("working", working.State);
        Assert.Equal("Old notes about cells.", working.Current!.Markdown); // untouched while the job runs

        gate.SetResult(true);
        var ready = await UntilAsync(remote, "lec-1", i => i.State != "working");
        Assert.Equal("ready", ready.State);
        Assert.Equal("New, better notes.", ready.Draft!.Markdown);
        Assert.Equal("Claude Code", ready.Draft.By);
        Assert.Equal("Old notes about cells.", ready.Current!.Markdown); // still the old notes: nobody chose yet

        var used = await remote.RewriteUseAsync("lec-1");
        Assert.NotNull(used);
        Assert.Equal("none", used!.State); // decided: nothing left pending
        Assert.Equal("New, better notes.", used.Current!.Markdown);
        Assert.Equal("Claude Code", used.Current.By); // notes_model, read back through WhoWrote

        var row = store.Get("lec-1")!;
        Assert.Equal("Claude Code", Engines.WhoWrote(row.SummaryModel ?? ""));
        Assert.Contains("New, better notes.", File.ReadAllText(row.MdPath!)); // the .md file itself was rewritten
    }

    [Fact]
    public async Task Keep_old_drops_the_draft_and_leaves_the_notes_exactly_as_they_were()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        Seed(store);
        var claude = new ScriptedAi("claude", "Claude") { Script = [new AiEvent("final", "New draft nobody wants.")] };
        var ai = new AiJobs(cfg.Home) { Providers = _ => claude, Checks = new FakeChecks().Installed("claude").Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        await remote.RewriteStartAsync("lec-1", "claude");
        await UntilAsync(remote, "lec-1", i => i.State == "ready");

        var kept = await remote.RewriteKeepAsync("lec-1");
        Assert.NotNull(kept);
        Assert.Equal("none", kept!.State);
        Assert.Equal("Old notes about cells.", kept.Current!.Markdown);

        var row = store.Get("lec-1")!;
        Assert.Equal("Old notes about cells.", row.SummaryMd); // never touched
    }

    [Fact]
    public async Task Cancel_stops_the_engine_and_the_state_becomes_cancelled()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        Seed(store);
        var claude = new ScriptedAi("claude", "Claude") { Hang = true }; // waits on the run's own token, then throws
        var ai = new AiJobs(cfg.Home) { Providers = _ => claude, Checks = new FakeChecks().Installed("claude").Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        await remote.RewriteStartAsync("lec-1", "claude");
        await UntilAsync(remote, "lec-1", i => i.State == "working");

        var cancelled = await remote.RewriteCancelAsync("lec-1");
        Assert.NotNull(cancelled);
        Assert.Equal("cancelled", cancelled!.State);
        // Stays cancelled: the hung engine's own throw (once its token fires) must never flip this back to "failed".
        await Task.Delay(200);
        Assert.Equal("cancelled", (await remote.RewriteAsync("lec-1"))!.State);
        Assert.Equal("Old notes about cells.", (await remote.RewriteAsync("lec-1"))!.Current!.Markdown);
    }

    [Fact]
    public async Task An_engine_error_ends_the_job_as_failed_and_the_notes_are_unchanged()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        Seed(store);
        var claude = new ScriptedAi("claude", "Claude") { Throws = new InvalidOperationException("Claude: usage limit reached") };
        var ai = new AiJobs(cfg.Home) { Providers = _ => claude, Checks = new FakeChecks().Installed("claude").Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        await remote.RewriteStartAsync("lec-1", "claude");
        var failed = await UntilAsync(remote, "lec-1", i => i.State == "failed");
        Assert.Contains("usage limit", failed.Error);
        Assert.Equal("Old notes about cells.", failed.Current!.Markdown);

        var refused = await Assert.ThrowsAsync<LibraryRefusedException>(() => remote.RewriteUseAsync("lec-1"));
        Assert.Equal(409, refused.Status);
    }

    [Fact]
    public async Task A_second_start_while_one_is_already_running_is_refused()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        Seed(store);
        var claude = new ScriptedAi("claude", "Claude") { Gate = new TaskCompletionSource<bool>() };
        var ai = new AiJobs(cfg.Home) { Providers = _ => claude, Checks = new FakeChecks().Installed("claude").Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        await remote.RewriteStartAsync("lec-1", "claude");
        await UntilAsync(remote, "lec-1", i => i.State == "working");

        var refused = await Assert.ThrowsAsync<LibraryRefusedException>(() => remote.RewriteStartAsync("lec-1", "claude"));
        Assert.Equal(409, refused.Status);
    }

    [Fact]
    public async Task An_unknown_lecture_is_404()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var ai = new AiJobs(cfg.Home) { Checks = new FakeChecks().Installed("claude").Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var refused = await Assert.ThrowsAsync<LibraryRefusedException>(() => remote.RewriteStartAsync("nope", "claude"));
        Assert.Equal(404, refused.Status);
        var refusedGet = await Assert.ThrowsAsync<LibraryRefusedException>(() => remote.RewriteAsync("nope"));
        Assert.Equal(404, refusedGet.Status);
    }

    [Fact]
    public async Task A_lecture_with_no_transcript_or_an_uninstalled_engine_is_refused()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        Seed(store, "short", transcript: "too short to write notes from");
        Seed(store, "lec-1");
        var ai = new AiJobs(cfg.Home) { Checks = new FakeChecks().Installed("claude").Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var noTranscript = await Assert.ThrowsAsync<LibraryRefusedException>(() => remote.RewriteStartAsync("short", "claude"));
        Assert.Equal(409, noTranscript.Status);

        var notInstalled = await Assert.ThrowsAsync<LibraryRefusedException>(() => remote.RewriteStartAsync("lec-1", "codex"));
        Assert.Equal(409, notInstalled.Status);
        Assert.Contains("Codex", notInstalled.Message);
    }

    [Fact]
    public async Task A_ready_draft_survives_a_restart()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        Seed(store);
        var claude = new ScriptedAi("claude", "Claude") { Script = [new AiEvent("final", "Draft that must survive.")] };
        var checks = new FakeChecks().Installed("claude").Build();
        var rewrites1 = new Rewrites(cfg, store, new AiJobs(cfg.Home) { Providers = _ => claude, Checks = checks });

        rewrites1.Start("lec-1", "claude");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (rewrites1.Get("lec-1").State != "ready")
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the draft never became ready");
            await Task.Delay(20);
        }

        // A fresh Rewrites over the same home — as if the library had just restarted — reads the same job back.
        var rewrites2 = new Rewrites(cfg, store, new AiJobs(cfg.Home) { Checks = checks });
        var info = rewrites2.Get("lec-1");
        Assert.Equal("ready", info.State);
        Assert.Equal("Draft that must survive.", info.Draft!.Markdown);
        Assert.Equal("claude", info.Engine);
    }

    [Fact]
    public async Task A_job_still_working_when_the_library_stopped_comes_back_failed()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        Seed(store);
        var claude = new ScriptedAi("claude", "Claude") { Gate = new TaskCompletionSource<bool>() }; // never lets go
        var checks = new FakeChecks().Installed("claude").Build();
        var rewrites1 = new Rewrites(cfg, store, new AiJobs(cfg.Home) { Providers = _ => claude, Checks = checks });

        rewrites1.Start("lec-1", "claude");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (rewrites1.Get("lec-1").State != "working")
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("never reached working");
            await Task.Delay(20);
        }

        var rewrites2 = new Rewrites(cfg, store, new AiJobs(cfg.Home) { Checks = checks });
        var info = rewrites2.Get("lec-1");
        Assert.Equal("failed", info.State);
        Assert.Contains("restarted", info.Error);
    }
}
