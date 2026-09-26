using System.Text.Json.Nodes;

namespace StudyStash.Core.Ai;

/// <summary>
/// The library's work, done by the AI picked for it: the functions the pipeline, sorting and Ask call. ai.json is
/// read on every call, so a change in Settings applies to the next lecture. Work that stays on Ollama goes through
/// the engine's own Ollama calls (their context sizing and loop guards).
/// </summary>
public sealed class AiJobs(string home, Func<string>? ollamaHost = null)
{
    AiSettings Settings => AiSettings.Load(home);

    /// <summary>Every engine by id, real by default; a test sets this so <em>every</em> job — Ollama included —
    /// goes through a fake, never a real account or <c>localhost:11434</c>.</summary>
    public Func<string, AiProvider>? Providers { get; init; }

    /// <summary>How the library checks whether each engine is ready, without ever contacting an account.</summary>
    public EngineChecks Checks { get; init; } = EngineChecks.Machine;

    /// <summary>How long <see cref="AskAsync"/> waits for the picked engine before falling back to Ollama.</summary>
    public TimeSpan AskTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>A model Ollama is pulling right now, for <see cref="AiOverview.Pulling"/>. Null once it's done.</summary>
    public PullInfo? Pulling { get; private set; }

    AiProvider Provider(string id) => Providers?.Invoke(id) ?? AiProviders.Get(id, ollamaHost);

    /// <summary>An empty folder for answers that need no files: nothing there to read or change.</summary>
    string Scratch()
    {
        string dir = Path.Combine(home, "ai-work");
        Directory.CreateDirectory(dir);
        return dir;
    }

    async Task<string> AnswerAsync(string job, string prompt, CancellationToken ct = default)
    {
        var choice = Settings.For(job);
        var provider = Provider(choice.Provider);
        var result = await provider.CompleteAsync(new AiRequest(prompt, Scratch()) { Model = choice.Model, Timeout = TimeSpan.FromMinutes(15) }, ct);
        if (!result.Ok) throw new InvalidOperationException($"{provider.Name}: {result.Text}");
        return result.Text;
    }

    /// <summary>The same prompt, told to answer with JSON only; then the JSON object in what came back.</summary>
    async Task<string> JsonAnswerAsync(string job, string prompt, JsonObject schema)
    {
        string text = await AnswerAsync(job, prompt + "\n\nAnswer with only a JSON object that fits this JSON schema, and nothing else:\n"
            + schema.ToJsonString());
        return FirstObject(text) ?? throw new InvalidDataException("the answer wasn't JSON");
    }

    /// <summary>Answering with a specific engine, not the one ai.json picks for a job — the "Answer with" menu's
    /// choice, and this class's own fallback to Ollama in <see cref="AskAsync"/>.</summary>
    async Task<string> AnswerWithAsync(string engine, string model, string prompt, CancellationToken ct)
    {
        var provider = Provider(engine);
        var result = await provider.CompleteAsync(new AiRequest(prompt, Scratch()) { Model = model, Timeout = TimeSpan.FromMinutes(15) }, ct);
        if (!result.Ok) throw new InvalidOperationException(result.Text);
        return result.Text;
    }

    async Task<string> JsonAnswerWithAsync(string engine, string model, string prompt, JsonObject schema, CancellationToken ct)
    {
        string text = await AnswerWithAsync(engine, model, prompt + "\n\nAnswer with only a JSON object that fits this JSON schema, and nothing else:\n"
            + schema.ToJsonString(), ct);
        return FirstObject(text) ?? throw new InvalidDataException("the answer wasn't JSON");
    }

    /// <summary>Ask a question with one named engine, ignoring ai.json's own "ask" pick.</summary>
    public LibraryReader.AskChatFn AskWith(string engine, string model = "", CancellationToken ct = default) =>
        (prompt, schema) => JsonAnswerWithAsync(engine, model, prompt, schema, ct);

    /// <summary>A JSON answer from the AI picked for <paramref name="job"/>, for work that returns a plan.</summary>
    public Task<string> PlanAsync(string job, string prompt, JsonObject schema) => JsonAnswerAsync(job, prompt, schema);

