using System.Text.Json.Nodes;
using StudyStash.Core.Ai;

namespace StudyStash.Core.Tests;

/// <summary>Picking an AI: ai.json, each provider's command line, and reading what each one prints.</summary>
public class AiTests
{
    [Fact]
    public void Without_ai_json_everything_stays_on_ollama()
    {
        using var dir = new TempDir();
        var s = AiSettings.Load(dir.Path);
        foreach (string job in AiSettings.Jobs) Assert.True(s.Local(job));
    }

    [Fact]
    public void A_job_can_use_its_own_ai_and_the_rest_follow_the_main_one()
    {
        using var dir = new TempDir();
        var s = new AiSettings { Provider = "claude", Models = { ["claude"] = "sonnet" } };
        s.ByJob["sort"] = new AiChoice("ollama");
        s.Save(dir.Path);
        var back = AiSettings.Load(dir.Path);
        Assert.Equal(new AiChoice("claude", "sonnet"), back.For("notes"));
        Assert.True(back.Local("sort"));
        Assert.Contains("\"by_job\"", File.ReadAllText(AiSettings.PathIn(dir.Path)));
    }

    [Fact]
    public void An_ai_json_from_before_fallback_and_limits_still_loads()
    {
        using var dir = new TempDir();
        File.WriteAllText(AiSettings.PathIn(dir.Path), """{"provider": "claude", "models": {"claude": "sonnet"}}""");
        var s = AiSettings.Load(dir.Path);
        Assert.Equal("claude", s.Provider);
        Assert.True(s.Fallback);
        Assert.Empty(s.Limits);
        Assert.Empty(s.Dismissed);
    }

    [Fact]
    public void Json_is_found_inside_a_chatty_answer()
    {
        Assert.Equal("""{"a": "}"}""", AiJobs.FirstObject("Sure! ```json\n{\"a\": \"}\"}\n```"));
        Assert.Equal("""{"b":1}""", AiJobs.FirstObject("{not json} then {\"b\":1}"));
        Assert.Null(AiJobs.FirstObject("no json here"));
    }

    [Fact]
    public void Claude_reads_only_unless_it_may_write_and_then_only_in_its_folder()
    {
        var claude = new ClaudeProvider();
        var ro = claude.Command(new AiRequest("hi", "/lib"), stream: false);
        Assert.Contains("Edit", ro.SkipWhile(a => a != "--disallowedTools"));
        Assert.DoesNotContain("--include-partial-messages", ro);
        var rw = claude.Command(new AiRequest("hi", "/lib") { Write = true, Tools = true, McpCommand = ["/app/studystash", "mcp"], Model = "sonnet" }, stream: true);
        Assert.Contains("Edit(//lib/**)", rw);
        Assert.Contains("mcp__study-stash", rw);
        Assert.Equal("sonnet", rw[rw.IndexOf("--model") + 1]);
        Assert.Contains("\"study-stash\"", rw[rw.IndexOf("--mcp-config") + 1]);
    }

    [Fact]
    public void Claude_streams_text_tools_and_its_result()
    {
        var c = new ClaudeProvider();
        Assert.Equal("session", c.Parse("""{"type":"system","subtype":"init","session_id":"s1"}""").Single().Kind);
        Assert.Equal("Hel", c.Parse("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hel"}}}""").Single().Text);
        var tool = c.Parse("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"/lib/a.md"}}]}}""").Single();
        Assert.Equal(("tool", "Read", "/lib/a.md"), (tool.Kind, tool.Name, tool.Path));
        var end = c.Parse("""{"type":"result","session_id":"s1","is_error":false,"result":"Hello"}""").ToList();
        Assert.Equal("Hello", end.Single(e => e.Kind == "final").Text);
        Assert.Equal("error", c.Parse("""{"type":"result","is_error":true,"result":"out of credits"}""").Last().Kind);
    }

    [Fact]
    public void Codex_answers_with_its_last_message_not_its_narration()
    {
        var codex = new CodexProvider();
        codex.Parse("""{"type":"thread.started","thread_id":"t1"}""").ToList();
        codex.Parse("""{"type":"item.completed","item":{"type":"agent_message","text":"I'll look at your notes."}}""").ToList();
        var tool = codex.Parse("""{"type":"item.started","item":{"type":"command_execution","command":"rg recursion"}}""").Single();
        Assert.Equal("rg recursion", tool.Path);
        codex.Parse("""{"type":"item.completed","item":{"type":"agent_message","text":"Recursion is a function calling itself."}}""").ToList();
        Assert.Empty(codex.Parse("""{"type":"error","message":"Reconnecting... 2/5 (stream disconnected)"}"""));
        var final = codex.Parse("""{"type":"turn.completed"}""").Single();
        Assert.Equal(("final", "Recursion is a function calling itself."), (final.Kind, final.Text));
    }

    [Fact]
    public void Codex_is_read_only_unless_it_may_write()
    {
        var cmd = new CodexProvider().Command(new AiRequest("q", "/lib") { System = "brief" }, stream: false);
        Assert.Equal("read-only", cmd[cmd.IndexOf("-s") + 1]);
        Assert.StartsWith("brief", cmd[^1]);
        var local = new OllamaProvider().Command(new AiRequest("q", "/lib") { Write = true }, stream: false);
        Assert.Contains("--oss", local);
        Assert.Equal("workspace-write", local[local.IndexOf("-s") + 1]);
    }

    [Fact]
    public void Gemini_reads_its_step_updates()
    {
        var g = new GeminiProvider();
        Assert.Equal("c1", g.Parse("""{"event":"init","conversation_id":"c1"}""").Single().Text);
        Assert.Equal("Hi", g.Parse("""{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"agent_response","text_delta":"Hi"}}""").Single().Text);
        var tool = g.Parse("""{"event":"step_update","step_update":{"state":"ACTIVE","step_type":"tool","tool_name":"view_file","tool_info":{"parameters":{"AbsolutePath":"/lib/x.md"}}}}""").Single();
        Assert.Equal(("view_file", "/lib/x.md"), (tool.Name, tool.Path));
        Assert.Empty(g.Parse("""{"event":"step_update","step_update":{"state":"DONE","step_type":"tool"}}"""));
        Assert.Equal("Done", g.Parse("""{"event":"result","result":{"status":"SUCCESS","response":"Done","conversation_id":"c1"}}""").Last().Text);
        Assert.Equal("error", g.Parse("""{"event":"result","result":{"status":"FAILED","error":"quota"}}""").Single().Kind);
        Assert.Equal("plan", g.Command(new AiRequest("q", "/lib"), false)[g.Command(new AiRequest("q", "/lib"), false).IndexOf("--mode") + 1]);
    }

    [Fact]
    public void Gemini_may_run_read_only_commands_and_its_settings_are_kept()
    {
        using var dir = new TempDir();
        string path = dir["settings.json"];
        File.WriteAllText(path, """{"colorScheme":"dark","permissions":{"allow":["command(ls)"]}}""");
        Assert.True(GeminiProvider.AllowReading(path));
        var j = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("dark", j["colorScheme"]!.GetValue<string>());
        var allow = j["permissions"]!["allow"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Single(allow, "command(ls)");
        Assert.Contains("command(grep)", allow);
        Assert.False(GeminiProvider.AllowReading(path)); // already there
        Assert.False(GeminiProvider.AllowReading(dir["missing.json"]));
    }
}
