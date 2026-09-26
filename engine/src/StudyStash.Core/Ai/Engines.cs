using System.Globalization;
using System.Text.RegularExpressions;

namespace StudyStash.Core.Ai;

/// <summary>
/// What the library's laptop and web app call to find out whether each AI is ready, without ever asking the AI
/// itself: a probe is a plain function so tests can fake every one of them and never touch a real account, a real
/// terminal, or <c>localhost:11434</c>.
/// </summary>
public sealed record EngineChecks
{
    /// <summary>Where a command lives, or null when it isn't on this computer (<see cref="AiProvider.Which"/>).</summary>
    public required Func<string, string?> Which { get; init; }
    /// <summary>Whether a file exists (a credential file's existence is a hint, never its contents).</summary>
    public required Func<string, bool> FileExists { get; init; }
    /// <summary>An environment variable's value, or null.</summary>
    public required Func<string, string?> Env { get; init; }
    public required Func<bool> OllamaInstalled { get; init; }
    /// <summary>Installed models, biggest first, or null when Ollama isn't answering.</summary>
    public required Func<string, Task<List<(string Name, double SizeGb)>?>> OllamaModels { get; init; }
    /// <summary>Start Ollama and wait for it to answer. True once it does.</summary>
    public required Func<string, Task<bool>> StartOllama { get; init; }
    /// <summary>Pull a model through Ollama's API, reporting bytes done and total as it goes.</summary>
    public required Func<string, string, Action<long, long>?, CancellationToken, Task<(bool Ok, string Why)>> PullModel { get; init; }
    /// <summary>Open a terminal on this computer running the sign-in command for a provider. What it said, or
    /// throws saying why not.</summary>
    public required Func<string, string, string, string> OpenSignIn { get; init; }
    public required Func<DateTime> Now { get; init; }

    /// <summary>The real probes: this computer, its files, its environment, its Ollama, its terminals.</summary>
    public static readonly EngineChecks Machine = new()
    {
        Which = AiProvider.Which,
        FileExists = File.Exists,
        Env = Environment.GetEnvironmentVariable,
        OllamaInstalled = () => Ollama.Installed(),
        OllamaModels = host => Ollama.ListModelsAsync(host),
        StartOllama = host => Ollama.StartAsync(host),
        PullModel = (model, host, progress, ct) => Ollama.PullAsync(model, host, progress, ct: ct),
        OpenSignIn = (home, terminal, id) =>
        {
            var (exe, args) = Engines.SignInCommand(id);
            return $"Opened {Terminal.RunCommand(home, terminal, exe, args)} to sign in to {Engines.Name(id)}.";
        },
        Now = () => DateTime.Now,
    };

    /// <summary>Nothing is installed and no action does anything: for tests that don't care about AI at all.</summary>
    public static readonly EngineChecks Off = new()
    {
        Which = _ => null,
        FileExists = _ => false,
        Env = _ => null,
        OllamaInstalled = () => false,
        OllamaModels = _ => Task.FromResult<List<(string, double)>?>(null),
        StartOllama = _ => Task.FromResult(false),
        PullModel = (_, _, _, _) => Task.FromResult((false, "nothing here downloads a model")),
        OpenSignIn = (_, _, id) => throw new InvalidOperationException($"{Engines.Name(id)} isn't installed on this computer."),
        Now = () => DateTime.Now,
    };
}

/// <summary>
/// The four AIs the library can pick from, in the order the design shows them, and whether each is ready right now
/// — installed, signed in, running, its model at hand — worked out from probes that never call an account.
/// </summary>
public static class Engines
{
    public static readonly string[] Order = ["ollama", "claude", "codex", "gemini"];

    public static string Name(string id) => id switch
    {
        "ollama" => "Ollama",
        "claude" => "Claude Code",
        "codex" => "Codex",
        "gemini" => "Gemini",
        _ => id,
    };

