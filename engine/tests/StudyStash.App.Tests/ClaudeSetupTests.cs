using System.Text.Json.Nodes;
using StudyStash.App.Services;
using StudyStash.Core;

namespace StudyStash.App.Tests;

/// <summary>Connecting Claude and other MCP tools on this computer: the text to paste for Codex or any other tool,
/// whether Claude Code already has Study Stash, and adding to or removing from Claude Desktop's own config. Never
/// runs a real <c>claude</c>: every test's <see cref="ClaudeSetup.Run"/> is a fake.</summary>
public class ClaudeSetupTests
{
    static ClaudeSetup Setup(TempHome home, Runner? run = null) => new()
    {
        Program = "/usr/local/bin/studystash", Home = home.Path, DesktopConfig = home["claude_desktop_config.json"],
        Run = run ?? ((_, _, _) => throw new InvalidOperationException("not expected to run anything")),
    };

    [Fact]
    public void Codex_setup_is_the_toml_heading_codex_reads()
    {
        using var home = new TempHome();
        var setup = Setup(home);
        Assert.Equal($"""
            [mcp_servers.{ClaudeTools.ServerName}]
            command = "/usr/local/bin/studystash"
            args = ["--home", "{home.Path}", "mcp"]
            """, setup.CodexSetup);
    }

    [Fact]
    public void Mcp_json_is_the_mcpServers_block_any_other_tool_takes()
    {
        using var home = new TempHome();
        var json = JsonNode.Parse(Setup(home).McpJson)!;
        var server = json["mcpServers"]![ClaudeTools.ServerName]!;
        Assert.Equal("/usr/local/bin/studystash", server["command"]!.GetValue<string>());
        Assert.Equal(["--home", home.Path, "mcp"], server["args"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void In_claude_code_asks_claude_itself_not_a_config_file()
    {
        using var home = new TempHome();
        int exitCode = 1;
        var calls = new List<IReadOnlyList<string>>();
        var setup = Setup(home, (_, args, _) =>
        {
            calls.Add(args);
            return new ProcResult(exitCode, "");
        });
        if (setup.ClaudeCli() is null)
        {
            // Nothing to ask on this computer: no CLI, no call, and InClaudeCode says so.
            Assert.False(setup.InClaudeCode());
            Assert.Empty(calls);
            return;
        }
        Assert.False(setup.InClaudeCode()); // exit 1: not added
        Assert.Equal(["mcp", "get", ClaudeTools.ServerName], calls.Single());
        exitCode = 0;
        Assert.True(setup.InClaudeCode()); // exit 0: added
    }

    [Fact]
    public void Adding_to_claude_desktop_keeps_everything_else_in_its_config_and_removing_undoes_it()
    {
        using var home = new TempHome();
        Directory.CreateDirectory(Path.GetDirectoryName(home["claude_desktop_config.json"])!);
        File.WriteAllText(home["claude_desktop_config.json"], """{"mcpServers":{"other":{"command":"other"}},"theme":"dark"}""");
        var setup = Setup(home);

        string said = setup.AddToClaudeDesktop();
        Assert.Contains("Added to Claude Desktop", said);
        var config = JsonNode.Parse(File.ReadAllText(home["claude_desktop_config.json"]))!;
        Assert.Equal("other", config["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        Assert.Equal("dark", config["theme"]!.GetValue<string>());
        Assert.Equal("/usr/local/bin/studystash", config["mcpServers"]![ClaudeTools.ServerName]!["command"]!.GetValue<string>());
        Assert.True(setup.InClaudeDesktop());

        string removed = setup.RemoveFromClaudeDesktop();
        Assert.Contains("Removed", removed);
        Assert.False(setup.InClaudeDesktop());
        var after = JsonNode.Parse(File.ReadAllText(home["claude_desktop_config.json"]))!;
        Assert.Equal("other", after["mcpServers"]!["other"]!["command"]!.GetValue<string>()); // untouched
    }

    [Fact]
    public void Removing_from_claude_desktop_when_its_not_there_says_so_without_touching_the_file()
    {
        using var home = new TempHome();
        var setup = Setup(home);
        Assert.Equal("Claude Desktop doesn't have Study Stash added.", setup.RemoveFromClaudeDesktop());
    }
}
