using StudyStash.Core.Ai;
using StudyStash.Library;

namespace StudyStash.Core.Tests;

/// <summary>/api/v2/ai/* end to end, through TestSite and AiRemote — exactly how the app reads it. No test here
/// ever touches a real AI account, a real terminal, or Ollama: every probe and every engine is a fake.</summary>
public class AiApiTests
{
    static Config Cfg(TempDir dir) => new(dir["home"], dir["pool"]) { PoolPassword = "pw", OllamaEnabled = true, OllamaModel = "qwen3:8b" };

    static async Task<TestSite> Site(Config cfg, Store store, AiJobs ai) => await TestSite.StartAsync(b => LibraryWeb.Build(b, cfg, store,
        new Pipeline(cfg, store, log: _ => { }), new LibraryWebOptions
        {
            ListModels = _ => Task.FromResult<List<(string, double)>?>(null), Tailscale = () => new TailscaleInfo(),
            Latest = _ => Task.FromResult<Release?>(null), Ai = ai,
        }));

    [Fact]
    public async Task Engines_reports_a_mixed_machine_through_AiRemote()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var ai = new AiJobs(cfg.Home) { Checks = new FakeChecks().Installed("codex").Ollama(models: [("qwen3:8b", 4.7)]).Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var overview = await remote.EnginesAsync();
        Assert.NotNull(overview);
        Assert.Equal(["ollama", "claude", "codex", "gemini"], overview!.Engines.Select(e => e.Id));
        Assert.Equal("ready", overview.Engines.Single(e => e.Id == "ollama").State);
        Assert.Equal("not_signed_in", overview.Engines.Single(e => e.Id == "codex").State); // installed, no hint, never tested
        Assert.Equal("not_installed", overview.Engines.Single(e => e.Id == "claude").State);
        Assert.True(overview.Fallback);
    }

    [Fact]
    public async Task Defaults_pick_who_writes_notes_and_who_answers_and_refuse_a_bad_pick()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var ai = new AiJobs(cfg.Home) { Checks = new FakeChecks().Installed("claude").Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var overview = await remote.DefaultsAsync(notes: "claude", ask: "claude", fallback: false);
        Assert.NotNull(overview);
        Assert.Equal(("claude", "claude", false), (overview!.Notes, overview.Ask, overview.Fallback));

        var unknown = await Assert.ThrowsAsync<LibraryRefusedException>(() => remote.DefaultsAsync(notes: "nope"));
        Assert.Equal(400, unknown.Status);
        var notInstalled = await Assert.ThrowsAsync<LibraryRefusedException>(() => remote.DefaultsAsync(notes: "codex"));
        Assert.Equal(409, notInstalled.Status);
        Assert.Contains("Codex", notInstalled.Message);
    }

    [Fact]
    public async Task Ask_answers_with_the_engine_you_pick()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var claude = new ScriptedAi("claude", "Claude") { Script = [new AiEvent("final", """{"answer":"Cells have membranes.","sources":[1]}""")] };
        var ai = new AiJobs(cfg.Home) { Providers = id => id == "claude" ? claude : new ScriptedAi(id), Checks = new FakeChecks().Installed("claude").Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var reply = await remote.AskAsync(new AskRequest("What did we study today?")
        {
            Engine = "claude", Live = "Today we studied the cell membrane and osmosis in class.",
        });
        Assert.NotNull(reply);
        Assert.Equal(("Cells have membranes.", "claude", "Claude Code", false), (reply!.Answer, reply.Engine, reply.EngineName, reply.FellBack));
    }