    /// <summary>Which engine wrote a lecture's notes, from the model name the pipeline recorded
    /// (<see cref="AiJobs.Describe"/>: an Ollama model like "qwen3:30b", or "Claude sonnet", "ChatGPT", "Gemini …").</summary>
    public static string WhoWrote(string notesModel) => notesModel switch
    {
        _ when notesModel.StartsWith("Claude", StringComparison.Ordinal) => "Claude Code",
        _ when notesModel.StartsWith("ChatGPT", StringComparison.Ordinal) || notesModel.StartsWith("Codex", StringComparison.Ordinal) => "Codex",
        _ when notesModel.StartsWith("Gemini", StringComparison.Ordinal) => "Gemini",
        { Length: > 0 } => "Ollama",
        _ => "",
    };

    /// <summary>The command a terminal runs to sign in ("claude", "codex login", "agy").</summary>
    public static (string Exe, string[] Args) SignInCommand(string id) => id switch
    {
        "codex" => ("codex", ["login"]),
        "gemini" => ("agy", []),
        _ => ("claude", []),
    };

    public static string SignInWords(string id)
    {
        var (exe, args) = SignInCommand(id);
        return args.Length == 0 ? exe : exe + " " + string.Join(' ', args);
    }

    // --- reading an error for what it means, never for who's right -------------------------------------------

