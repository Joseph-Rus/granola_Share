using Avalonia.Headless.XUnit;
using StudyStash.App.ViewModels;
using StudyStash.Core;
using StudyStash.Core.Ai;

namespace StudyStash.App.Tests;

public class AiWordsTests
{
    [Theory]
    [InlineData("ready", "Ready", "Options")]
    [InlineData("unchecked", "Installed", "Check")]
    [InlineData("not_signed_in", "Not signed in", "Sign in")]
    [InlineData("not_running", "Not running", "Start")]
    [InlineData("model_missing", "Needs its model", "Download")]
    [InlineData("limited", "Limit reached", "Options")]
    [InlineData("not_installed", "Not installed", "Get it")]
    [InlineData("failed", "Didn't work", "Options")]
    public void Every_state_has_its_word_and_its_row_action(string state, string words, string action)
    {
        Assert.Equal(words, AiWords.EngineStateWords(state));
        Assert.Equal(action, AiWords.RowActionWords(state));
    }

    [Fact]
    public void Only_sign_in_is_the_primary_button()
    {
        Assert.True(AiWords.RowActionPrimary("not_signed_in"));
        foreach (string s in new[] { "ready", "unchecked", "not_running", "model_missing", "limited", "not_installed", "failed" })
            Assert.False(AiWords.RowActionPrimary(s));
    }

    [Fact]
    public void Only_ready_limited_and_failed_open_the_options_menu()
    {
        Assert.True(AiWords.NeedsOptions("ready"));
        Assert.True(AiWords.NeedsOptions("limited"));
        Assert.True(AiWords.NeedsOptions("failed"));
        foreach (string s in new[] { "unchecked", "not_signed_in", "not_running", "model_missing", "not_installed" })
            Assert.False(AiWords.NeedsOptions(s));
    }

    [Fact]
    public void Ollama_never_leaves_this_computer_a_signed_in_cli_says_so()
    {
        Assert.Equal("Runs on your library. Nothing leaves it.", AiWords.EngineAbout("ollama", "ready"));
        Assert.Equal("Runs on your library. Signed in.", AiWords.EngineAbout("claude", "ready"));
        Assert.Equal("Runs on your library.", AiWords.EngineAbout("codex", "not_signed_in"));
    }

    [Fact]
    public void Setup_words_are_phrased_for_this_computer()
    {
        Assert.Equal("Private. Runs here, nothing leaves this computer.", AiWords.SetupAbout("ollama", "ready"));
        Assert.Equal("Signed in on this computer.", AiWords.SetupAbout("claude", "ready"));
        Assert.Equal("Sign in first.", AiWords.SetupAbout("codex", "not_signed_in"));
        Assert.Equal("Installed on this computer.", AiWords.SetupAbout("gemini", "unchecked"));
    }
}

public class AiEnginesModelTests
{
    static (AiEnginesModel Model, FakeAiLibrary Library) Loaded()
    {
        var lib = new FakeAiLibrary { Overview = AiTestData.MixedOverview() };
        var model = new AiEnginesModel(lib);
        return (model, lib);
    }

    [AvaloniaFact]
    public async Task Loading_fills_the_rows_and_the_defaults_without_posting_anything()
    {
        var (model, lib) = Loaded();

        await model.Load();

        Assert.Equal(["Ollama", "Claude Code", "Codex"], model.Engines.Select(r => r.Name));
        Assert.Single(model.AddChoices);
        Assert.Equal("Gemini", model.AddChoices[0].Name);
        Assert.Equal("ollama", model.SelectedNotes);
        Assert.Equal("claude", model.SelectedAsk);
        Assert.True(model.Fallback);
        Assert.False(model.OlderLibrary);
        Assert.False(model.Offline);
        Assert.DoesNotContain("defaults", lib.Calls);

        var codex = model.Engines.Single(r => r.Id == "codex");
        Assert.Equal("Not signed in", codex.StateWords);
        Assert.True(codex.Warn);
        Assert.Equal("Sign in", codex.ActionWords);
        Assert.True(codex.IsPrimary);
    }

    [AvaloniaFact]
    public async Task Picking_who_writes_notes_posts_the_new_default()
    {
        var (model, lib) = Loaded();
        await model.Load();

        model.SelectedNotes = "claude";

        Assert.Equal(("claude", (string?)null, (bool?)null), (lib.DefaultsCalls[0].Notes, lib.DefaultsCalls[0].Ask, lib.DefaultsCalls[0].Fallback));
        Assert.Equal("claude", lib.Overview!.Notes);
    }

    [AvaloniaFact]
    public async Task Turning_the_fallback_off_posts_it()
    {
        var (model, lib) = Loaded();
        await model.Load();

        model.Fallback = false;

        Assert.Single(lib.DefaultsCalls);
        Assert.False(lib.Overview!.Fallback);
    }

    [AvaloniaFact]
    public async Task Signing_in_calls_the_engine_and_says_what_happened()
    {
        var (model, lib) = Loaded();
        await model.Load();
        var codex = model.Engines.Single(r => r.Id == "codex");

        await codex.ActCommand.ExecuteAsync(null);

        Assert.Contains("sign-in:codex", lib.Calls);
        Assert.Equal("Ready", model.Engines.Single(r => r.Id == "codex").StateWords);
        Assert.NotNull(model.Say);
    }

