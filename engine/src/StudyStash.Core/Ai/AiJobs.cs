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
        var provider = AiProviders.Get(choice.Provider, ollamaHost);
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

    /// <summary>Writing study notes.</summary>
    public Task<string> SummarizeAsync(Meeting m, Config cfg) => Settings.Local("notes")
        ? Core.Summarize.SummarizeTranscriptAsync(m, cfg)
        // Other models read a whole lecture at once: tell the splitter their context is large.
        : Core.Summarize.SummarizeTranscriptAsync(m, cfg,
            chat: (_, _, prompt, _) => AnswerAsync("notes", prompt),
            show: (_, _) => Task.FromResult<int?>(200_000));

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
        if (c.Provider == "ollama") return job == "notes" ? cfg.EffectiveSummaryModel : cfg.OllamaModel;
        return AiProviders.Get(c.Provider).Name + (c.Model.Length > 0 ? " " + c.Model : "");
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
        var provider = AiProviders.Get(choice.Provider, ollamaHost);
        return provider.RunAsync(new AiRequest(prompt, cwd)
        {
            Model = choice.Model, Write = write, Tools = McpCommand.Count > 0, McpCommand = McpCommand, System = system,
            Session = session, ReadDirs = readDirs ?? [],
        }, ct: ct);
    }

    /// <summary>Whether agent work can run: its AI is installed (a local model needs Codex to act as an agent).</summary>
    public (bool Ok, string Why) AgentReady()
    {
        var c = Settings.For("agent");
        var p = AiProviders.Get(c.Provider, ollamaHost);
        if (!p.Available()) return (false, $"{p.Name} isn't installed on the library's computer.");
        if (p is OllamaProvider && !OllamaProvider.Agentic()) return (false, "A local model needs the Codex CLI to explore on its own (brew install codex).");
        return (true, "");
    }

    /// <summary>Have a provider say one word, so a person knows it's signed in before relying on it. Kept in ai.json.</summary>
    public async Task<(bool Ok, string Why)> TestAsync(string providerId, string model = "")
    {
        var provider = AiProviders.Get(providerId, ollamaHost);
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
}
