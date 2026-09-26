using StudyStash.Core.Ai;

namespace StudyStash.Core.Tests;

/// <summary>Whether each engine is ready, worked out from fake probes: never a real account, terminal, or Ollama.</summary>
public class AiEnginesTests
{
    static Config Cfg(string home) => new(home, home) { OllamaModel = "qwen3:8b" };

    static async Task<EngineInfo> Row(string id, AiSettings settings, Config cfg, FakeChecks checks) =>
        (await Engines.StatusAsync(settings, cfg, checks.Build())).Engines.Single(e => e.Id == id);

    [Fact]
    public void Engines_come_in_the_designs_order_with_their_own_names()
    {
        Assert.Equal(["ollama", "claude", "codex", "gemini"], Engines.Order);
        Assert.Equal(["Ollama", "Claude Code", "Codex", "Gemini"], Engines.Order.Select(Engines.Name));
    }

    [Theory]
    [InlineData("Claude sonnet", "Claude Code")]
    [InlineData("ChatGPT", "Codex")]
    [InlineData("Gemini 3.1 Pro", "Gemini")]
    [InlineData("qwen3:30b", "Ollama")]
    [InlineData("", "")]
    public void WhoWrote_maps_the_pipelines_model_name_back_to_an_engine(string notesModel, string who) =>
        Assert.Equal(who, Engines.WhoWrote(notesModel));

    [Fact]
    public async Task Ollama_is_not_installed_not_running_missing_its_model_or_ready()
    {
        var cfg = Cfg("/lib");
        var settings = new AiSettings();
        Assert.Equal("not_installed", (await Row("ollama", settings, cfg, new FakeChecks())).State);
        Assert.Equal("not_running", (await Row("ollama", settings, cfg, new FakeChecks().Ollama())).State);
        Assert.Equal("model_missing", (await Row("ollama", settings, cfg, new FakeChecks().Ollama(models: [("llama3", 4.7)]))).State);
        var ready = await Row("ollama", settings, cfg, new FakeChecks().Ollama(models: [("qwen3:8b", 4.7)]));
        Assert.Equal("ready", ready.State);
        Assert.Equal("qwen3:8b", ready.Model);
        Assert.Contains(new ModelOption("qwen3:8b", "qwen3:8b (4.7 GB)"), ready.Models);
    }

    [Fact]
    public async Task A_cli_engine_is_not_installed_then_not_signed_in_until_a_hint_or_a_test_says_otherwise()
    {
        var cfg = Cfg("/lib");
        Assert.Equal("not_installed", (await Row("codex", new AiSettings(), cfg, new FakeChecks())).State);
        var installed = new FakeChecks().Installed("codex");
        Assert.Equal("not_signed_in", (await Row("codex", new AiSettings(), cfg, installed)).State); // no hint, never tested
        Assert.Equal("unchecked", (await Row("codex", new AiSettings(), cfg, installed.HasEnv("OPENAI_API_KEY", "sk-1"))).State);
        var tested = new AiSettings();
        tested.Tests["codex"] = "works";
        Assert.Equal("ready", (await Row("codex", tested, cfg, installed)).State);
    }

    [Fact]
    public async Task Claude_stays_unchecked_with_no_hint_at_all_since_its_login_is_in_the_keychain()
    {
        var checks = new FakeChecks().Installed("claude");
        Assert.Equal("unchecked", (await Row("claude", new AiSettings(), Cfg("/lib"), checks)).State);
    }

    [Fact]
    public async Task A_tested_engine_is_not_signed_in_or_failed_by_what_its_last_try_said()
    {
        var checks = new FakeChecks().Installed("codex");
        var signedOut = new AiSettings();
        signedOut.Tests["codex"] = "You are not logged in. Run codex login.";
        var row = await Row("codex", signedOut, Cfg("/lib"), checks);
        Assert.Equal("not_signed_in", row.State);
        Assert.Equal(signedOut.Tests["codex"], row.Why);
        var broke = new AiSettings();
        broke.Tests["codex"] = "the model timed out";
        Assert.Equal("failed", (await Row("codex", broke, Cfg("/lib"), checks)).State);
    }