    [AvaloniaFact]
    public async Task Checking_a_not_yet_tested_engine_calls_check()
    {
        var overview = AiTestData.MixedOverview();
        var lib = new FakeAiLibrary { Overview = overview with { Engines = [.. overview.Engines.Select(e => e.Id == "codex" ? e with { State = "unchecked" } : e)] } };
        var model = new AiEnginesModel(lib);
        await model.Load();
        var codex = model.Engines.Single(r => r.Id == "codex");
        Assert.Equal("Check", codex.ActionWords);

        await codex.ActCommand.ExecuteAsync(null);

        Assert.Contains("check:codex", lib.Calls);
    }

    [AvaloniaFact]
    public async Task Adding_an_engine_opens_its_site()
    {
        var (model, _) = Loaded();
        await model.Load();
        string? opened = null;
        model.OpenUrl = url => opened = url;
        var gemini = model.AddChoices.Single();

        gemini.AddCommand.Execute(null);

        Assert.Equal("https://gemini.google.com", opened);
        Assert.Equal("Install Gemini on your library's computer.", model.Say);
    }

    [AvaloniaFact]
    public async Task An_older_library_says_so_instead_of_showing_engines()
    {
        var model = new AiEnginesModel(new FakeAiLibrary { Overview = null });

        await model.Load();

        Assert.True(model.OlderLibrary);
        Assert.Empty(model.Engines);
    }

    [AvaloniaFact]
    public async Task A_library_that_cant_be_reached_says_offline()
    {
        var lib = new FakeAiLibrary { OnEngines = () => throw new HttpRequestException("down") };
        var model = new AiEnginesModel(lib);

        await model.Load();

        Assert.True(model.Offline);
    }
}

public class AiSetupModelTests
{
    static (AiSetupModel Model, FakeAiLibrary Library) Loaded()
    {
        var lib = new FakeAiLibrary { Overview = AiTestData.MixedOverview() with { Ask = "ollama" } };
        var model = new AiSetupModel(lib);
        return (model, lib);
    }

    [AvaloniaFact]
    public async Task Loading_shows_the_computers_engines_ollama_recommended()
    {
        var (model, _) = Loaded();

        await model.Load();

        Assert.Equal(["ollama", "claude", "codex"], model.Engines.Select(r => r.Id));
        var ollama = model.Engines.Single(r => r.Id == "ollama");
        Assert.True(ollama.Recommended);
        Assert.True(ollama.Selected);
        var codex = model.Engines.Single(r => r.Id == "codex");
        Assert.True(codex.ShowSignIn);
        Assert.Equal("Sign in first.", codex.About);
        // ask == notes on the library: the select starts on "Same as notes".
        Assert.Equal(AiSetupModel.SameAsNotes, model.SelectedAsk);
        Assert.Equal("Same as notes", model.SelectedAskName);
    }

    [AvaloniaFact]
    public async Task Picking_a_row_moves_the_radio_without_posting_anything()
    {
        var (model, lib) = Loaded();
        await model.Load();

        model.Engines.Single(r => r.Id == "claude").SelectCommand.Execute(null);

        Assert.Equal("claude", model.SelectedNotes);
        Assert.True(model.Engines.Single(r => r.Id == "claude").Selected);
        Assert.False(model.Engines.Single(r => r.Id == "ollama").Selected);
        Assert.Empty(lib.DefaultsCalls);
    }

    [AvaloniaFact]
    public async Task Signing_in_from_the_setup_row_calls_the_engine()
    {
        var (model, lib) = Loaded();
        await model.Load();

        await model.Engines.Single(r => r.Id == "codex").SignInCommand.ExecuteAsync(null);

        Assert.Contains("sign-in:codex", lib.Calls);
        Assert.False(model.Engines.Single(r => r.Id == "codex").ShowSignIn);
    }

    [AvaloniaFact]
    public async Task Continue_saves_notes_and_lets_ask_follow_it_when_left_as_same()
    {
        var (model, lib) = Loaded();
        await model.Load();
        model.Engines.Single(r => r.Id == "claude").SelectCommand.Execute(null);

        bool ok = await model.SaveAsync();

        Assert.True(ok);
        Assert.Equal(("claude", "claude", (bool?)null), (lib.DefaultsCalls[0].Notes, lib.DefaultsCalls[0].Ask, lib.DefaultsCalls[0].Fallback));
    }

    [AvaloniaFact]
    public async Task Continue_saves_a_picked_ask_engine_on_its_own()
    {
        var (model, lib) = Loaded();
        await model.Load();
        model.SelectedAsk = "codex";

        await model.SaveAsync();

        Assert.Equal("codex", lib.DefaultsCalls[0].Ask);
    }

    [AvaloniaFact]
    public async Task An_older_library_refuses_to_save()
    {
        var model = new AiSetupModel(new FakeAiLibrary { Overview = null });

        bool ok = await model.SaveAsync();

        Assert.False(ok);
        Assert.True(model.OlderLibrary);
    }
}
