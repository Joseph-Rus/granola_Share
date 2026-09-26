using System.Text.Json;
using System.Text.Json.Nodes;
using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>
/// Connecting Claude to your lectures. On this computer, Claude Code and Claude Desktop start Study Stash's MCP server
/// themselves (<c>StudyStash mcp</c>), which reads the library with the password this laptop already has. Claude on
/// the web needs the library to be reachable from the internet: the library does that with Tailscale Funnel, and
/// Claude signs in with your library password.
/// </summary>
public sealed class ClaudeSetup
{
    public string Program { get; init; } = Platform.Desktop.Program;
    public string Home { get; init; } = Configs.DefaultHome;
    public Runner Run { get; init; } = (_, _, _) => throw new InvalidOperationException("Running Claude Code is off here.");
    /// <summary>Claude Desktop's config file (claude_desktop_config.json).</summary>
    public string DesktopConfig { get; init; } = DefaultDesktopConfig;

    public static ClaudeSetup ThisComputer(string home) => new() { Run = Machine.Run, Home = home };

    public static string DefaultDesktopConfig => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Claude", "claude_desktop_config.json");

    /// <summary>The MCP server's command: the app, told to be the server (and where its settings are, if not the usual).</summary>
    public List<string> McpArgs => Home == Configs.DefaultHome ? ["mcp"] : ["--home", Home, "mcp"];

    /// <summary>What to type to add it to Claude Code by hand.</summary>
    public string ClaudeCodeCommand => "claude mcp add --scope user " + ClaudeTools.ServerName + " -- "
        + string.Join(" ", new[] { Program }.Concat(McpArgs).Select(Quote));

    static string Quote(string a) => a.Any(c => c is ' ' or '"' or '\'') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a;

    public string? ClaudeCli() => Machine.Which("claude");

    /// <summary>Add Study Stash to Claude Code (for every project). The result to show.</summary>
    public string AddToClaudeCode()
    {
        if (ClaudeCli() is not { } claude) return "Claude Code isn't installed here. Install it, or copy the command below into another computer's terminal.";
        Run(claude, ["mcp", "remove", "--scope", "user", ClaudeTools.ServerName], TimeSpan.FromSeconds(30));
        var p = Run(claude, ["mcp", "add", "--scope", "user", ClaudeTools.ServerName, "--", Program, .. McpArgs], TimeSpan.FromSeconds(60));
        return p is { ExitCode: 0 } ? "Added to Claude Code. Ask it about your lectures in any project."
            : $"Claude Code didn't take it: {Py.Head(Py.Strip(p?.Stdout ?? ""), 200)}";
    }

    /// <summary>Whether Claude Code already has Study Stash added, asked of Claude Code itself (never the config
    /// file: Claude Code keeps its own).</summary>
    public bool InClaudeCode()
    {
        if (ClaudeCli() is not { } claude) return false;
        try
        {
            return Run(claude, ["mcp", "get", ClaudeTools.ServerName], TimeSpan.FromSeconds(10)) is { ExitCode: 0 };
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    static string TomlString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>What to paste into <c>~/.codex/config.toml</c>, under its own heading.</summary>
    public string CodexSetup => $"""
        [mcp_servers.{ClaudeTools.ServerName}]
        command = {TomlString(Program)}
        args = [{string.Join(", ", McpArgs.Select(TomlString))}]
        """;

    JsonObject McpServerJson => new()
    {
        ["command"] = Program,
        ["args"] = new JsonArray(McpArgs.Select(a => (JsonNode?)a).ToArray()),
    };

    /// <summary>The <c>mcpServers</c> JSON any other MCP client's settings take, to paste in by hand.</summary>
    public string McpJson => new JsonObject { ["mcpServers"] = new JsonObject { [ClaudeTools.ServerName] = McpServerJson } }
        .ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public bool InClaudeDesktop()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(DesktopConfig))?["mcpServers"]?[ClaudeTools.ServerName] is not null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Add Study Stash to Claude Desktop's MCP servers, keeping everything else in its config.</summary>
    public string AddToClaudeDesktop()
    {
        JsonObject config;
        try
        {
            config = File.Exists(DesktopConfig) ? JsonNode.Parse(File.ReadAllText(DesktopConfig)) as JsonObject ?? [] : [];
        }
        catch (JsonException)
        {
            return "Claude Desktop's settings file isn't valid JSON, so it's left alone. Fix it, or add Study Stash by hand.";
        }
        var servers = config["mcpServers"] as JsonObject ?? [];
        config["mcpServers"] = servers;
        servers[ClaudeTools.ServerName] = McpServerJson;
        Directory.CreateDirectory(Path.GetDirectoryName(DesktopConfig)!);
        string tmp = DesktopConfig + ".tmp";
        File.WriteAllText(tmp, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, DesktopConfig, overwrite: true);
        return "Added to Claude Desktop. Quit and reopen Claude Desktop to see it.";
    }

    /// <summary>Take Study Stash out of Claude Desktop's MCP servers, keeping everything else in its config.</summary>
    public string RemoveFromClaudeDesktop()
    {
        if (!File.Exists(DesktopConfig)) return "Claude Desktop doesn't have Study Stash added.";
        JsonObject config;
        try
        {
            config = JsonNode.Parse(File.ReadAllText(DesktopConfig)) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return "Claude Desktop's settings file isn't valid JSON, so it's left alone.";
        }
        if (config["mcpServers"] is not JsonObject servers || !servers.Remove(ClaudeTools.ServerName))
            return "Claude Desktop doesn't have Study Stash added.";
        string tmp = DesktopConfig + ".tmp";
        File.WriteAllText(tmp, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, DesktopConfig, overwrite: true);
        return "Removed from Claude Desktop.";
    }
}