    [Fact]
    public async Task A_usage_limit_in_the_future_makes_an_engine_limited_until_it_passes()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var checks = new FakeChecks().Installed("codex").At(now);
        var s = new AiSettings();
        s.Limits["codex"] = now.AddHours(3).ToString("o");
        var limited = await Row("codex", s, Cfg("/lib"), checks);
        Assert.Equal("limited", limited.State);
        Assert.Equal(s.Limits["codex"], limited.Until);
        s.Limits["codex"] = now.AddHours(-3).ToString("o"); // already past
        Assert.Equal("not_signed_in", (await Row("codex", s, Cfg("/lib"), checks)).State);
    }

    [Fact]
    public void Until_reads_a_relative_or_a_clock_time_from_the_error()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        Assert.Equal(now.AddHours(2), Engines.UntilFrom("rate limit: resets in 2 hours", now));
        Assert.Equal(new DateTime(2026, 1, 1, 15, 0, 0), Engines.UntilFrom("usage limit resets at 3pm", now));
        Assert.Equal(new DateTime(2026, 1, 2, 9, 0, 0), Engines.UntilFrom("quota resets at 9:00 AM", now)); // already past today
        Assert.Equal(now.AddHours(1), Engines.UntilFrom("quota exceeded", now)); // nothing to read: an hour from now
    }

    [Fact]
    public async Task Ollama_offline_is_a_problem_only_when_it_would_actually_be_used()
    {
        var cfg = Cfg("/lib");
        var checks = new FakeChecks().Ollama();
        var idle = new AiSettings { Provider = "claude", Fallback = false };
        Assert.Empty((await Engines.StatusAsync(idle, cfg, checks.Build())).Problems);
        var used = new AiSettings(); // the default for everything, and the fallback
        var overview = await Engines.StatusAsync(used, cfg, checks.Build());
        var problem = Assert.Single(overview.Problems);
        Assert.Equal(("engine_offline", "ollama"), (problem.Kind, problem.Engine));
    }

    [Fact]
    public async Task A_missing_ollama_model_is_a_problem_too_naming_the_model()
    {
        var cfg = Cfg("/lib");
        var overview = await Engines.StatusAsync(new AiSettings(), cfg, new FakeChecks().Ollama(models: [("llama3", 4.7)]).Build());
        var problem = Assert.Single(overview.Problems);
        Assert.Equal(("model_missing", "qwen3:8b"), (problem.Kind, problem.Model));
    }

    [Fact]
    public async Task A_default_engine_thats_not_signed_in_is_a_problem_naming_the_fallback()
    {
        var cfg = Cfg("/lib");
        var s = new AiSettings { Provider = "codex" };
        var overview = await Engines.StatusAsync(s, cfg, new FakeChecks().Installed("codex").Build());
        var problem = Assert.Single(overview.Problems);
        Assert.Equal(("not_signed_in", "codex", "Ollama"), (problem.Kind, problem.Engine, problem.FallbackTo));
    }

    [Fact]
    public async Task Dismissing_a_usage_limit_problem_hides_it_until_the_limit_changes()
    {
        var cfg = Cfg("/lib");
        var checks = new FakeChecks().Installed("codex").At(new DateTime(2026, 1, 1, 12, 0, 0)).Build();
        var s = new AiSettings();
        s.Limits["codex"] = new DateTime(2026, 1, 1, 15, 0, 0).ToString("o");
        var before = await Engines.StatusAsync(s, cfg, checks);
        var problem = Assert.Single(before.Problems);
        s.Dismissed.Add(problem.Id);
        Assert.Empty((await Engines.StatusAsync(s, cfg, checks)).Problems);
        s.Limits["codex"] = new DateTime(2026, 1, 1, 18, 0, 0).ToString("o"); // hit its limit again: a new until
        Assert.Single((await Engines.StatusAsync(s, cfg, checks)).Problems);
    }
}