    [Fact]
    public async Task Ask_falls_back_to_ollama_when_the_picked_engine_times_out()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var codex = new ScriptedAi("codex", "Codex") { Hang = true };
        var ollama = new ScriptedAi("ollama") { Script = [new AiEvent("final", """{"answer":"From Ollama.","sources":[]}""")] };
        var ai = new AiJobs(cfg.Home)
        {
            Providers = id => id switch { "codex" => codex, "ollama" => ollama, _ => new ScriptedAi(id) },
            // installed, with a credential hint: "unchecked", not "not_signed_in" — so it's actually tried, not skipped up front
            Checks = new FakeChecks().Installed("codex").HasEnv("OPENAI_API_KEY", "sk-1").Build(),
            AskTimeout = TimeSpan.FromMilliseconds(200),
        };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var reply = await remote.AskAsync(new AskRequest("What happened in class?") { Engine = "codex", Live = "Something happened today in class." });
        Assert.NotNull(reply);
        Assert.True(reply!.FellBack);
        Assert.Equal(("ollama", "codex", "From Ollama."), (reply.Engine, reply.Asked, reply.Answer));
        Assert.Contains("didn't respond in time", reply.Why);
    }

    [Fact]
    public async Task Ask_falls_back_on_a_usage_limit_error_and_records_it_until_it_lifts()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var claude = new ScriptedAi("claude", "Claude") { Script = [AiEvent.Error("rate limit exceeded, resets in 2 hours")] };
        var ollama = new ScriptedAi("ollama") { Script = [new AiEvent("final", """{"answer":"Ollama's own answer.","sources":[]}""")] };
        var now = new DateTime(2026, 1, 1, 9, 0, 0);
        var ai = new AiJobs(cfg.Home)
        {
            Providers = id => id switch { "claude" => claude, "ollama" => ollama, _ => new ScriptedAi(id) },
            Checks = new FakeChecks().Installed("claude").At(now).Build(),
        };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var reply = await remote.AskAsync(new AskRequest("What happened?") { Engine = "claude", Live = "Something happened today." });
        Assert.NotNull(reply);
        Assert.True(reply!.FellBack);
        Assert.Equal("Ollama's own answer.", reply.Answer);
        Assert.Contains("didn't answer", reply.Why);

        var overview = await remote.EnginesAsync();
        var row = overview!.Engines.Single(e => e.Id == "claude");
        Assert.Equal("limited", row.State);
        Assert.Equal(now.AddHours(2).ToString("o"), row.Until);
    }

    [Fact]
    public async Task Turning_fallback_off_lets_the_picked_engines_own_error_come_back()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        new AiSettings { Fallback = false }.Save(cfg.Home);
        var claude = new ScriptedAi("claude") { Script = [AiEvent.Error("the model refused to answer")] };
        var ai = new AiJobs(cfg.Home) { Providers = id => id == "claude" ? claude : new ScriptedAi(id), Checks = new FakeChecks().Installed("claude").Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var refused = await Assert.ThrowsAsync<LibraryRefusedException>(() =>
            remote.AskAsync(new AskRequest("What happened?") { Engine = "claude", Live = "Something happened." }));
        Assert.Equal(503, refused.Status);
        Assert.Contains("the model refused to answer", refused.Message);
    }

    [Fact]
    public async Task Signing_in_is_refused_away_from_the_librarys_own_computer()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var ai = new AiJobs(cfg.Home) { Checks = new FakeChecks().Installed("codex").Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        // TestSite has no real socket, so it never looks local — the same as a laptop reaching the library remotely.
        var refused = await Assert.ThrowsAsync<LibraryRefusedException>(() => remote.SignInAsync("codex"));
        Assert.Equal(409, refused.Status);
        Assert.Contains("codex login", refused.Message);
    }

    [Fact]
    public async Task Start_and_download_reach_only_the_fake_checks()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var checks = new FakeChecks().Ollama().StartsOllama(true).Pulls(true);
        var ai = new AiJobs(cfg.Home) { Checks = checks.Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var started = await remote.StartAsync("ollama");
        Assert.NotNull(started);
        Assert.Equal(1, checks.StartCalls);

        var downloaded = await remote.DownloadAsync("ollama");
        Assert.NotNull(downloaded?.Overview?.Pulling);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && (await remote.EnginesAsync())!.Pulling is not null) await Task.Delay(20);
        Assert.Equal(1, checks.PullCalls);
        Assert.Null((await remote.EnginesAsync())!.Pulling);
    }

    [Fact]
    public async Task Checking_an_engine_records_whether_it_answered()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var codex = new ScriptedAi("codex", "Codex") { Script = [new AiEvent("final", "ready")] };
        var ai = new AiJobs(cfg.Home) { Providers = id => id == "codex" ? codex : new ScriptedAi(id), Checks = new FakeChecks().Installed("codex").Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var said = await remote.CheckAsync("codex");
        Assert.NotNull(said);
        Assert.Equal("ready", said!.Overview!.Engines.Single(e => e.Id == "codex").State);
    }

    [Fact]
    public async Task Picking_a_model_updates_ollamas_notes_model_and_a_clis_own_model()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var ai = new AiJobs(cfg.Home) { Checks = new FakeChecks().Installed("claude").Ollama(models: [("qwen3:8b", 4.7), ("llama3", 4.7)]).Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var afterOllama = await remote.ModelAsync("ollama", "llama3");
        Assert.NotNull(afterOllama);
        Assert.Equal("llama3", afterOllama!.Engines.Single(e => e.Id == "ollama").Model);
        Assert.Equal("llama3", Configs.Load(cfg.Home).SummaryModel);

        var afterClaude = await remote.ModelAsync("claude", "opus");
        Assert.Equal("opus", afterClaude!.Engines.Single(e => e.Id == "claude").Model);
    }

    [Fact]
    public async Task Dismissing_a_problem_hides_it_from_the_overview()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var ai = new AiJobs(cfg.Home) { Checks = new FakeChecks().Ollama().Build() }; // not running, and it's the default: a problem
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "pw", site.Client);

        var before = await remote.EnginesAsync();
        var problem = Assert.Single(before!.Problems);
        var after = await remote.DismissAsync(problem.Id);
        Assert.Empty(after!.Problems);
    }

    [Fact]
    public async Task An_older_library_with_no_ai_routes_gives_null_from_AiRemote()
    {
        await using var site = await TestSite.StartAsync(b => b.Build()); // nothing mapped: a plain 404, not one of ours
        var remote = new AiRemote("http://localhost", "pw", site.Client);
        Assert.Null(await remote.EnginesAsync());
        Assert.Null(await remote.DefaultsAsync(fallback: true));
        Assert.Null(await remote.StartAsync("ollama"));
    }

    [Fact]
    public async Task A_wrong_key_is_401()
    {
        using var dir = new TempDir();
        var cfg = Cfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var ai = new AiJobs(cfg.Home) { Checks = new FakeChecks().Ollama(models: [("qwen3:8b", 4)]).Build() };
        await using var site = await Site(cfg, store, ai);
        var remote = new AiRemote("http://localhost", "wrong-password", site.Client);

        var refused = await Assert.ThrowsAsync<LibraryRefusedException>(() => remote.EnginesAsync());
        Assert.Equal(401, refused.Status);
    }
}