    static readonly Regex AuthLike = new(@"not (logged|signed) in|log ?in|sign ?in|authenticat|unauthori[sz]ed|\b401\b|api key|credential",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex LimitLike = new(@"usage limit|rate limit|quota|limit reached|too many requests|\b429\b|resets? (at|in)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex InDuration = new(@"in (\d+)\s*(hour|minute)s?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex AtTime = new(@"at (\d{1,2})(?::(\d{2}))?\s*([ap]m)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksLikeAuth(string error) => AuthLike.IsMatch(error);
    public static bool LooksLikeLimit(string error) => LimitLike.IsMatch(error);

    /// <summary>When a usage limit's error ("resets at 3pm", "at 3:00 PM", "in 2 hours") lasts until: that time
    /// today (tomorrow if it's already past), or an hour from now when nothing in the message says.</summary>
    public static DateTime UntilFrom(string error, DateTime now)
    {
        if (InDuration.Match(error) is { Success: true } d)
        {
            int n = int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture);
            return d.Groups[2].Value.Equals("hour", StringComparison.OrdinalIgnoreCase) ? now.AddHours(n) : now.AddMinutes(n);
        }
        if (AtTime.Match(error) is { Success: true } a)
        {
            int hour = int.Parse(a.Groups[1].Value, CultureInfo.InvariantCulture) % 12;
            int minute = a.Groups[2].Success ? int.Parse(a.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            if (a.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase)) hour += 12;
            var t = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0, now.Kind);
            return t <= now ? t.AddDays(1) : t;
        }
        return now.AddHours(1);
    }

    // --- credential-file hints (existence only: never read what's inside) --------------------------------------

    static string CodexAuthPath(EngineChecks checks) =>
        Path.Combine(checks.Env("CODEX_HOME") is { Length: > 0 } h ? h : Path.Combine(Py.UserHome(), ".codex"), "auth.json");
    static string GeminiCredsPath => Path.Combine(Py.UserHome(), ".gemini", "oauth_creds.json");

    /// <summary>Whether a not-yet-tested CLI looks signed in from files/env alone. Claude keeps its login in the
    /// Keychain on macOS, so its absence proves nothing: it always comes back unchecked, never not-signed-in.</summary>
    static bool? Hinted(string id, EngineChecks checks) => id switch
    {
        "codex" => checks.FileExists(CodexAuthPath(checks)) || checks.Env("OPENAI_API_KEY") is { Length: > 0 },
        "gemini" => checks.Env("GEMINI_API_KEY") is { Length: > 0 } || checks.FileExists(GeminiCredsPath),
        _ => null, // claude
    };

    static bool LimitedNow(string id, AiSettings settings, EngineChecks checks) =>
        settings.Limits.TryGetValue(id, out string? until) && DateTime.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
        && t > checks.Now();

    static (string State, string Until) CliState(string id, AiSettings settings, EngineChecks checks)
    {
        if (checks.Which(AiProviders.Get(id).Binary) is null) return ("not_installed", "");
        if (LimitedNow(id, settings, checks)) return ("limited", settings.Limits[id]);
        string test = settings.Tests.GetValueOrDefault(id, "");
        if (test == "works") return ("ready", "");
        if (test.Length > 0) return (LooksLikeAuth(test) ? "not_signed_in" : "failed", "");
        return (Hinted(id, checks) == false ? "not_signed_in" : "unchecked", "");
    }

    static async Task<EngineInfo> OllamaRowAsync(AiSettings settings, Config cfg, EngineChecks checks)
    {
        bool installed = checks.OllamaInstalled();
        var installedModels = installed ? await checks.OllamaModels(cfg.OllamaHost) : null;
        string state = !installed ? "not_installed"
            : installedModels is null ? "not_running"
            : !Ollama.HasModel(installedModels.Select(m => m.Name).ToList(), cfg.EffectiveSummaryModel) ? "model_missing"
            : "ready";
        var models = (installedModels ?? []).Select(m => new ModelOption(m.Name, $"{m.Name} ({Ollama.SizeLabel(m.SizeGb)})")).ToList();
        return new EngineInfo("ollama", Name("ollama"), state)
        {
            Installed = installed, Model = cfg.EffectiveSummaryModel, Models = models, Site = AiProviders.Get("ollama").Site,
        };
    }

    static EngineInfo CliRow(string id, AiSettings settings, EngineChecks checks)
    {
        var provider = AiProviders.Get(id);
        var (state, until) = CliState(id, settings, checks);
        string test = settings.Tests.GetValueOrDefault(id, "");
        return new EngineInfo(id, Name(id), state)
        {
            Installed = checks.Which(provider.Binary) is not null,
            Model = settings.Models.GetValueOrDefault(id, ""),
            Models = provider.Models.Select(m => new ModelOption(m.Id, m.Label)).ToList(),
            Site = provider.Site,
            Until = until,
            Why = state is "failed" or "not_signed_in" ? test : "",
        };
    }

    /// <summary>Every engine's state, and the problems worth telling the student about right now.</summary>
    public static async Task<AiOverview> StatusAsync(AiSettings settings, Config cfg, EngineChecks checks)
    {
        var engines = new List<EngineInfo> { await OllamaRowAsync(settings, cfg, checks) };
        foreach (string id in Order.Skip(1)) engines.Add(CliRow(id, settings, checks));

        var problems = new List<AiProblemInfo>();
        bool Dismissed(string id) => settings.Dismissed.Contains(id);
        var ollama = engines[0];
        bool ollamaMatters = settings.For("notes").Provider == "ollama" || settings.For("ask").Provider == "ollama" || settings.Fallback;
        if (ollamaMatters && ollama.State == "not_running" && !Dismissed("engine_offline:ollama"))
            problems.Add(new AiProblemInfo("engine_offline:ollama", "engine_offline", "ollama", ollama.Name));
        if (ollamaMatters && ollama.State == "model_missing" && !Dismissed("model_missing:ollama"))
            problems.Add(new AiProblemInfo("model_missing:ollama", "model_missing", "ollama", ollama.Name) { Model = cfg.EffectiveSummaryModel });

        foreach (string job in new[] { "notes", "ask" })
        {
            string id = settings.For(job).Provider;
            if (id == "ollama") continue;
            string pid = $"not_signed_in:{id}";
            if (Dismissed(pid) || problems.Any(p => p.Id == pid)) continue;
            var row = engines.First(e => e.Id == id);
            if (row.State != "not_signed_in") continue;
            problems.Add(new AiProblemInfo(pid, "not_signed_in", id, row.Name) { FallbackTo = settings.Fallback ? Name("ollama") : "" });
        }

        foreach (var (id, until) in settings.Limits)
        {
            if (!DateTime.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) || t <= checks.Now()) continue;
            string pid = $"usage_limit:{id}:{until}";
            if (Dismissed(pid)) continue;
            problems.Add(new AiProblemInfo(pid, "usage_limit", id, Name(id)) { Until = until });
        }

        return new AiOverview(engines, settings.For("notes").Provider, settings.For("ask").Provider, settings.Fallback, problems);
    }
}
