using System.Diagnostics;
using System.Security.Cryptography;

namespace StudyStash.Core.Ai;

/// <summary>
/// "Open in Claude Code": a terminal in a class's folder, running the AI picked for agent work (Claude Code, Codex,
/// or Antigravity for Gemini), briefed on where it is, for real work with the class's notes and Canvas material at
/// hand. Ghostty unless another terminal is picked.
/// </summary>
public static class Terminal
{
    public static readonly (string Id, string Name, string App)[] Mac =
    [
        ("ghostty", "Ghostty", "/Applications/Ghostty.app"), ("terminal", "Terminal", "/System/Applications/Utilities/Terminal.app"),
        ("iterm", "iTerm", "/Applications/iTerm.app"), ("warp", "Warp", "/Applications/Warp.app"),
    ];

    /// <summary>The terminals on this computer: (id, name).</summary>
    public static List<(string Id, string Name)> Available() =>
        OperatingSystem.IsMacOS() ? Mac.Where(t => Directory.Exists(t.App)).Select(t => (t.Id, t.Name)).ToList()
        : OperatingSystem.IsWindows() ? [("wt", "Windows Terminal")] : [];

    /// <summary>What the AI is told when it opens.</summary>
    public static string Briefing(string library, string folder, string? className) =>
        $"You were opened from Study Stash, the user's lecture library ({library}: one folder per class, lectures as Markdown, "
        + "and a Canvas/ folder with assignment specs, feedback, module files and announcements). "
        + (className is not null ? $"This is the class \"{className}\" ({folder}). Look at its lectures, Canvas/assignments and Canvas/canvas-recipe.md before starting, and follow any AI-use policy in its syllabus. " : "")
        + "The study-stash MCP tools (search_notes, get_lecture, due_assignments, canvas_*) read the library and Canvas.";

    static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>Open it. The terminal's name, or throws InvalidOperationException saying why not.</summary>
    public static string Open(string home, string folder, string library, string? className, string terminal, string provider, IReadOnlyList<string> mcp)
    {
        var (bin, args) = provider switch
        {
            "codex" or "ollama" => ("codex", new List<string>()),
            "gemini" => ("agy", new List<string>()),
            _ => ("claude", new List<string> { "--append-system-prompt", Briefing(library, folder, className) }),
        };
        string exe = AiProvider.Which(bin) ?? throw new InvalidOperationException($"{bin} isn't installed on this computer.");
        if (bin == "claude" && mcp.Count > 0)
            args.AddRange(["--mcp-config", new System.Text.Json.Nodes.JsonObject
            {
                ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
                {
                    [ClaudeTools.ServerName] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["command"] = mcp[0], ["args"] = new System.Text.Json.Nodes.JsonArray(mcp.Skip(1).Select(a => (System.Text.Json.Nodes.JsonNode)a).ToArray()),
                    },
                },
            }.ToJsonString()]);
        if (!Py.SamePath(folder, library)) args.AddRange(bin == "claude" ? ["--add-dir", library] : []);

        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true, ArgumentList = { "-d", folder, exe } });
            return "Windows Terminal";
        }
        var t = Mac.FirstOrDefault(m => m.Id == terminal);
        if (t.Id is null || !Directory.Exists(t.App)) t = Mac.FirstOrDefault(m => Directory.Exists(m.App));
        if (t.Id is null) throw new InvalidOperationException("No terminal app found.");
        // A small script the terminal runs: into the folder, start the AI, and stay in a shell after.
        string dir = Path.Combine(home, "launch");
        Directory.CreateDirectory(dir);
        foreach (string old in Directory.EnumerateFiles(dir, "*.command").OrderByDescending(File.GetLastWriteTimeUtc).Skip(20)) File.Delete(old);
        string script = Path.Combine(dir, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)) + ".command");
        File.WriteAllText(script, $"#!/bin/zsh -l\ncd {Quote(folder)} || exit 1\n{Quote(exe)} {string.Join(" ", args.Select(Quote))}\nexec zsh -l\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var open = t.Id == "ghostty"
            ? new ProcessStartInfo("open") { ArgumentList = { "-na", t.App, "--args", $"--working-directory={folder}", "-e", "/bin/zsh", script } }
            : new ProcessStartInfo("open") { ArgumentList = { "-a", t.App, script } };
        open.UseShellExecute = false;
        using var _ = Process.Start(open);
        return t.Name;
    }
}
