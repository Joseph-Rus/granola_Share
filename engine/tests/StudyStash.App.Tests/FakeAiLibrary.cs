using StudyStash.Core;
using StudyStash.Core.Ai;

namespace StudyStash.App.Tests;

/// <summary>
/// A library's AI, scripted: a view model test sets <see cref="Overview"/> (and the other fields below) instead of
/// running a real library, and reads <see cref="Calls"/> to check which endpoint a command hit. Every action, by
/// default, applies its obvious effect to <see cref="Overview"/> and hands it straight back — set the matching
/// <c>On...</c> hook to script something else (a timeout, a refusal, an older library's null).
/// </summary>
public sealed class FakeAiLibrary : IAiLibrary
{
    public AiOverview? Overview { get; set; }
    public List<string> Calls { get; } = [];
    public List<(string? Notes, string? Ask, bool? Fallback)> DefaultsCalls { get; } = [];
    public List<AskRequest> AskRequests { get; } = [];

    public Func<string, AiSaid?>? OnStart { get; set; }
    public Func<string, AiSaid?>? OnDownload { get; set; }
    public Func<string, AiSaid?>? OnSignIn { get; set; }
    public Func<string, string, AiSaid?>? OnCheck { get; set; }
    public Func<AiOverview?>? OnEngines { get; set; }
    public Func<string?, string?, bool?, AiOverview?>? OnDefaults { get; set; }
    public Func<AskRequest, AskReply?>? OnAsk { get; set; }
    public Func<string, RewriteInfo?>? OnRewrite { get; set; }

    static EngineInfo Row(AiOverview o, string id) => o.Engines.First(e => e.Id == id);

    public Task<AiOverview?> EnginesAsync()
    {
        Calls.Add("engines");
        return Task.FromResult(OnEngines is not null ? OnEngines() : Overview);
    }

    public Task<AiOverview?> DefaultsAsync(string? notes = null, string? ask = null, bool? fallback = null)
    {
        Calls.Add("defaults");
        DefaultsCalls.Add((notes, ask, fallback));
        if (OnDefaults is not null) return Task.FromResult(OnDefaults(notes, ask, fallback));
        if (Overview is null) return Task.FromResult<AiOverview?>(null);
        Overview = Overview with { Notes = notes ?? Overview.Notes, Ask = ask ?? Overview.Ask, Fallback = fallback ?? Overview.Fallback };
        return Task.FromResult<AiOverview?>(Overview);
    }

    public Task<AiSaid?> StartAsync(string engine)
    {
        Calls.Add($"start:{engine}");
        if (OnStart is not null) return Task.FromResult(OnStart(engine));
        if (Overview is null) return Task.FromResult<AiSaid?>(null);
        Overview = Overview with { Engines = [.. Overview.Engines.Select(e => e.Id == engine ? e with { State = "ready" } : e)] };
        return Task.FromResult<AiSaid?>(new AiSaid($"Started {Row(Overview, engine).Name}.", Overview));
    }

    public Task<AiSaid?> DownloadAsync(string engine)
    {
        Calls.Add($"download:{engine}");
        if (OnDownload is not null) return Task.FromResult(OnDownload(engine));
        if (Overview is null) return Task.FromResult<AiSaid?>(null);
        Overview = Overview with { Engines = [.. Overview.Engines.Select(e => e.Id == engine ? e with { State = "ready" } : e)] };
        return Task.FromResult<AiSaid?>(new AiSaid($"Downloading {Row(Overview, engine).Name}'s model.", Overview));
    }

    public Task<AiSaid?> SignInAsync(string engine)
    {
        Calls.Add($"sign-in:{engine}");
        if (OnSignIn is not null) return Task.FromResult(OnSignIn(engine));
        if (Overview is null) return Task.FromResult<AiSaid?>(null);
        Overview = Overview with { Engines = [.. Overview.Engines.Select(e => e.Id == engine ? e with { State = "ready" } : e)] };
        return Task.FromResult<AiSaid?>(new AiSaid($"Opened a terminal to sign in to {Row(Overview, engine).Name}.", Overview));
    }

    public Task<AiSaid?> CheckAsync(string engine, string model = "")
    {
        Calls.Add($"check:{engine}");
        if (OnCheck is not null) return Task.FromResult(OnCheck(engine, model));
        if (Overview is null) return Task.FromResult<AiSaid?>(null);
        Overview = Overview with { Engines = [.. Overview.Engines.Select(e => e.Id == engine ? e with { State = "ready" } : e)] };
        return Task.FromResult<AiSaid?>(new AiSaid("Works.", Overview));
    }

    public Task<AiOverview?> ModelAsync(string engine, string model)
    {
        Calls.Add($"model:{engine}:{model}");
        if (Overview is null) return Task.FromResult<AiOverview?>(null);
        Overview = Overview with { Engines = [.. Overview.Engines.Select(e => e.Id == engine ? e with { Model = model } : e)] };
        return Task.FromResult<AiOverview?>(Overview);
    }

    public Task<AiOverview?> DismissAsync(string problemId)
    {
        Calls.Add($"dismiss:{problemId}");
        if (Overview is null) return Task.FromResult<AiOverview?>(null);
        Overview = Overview with { Problems = [.. Overview.Problems.Where(p => p.Id != problemId)] };
        return Task.FromResult<AiOverview?>(Overview);
    }

    public Task<AskReply?> AskAsync(AskRequest request)
    {
        Calls.Add("ask");
        AskRequests.Add(request);
        return Task.FromResult(OnAsk?.Invoke(request));
    }

    public Task<RewriteInfo?> RewriteAsync(string lecture)
    {
        Calls.Add($"rewrite:{lecture}");
        return Task.FromResult(OnRewrite?.Invoke(lecture));
    }

    public Task<RewriteInfo?> RewriteStartAsync(string lecture, string engine)
    {
        Calls.Add($"rewrite-start:{lecture}:{engine}");
        return Task.FromResult<RewriteInfo?>(new RewriteInfo(lecture, "working") { Engine = engine });
    }

    public Task<RewriteInfo?> RewriteCancelAsync(string lecture)
    {
        Calls.Add($"rewrite-cancel:{lecture}");
        return Task.FromResult<RewriteInfo?>(new RewriteInfo(lecture, "cancelled"));
    }

    public Task<RewriteInfo?> RewriteKeepAsync(string lecture)
    {
        Calls.Add($"rewrite-keep:{lecture}");
        return Task.FromResult<RewriteInfo?>(new RewriteInfo(lecture, "none"));
    }

    public Task<RewriteInfo?> RewriteUseAsync(string lecture)
    {
        Calls.Add($"rewrite-use:{lecture}");
        return Task.FromResult<RewriteInfo?>(new RewriteInfo(lecture, "none"));
    }
}

/// <summary>Sample AI engine data for view model tests: Ollama and Claude Code ready, Codex not signed in, Gemini
/// not installed — the same mix the AI screens' shots draw.</summary>
public static class AiTestData
{
    public static AiOverview MixedOverview() => new(
        Engines:
        [
            new EngineInfo("ollama", "Ollama", "ready") { Installed = true, Model = "qwen3:30b" },
            new EngineInfo("claude", "Claude Code", "ready") { Installed = true },
            new EngineInfo("codex", "Codex", "not_signed_in") { Installed = true },
            new EngineInfo("gemini", "Gemini", "not_installed") { Site = "https://gemini.google.com" },
        ],
        Notes: "ollama", Ask: "claude", Fallback: true, Problems: []);
}