    /// <summary>The first whole {...} in a text (models like to wrap JSON in a code fence or a sentence).</summary>
    public static string? FirstObject(string text)
    {
        int start = text.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0;
            bool quoted = false, escaped = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    string candidate = text[start..(i + 1)];
                    try
                    {
                        if (JsonNode.Parse(candidate) is JsonObject) return candidate;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                    }
                    break;
                }
            }
            start = text.IndexOf('{', start + 1);
        }
        return null;
    }

    /// <summary>Whether "notes" should really write with Ollama instead of ai.json's pick: either that's the pick
    /// already, or the pick is known unusable and Fallback is on. <see cref="SummarizeAsync"/> and
    /// <see cref="Describe"/> both ask this, so a note never claims a CLI wrote what Ollama actually wrote — a
    /// pre-flight check only, before the run starts; a job already under way never changes engines mid-run (the
    /// pipeline's own retry does that, not this).</summary>
    bool NotesOnOllama(AiSettings settings) =>
        settings.Local("notes") || (settings.Fallback && Engines.KnownUnusableWhy(settings.For("notes").Provider, settings, Checks) is not null);

    /// <summary>Writing study notes.</summary>
    public Task<string> SummarizeAsync(Meeting m, Config cfg) => NotesOnOllama(Settings)
        ? Core.Summarize.SummarizeTranscriptAsync(m, cfg)
        // Other models read a whole lecture at once: tell the splitter their context is large.
        : Core.Summarize.SummarizeTranscriptAsync(m, cfg,
            chat: (_, _, prompt, _) => AnswerAsync("notes", prompt),
            show: (_, _) => Task.FromResult<int?>(200_000));

    /// <summary>Rewrite a lecture's notes with a chosen engine — the "Rewrite notes with" menu's own choice, not
    /// ai.json's "notes" pick. Real Ollama with no test hooks keeps <see cref="SummarizeAsync"/>'s own direct path;
    /// every other engine (and a faked Ollama, in tests) goes through the same chat wrapper, but with
    /// <paramref name="engine"/> itself, and <paramref name="ct"/> in the closure since a chat call takes none of
    /// its own. <paramref name="progress"/> hears a running count as each chat call finishes, against a first
    /// estimate of how many parts the transcript needs (long lectures may need a few more, to merge them). Cancelling
    /// a real Ollama run is best-effort only — Ollama's own call has no way to stop mid-generation — so the caller
    /// marks that job cancelled itself and drops whatever this returns.</summary>
    public Task<string> WriteNotesAsync(Meeting m, Config cfg, string engine, Action<int, int>? progress, CancellationToken ct)
    {
        if (engine == "ollama" && Providers is null)
            return Core.Summarize.SummarizeTranscriptAsync(m, cfg);
        string model = Settings.Models.GetValueOrDefault(engine, "");
        const int ctx = 200_000; // other models read a whole lecture at once: tell the splitter their context is large
        string text = Py.Strip(TimedText.Plain(m.Transcript));
        int budget = Core.Summarize.TranscriptBudget(ctx);
        int parts = text.Length <= budget ? 1 : Core.Summarize.SplitTranscript(text, budget).Count;
        int done = 0;
        async Task<string> ChatAsync(Config c, string mdl, string prompt, int numCtx)
        {
            string result = await AnswerWithAsync(engine, model, prompt, ct);
            progress?.Invoke(++done, Math.Max(done, parts));
            return result;
        }
        return Core.Summarize.SummarizeTranscriptAsync(m, cfg, chat: ChatAsync, show: (_, _) => Task.FromResult<int?>(ctx));
    }

    /// <summary>The name of what wrote something with a specific engine, not ai.json's own pick for a job — the
    /// rewrite job's choice, kept as the note's <c>summary_model</c> so <see cref="Engines.WhoWrote"/> maps it back
    /// to a display name later, the same way it does for the notes ai.json actually picked.</summary>
    public string DescribeChoice(string engine, Config cfg) => engine == "ollama"
        ? cfg.EffectiveSummaryModel
        : Provider(engine).Name + (Settings.Models.GetValueOrDefault(engine, "") is { Length: > 0 } model ? " " + model : "");

    /// <summary>Sorting into classes.</summary>
    public Task<string> SortAsync(Config cfg, string prompt, JsonObject schema) => Settings.Local("sort")
        ? Classify.OllamaChatAsync(cfg, prompt, schema)
        : JsonAnswerAsync("sort", prompt, schema);

    /// <summary>Asking your notes.</summary>
    public LibraryReader.AskChatFn Ask(Func<Config> cfg) => (prompt, schema) => Settings.Local("ask")
        ? LibraryReader.OllamaAskAsync(cfg(), prompt, schema)
        : JsonAnswerAsync("ask", prompt, schema);

    /// <summary>The name of what does a job, for the log ("Claude sonnet", "qwen3:8b").</summary>
    public string Describe(string job, Config cfg)
    {
        var c = Settings.For(job);
        if (c.Provider == "ollama" || (job == "notes" && NotesOnOllama(Settings)))
            return job == "notes" ? cfg.EffectiveSummaryModel : cfg.OllamaModel;
        return Provider(c.Provider).Name + (c.Model.Length > 0 ? " " + c.Model : "");
    }

    /// <summary>The command that starts this engine's MCP server (the Canvas and library tools), for agents.</summary>
    public IReadOnlyList<string> McpCommand { get; init; } =
        Environment.ProcessPath is { } exe && !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [exe, "--home", home, "mcp"] : [];

    /// <summary>
    /// Agent work (the course scout, a chat that may change notes): the AI picked for <c>agent</c>, working in
    /// <paramref name="cwd"/> with the library's tools. <paramref name="write"/> lets it change files there.
    /// </summary>
    public IAsyncEnumerable<AiEvent> AgentAsync(string prompt, string cwd, bool write, string system = "", string session = "",
        IReadOnlyList<string>? readDirs = null, string job = "agent", CancellationToken ct = default)
    {
        var choice = Settings.For(job);
        var provider = Provider(choice.Provider);
        return provider.RunAsync(new AiRequest(prompt, cwd)
        {
            Model = choice.Model, Write = write, Tools = McpCommand.Count > 0, McpCommand = McpCommand, System = system,
            Session = session, ReadDirs = readDirs ?? [],
        }, ct: ct);
    }

    /// <summary>The name of the AI that does agent work ("Claude").</summary>
    public string AgentName => Provider(Settings.For("agent").Provider).Name;

    /// <summary>Whether agent work can run: its AI is installed (a local model needs Codex to act as an agent).</summary>
    public (bool Ok, string Why) AgentReady()
    {
        var c = Settings.For("agent");
        var p = Provider(c.Provider);
        if (!p.Available()) return (false, $"{p.Name} isn't installed on the library's computer.");
        if (p is OllamaProvider && !OllamaProvider.Agentic()) return (false, "A local model needs the Codex CLI to explore on its own (brew install codex).");
        return (true, "");
    }

    /// <summary>Have a provider say one word, so a person knows it's signed in before relying on it. Kept in ai.json.</summary>
    public async Task<(bool Ok, string Why)> TestAsync(string providerId, string model = "")
    {
        var provider = Provider(providerId);
        var r = await provider.CompleteAsync(new AiRequest("Reply with the single word: ready", Scratch())
        {
            Model = model, Timeout = TimeSpan.FromMinutes(3),
        });
        bool ok = r.Ok && r.Text.Contains("ready", StringComparison.OrdinalIgnoreCase);
        string why = ok ? "" : r.Ok ? $"it answered \"{Py.Head(r.Text, 80)}\"" : r.Text;
        var s = Settings;
        s.Tests[provider.Id] = ok ? "works" : why;
        s.Save(home);
        return (ok, why);
    }

    /// <summary>What an engine's run just proved: success clears any usage limit and marks it working; an
    /// auth-looking error is kept so the engines pane shows "Not signed in"; a limit-looking error is kept as an
    /// until time. Anything else (a one-off hiccup) isn't recorded, so it doesn't outlive the moment.</summary>
    public void Record(string id, bool ok, string error)
    {
        var s = Settings;
        if (ok)
        {
            s.Tests[id] = "works";
            s.Limits.Remove(id);
        }
        else if (Engines.LooksLikeLimit(error))
        {
            s.Limits[id] = Engines.UntilFrom(error, Checks.Now()).ToString("o");
        }
        else if (Engines.LooksLikeAuth(error))
        {
            s.Tests[id] = error;
        }
        else return; // a plain failure: not lasting enough to change the engine's remembered state
        s.Save(home);
    }

    /// <summary>Download <paramref name="model"/> through Ollama, in the background, reporting progress through
    /// <see cref="Pulling"/> until it's done (then null) or it fails (then <see cref="PullInfo.Why"/> says why).</summary>
    public Task DownloadAsync(string model, string host, CancellationToken ct = default)
    {
        if (Pulling is { Why.Length: 0 } already && already.Model == model) return Task.CompletedTask;
        Pulling = new PullInfo(model, 0, "");
        return Task.Run(async () =>
        {
            try
            {
                var (ok, why) = await Checks.PullModel(model, host,
                    (done, total) => Pulling = new PullInfo(model, total > 0 ? (double)done / total : 0, ""), ct);
                Pulling = ok ? null : new PullInfo(model, 0, why);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                Pulling = new PullInfo(model, 0, e.Message);
            }
        }, ct);
    }

    /// <summary>
    /// Answer a question with a chosen engine (the request's, or ai.json's "ask" pick): when it's known unusable
    /// (not installed, not signed in, hit its limit) and <see cref="AiSettings.Fallback"/> is on, Ollama answers up
    /// front; otherwise it's tried for real, under <see cref="AskTimeout"/>, and only falls back on a timeout or an
    /// error (never mid-run: that's the pipeline's own job). Every engine that's actually run has its result
    /// recorded (<see cref="Record"/>).
    /// </summary>
    public async Task<AskReply> AskAsync(AskRequest request, LibraryReader reader, Config cfg, CancellationToken ct = default)
    {
        var settings = Settings;
        string asked = request.Engine is { Length: > 0 } e ? e : settings.For("ask").Provider;
        string engine = asked;
        bool fellBack = false;
        string why = "";

        if (settings.Fallback && Engines.KnownUnusableWhy(asked, settings, Checks) is { } preflight)
        {
            engine = "ollama";
            fellBack = true;
            why = preflight;
        }
        if (engine == "ollama" && !cfg.OllamaEnabled && Providers is null)
            throw new InvalidOperationException("Asking needs an engine: turn one on in AI engines.");

        async Task<JsonObject> RunAsync(string who, CancellationToken token)
        {
            string model = settings.Models.GetValueOrDefault(who, "");
            var answer = await reader.AskAsync(request.Question, request.Lecture, request.Class, request.Live,
                request.LiveTitle is { Length: > 0 } t ? t : "This lecture", AskWith(who, model, token));
            Record(who, true, "");
            return answer;
        }

        JsonObject result;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(AskTimeout);
        try
        {
            result = await RunAsync(engine, cts.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or HttpRequestException or TimeoutException or OperationCanceledException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw; // the caller gave up; not ours to paper over
            bool timedOut = ex is OperationCanceledException;
            Record(engine, false, timedOut ? "timed out" : ex.Message);
            if (engine == "ollama" || !settings.Fallback) throw;
            why = timedOut ? $"{Engines.Name(engine)} didn't respond in time." : $"{Engines.Name(engine)} didn't answer: {ex.Message}";
            fellBack = true;
            engine = "ollama";
            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(AskTimeout);
            result = await RunAsync(engine, cts2.Token);
        }

        var sources = (result["sources"] as JsonArray ?? []).Select(s => new AskSource(
            Py.AsString(s?["id"]), Py.AsString(s?["title"]) ?? "", Py.AsString(s?["class"]), Py.AsString(s?["date"]),
            s?["at"] is JsonValue av && av.TryGetValue(out double atv) ? atv : null, Py.AsString(s?["section"]) ?? "")).ToList();
        return new AskReply(Py.AsString(result["answer"]) ?? "", sources, engine, Engines.Name(engine))
        {
            Asked = asked, AskedName = Engines.Name(asked), FellBack = fellBack, Why = why,
        };
    }
}
