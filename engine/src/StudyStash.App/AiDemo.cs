using StudyStash.App.ViewModels;
using StudyStash.Core.Ai;

namespace StudyStash.App;

/// <summary>The design's AI engines: Ollama ready with its model downloaded, Claude Code ready and signed in, Codex
/// installed but not signed in, Gemini not installed. What screenshots and previews of the AI screens show.</summary>
public static class AiDemo
{
    public static AiOverview Overview() => new(
        Engines:
        [
            new EngineInfo("ollama", "Ollama", "ready") { Installed = true, Model = "qwen3:30b", Models = [new ModelOption("qwen3:30b", "qwen3:30b (19 GB)")] },
            new EngineInfo("claude", "Claude Code", "ready") { Installed = true },
            new EngineInfo("codex", "Codex", "not_signed_in") { Installed = true },
            new EngineInfo("gemini", "Gemini", "not_installed") { Site = "https://ai.google.dev/gemini-api" },
        ],
        Notes: "ollama", Ask: "claude", Fallback: true, Problems: []);

    /// <summary>Answers <see cref="Overview"/> and nothing else: enough to draw the panes, never a real library.</summary>
    sealed class Library : IAiLibrary
    {
        public Task<AiOverview?> EnginesAsync() => Task.FromResult<AiOverview?>(Overview());
        public Task<AiOverview?> DefaultsAsync(string? notes = null, string? ask = null, bool? fallback = null) => Task.FromResult<AiOverview?>(Overview());
        public Task<AiSaid?> StartAsync(string engine) => Task.FromResult<AiSaid?>(null);
        public Task<AiSaid?> DownloadAsync(string engine) => Task.FromResult<AiSaid?>(null);
        public Task<AiSaid?> SignInAsync(string engine) => Task.FromResult<AiSaid?>(null);
        public Task<AiSaid?> CheckAsync(string engine, string model = "") => Task.FromResult<AiSaid?>(null);
        public Task<AiOverview?> ModelAsync(string engine, string model) => Task.FromResult<AiOverview?>(Overview());
        public Task<AiOverview?> DismissAsync(string problemId) => Task.FromResult<AiOverview?>(Overview());
        public Task<AskReply?> AskAsync(AskRequest request) => Task.FromResult<AskReply?>(null);
        public Task<RewriteInfo?> RewriteAsync(string lecture) => Task.FromResult<RewriteInfo?>(null);
        public Task<RewriteInfo?> RewriteStartAsync(string lecture, string engine) => Task.FromResult<RewriteInfo?>(null);
        public Task<RewriteInfo?> RewriteCancelAsync(string lecture) => Task.FromResult<RewriteInfo?>(null);
        public Task<RewriteInfo?> RewriteKeepAsync(string lecture) => Task.FromResult<RewriteInfo?>(null);
        public Task<RewriteInfo?> RewriteUseAsync(string lecture) => Task.FromResult<RewriteInfo?>(null);
    }

    /// <summary>The AI engines pane, loaded: notes on Ollama, questions on Claude Code, the fallback on.</summary>
    public static AiEnginesModel Engines()
    {
        var m = new AiEnginesModel(new Library());
        m.Load().GetAwaiter().GetResult();
        return m;
    }

    /// <summary>The library setup step, loaded: Ollama picked (Recommended), Codex offering to sign in.</summary>
    public static AiSetupModel Setup()
    {
        var m = new AiSetupModel(new Library());
        m.Load().GetAwaiter().GetResult();
        return m;
    }
}
