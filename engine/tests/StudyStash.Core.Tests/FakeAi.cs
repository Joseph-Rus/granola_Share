using System.Runtime.CompilerServices;
using StudyStash.Core.Ai;

namespace StudyStash.Core.Tests;

/// <summary>
/// Builds an <see cref="EngineChecks"/> from just the pieces a test cares about; everything else answers "nothing
/// here, nothing installed", so no test ever touches a real account, a real terminal, or Ollama.
/// </summary>
public sealed class FakeChecks
{
    readonly Dictionary<string, string> which = [];
    readonly HashSet<string> files = [];
    readonly Dictionary<string, string> env = [];
    bool ollamaInstalled;
    List<(string Name, double SizeGb)>? ollamaModels;
    bool startsOk = true;
    (bool Ok, string Why) pullResult = (true, "");
    DateTime clock = DateTime.Now;
    Func<string, string, string, string>? signIn;

    public FakeChecks Installed(params string[] binaries)
    {
        foreach (string b in binaries) which[b] = "/usr/bin/" + b;
        return this;
    }

    public FakeChecks HasFile(string path)
    {
        files.Add(path);
        return this;
    }

    public FakeChecks HasEnv(string name, string value)
    {
        env[name] = value;
        return this;
    }

    /// <summary>Ollama installed; <paramref name="models"/> null means it isn't running.</summary>
    public FakeChecks Ollama(bool installed = true, List<(string Name, double SizeGb)>? models = null)
    {
        ollamaInstalled = installed;
        ollamaModels = models;
        return this;
    }

    public FakeChecks StartsOllama(bool ok)
    {
        startsOk = ok;
        return this;
    }

    public FakeChecks Pulls(bool ok, string why = "")
    {
        pullResult = (ok, why);
        return this;
    }

    public FakeChecks At(DateTime now)
    {
        clock = now;
        return this;
    }

    public FakeChecks SignsInWith(Func<string, string, string, string> f)
    {
        signIn = f;
        return this;
    }

    /// <summary>A fresh, independent snapshot of what's been set up so far.</summary>
    public EngineChecks Build()
    {
        var whichNow = new Dictionary<string, string>(which);
        var filesNow = new HashSet<string>(files);
        var envNow = new Dictionary<string, string>(env);
        bool installedNow = ollamaInstalled;
        var modelsNow = ollamaModels;
        bool startsNow = startsOk;
        var pullNow = pullResult;
        var clockNow = clock;
        var signInNow = signIn;
        return new EngineChecks
        {
            Which = b => whichNow.GetValueOrDefault(b),
            FileExists = filesNow.Contains,
            Env = n => envNow.GetValueOrDefault(n),
            OllamaInstalled = () => installedNow,
            OllamaModels = _ => Task.FromResult(modelsNow),
            StartOllama = _ => Task.FromResult(startsNow),
            PullModel = (_, _, progress, _) =>
            {
                progress?.Invoke(pullNow.Ok ? 10 : 3, 10);
                return Task.FromResult(pullNow);
            },
            OpenSignIn = signInNow ?? ((home, terminal, id) => $"Opened a fake terminal to sign in to {id}."),
            Now = () => clockNow,
        };
    }
}

/// <summary>
/// A stand-in AI: it never runs a real command. A test scripts what it says (<see cref="Script"/>), makes it hang
/// until cancelled (<see cref="Hang"/>, for timeout/fallback tests that must still honour the token), or has it
/// throw outright (<see cref="Throws"/>), then hands it to <see cref="AiJobs.Providers"/> by id.
/// </summary>
public sealed class ScriptedAi(string id, string name = "") : AiProvider
{
    public override string Id { get; } = id;
    public override string Name { get; } = name.Length > 0 ? name : id;
    public override string Binary => Id;
    public override string Site => "https://example.test/" + Id;

    public bool IsAvailable { get; set; } = true;
    public override bool Available() => IsAvailable;

    /// <summary>What it says, in order. Defaults to one plain answer.</summary>
    public List<AiEvent> Script { get; set; } = [new("final", "ready")];
    /// <summary>Hangs until the run's own CancellationToken fires, then throws — for a timeout that must fall back.</summary>
    public bool Hang { get; set; }
    /// <summary>Thrown as soon as it's asked to run, instead of yielding anything.</summary>
    public Exception? Throws { get; set; }

    public override List<string> Command(AiRequest req, bool stream) => [];
    public override IEnumerable<AiEvent> Parse(string line) => [];

    public override async IAsyncEnumerable<AiEvent> RunAsync(AiRequest req, bool stream = true,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Throws is not null) throw Throws;
        if (Hang) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        foreach (var e in Script) yield return e;
    }
}
