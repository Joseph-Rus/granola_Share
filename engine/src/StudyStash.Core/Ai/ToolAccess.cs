using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace StudyStash.Core.Ai;

/// <summary>
/// Which of a student's reading toggles (<see cref="ReadingScopes"/>) a tool needs, and the guard that refuses a
/// call when AI tool access is off, or the toggle its kind needs is off. Refusing happens at call time, not by
/// hiding the tool from <c>tools/list</c>, so a client's tool list never changes underneath it.
/// </summary>
public static class ToolAccess
{
    public const string Refused = "Study Stash's settings don't let AI tools read that right now.";

    /// <summary>The scope a tool needs, or null when every tool may use it (list_classes: just the class names and
    /// counts) or nothing reads it yet (audio).</summary>
    public static string? Scope(string tool) => tool switch
    {
        "list_lectures" or "search_notes" or "get_transcript" => "lectures",
        "get_lecture" => "notes",
        "search_files" or "due_assignments" or "canvas_courses" or "canvas_api" or "canvas_page" or "canvas_download" => "canvas",
        _ => null,
    };

    static bool Allowed(string tool, ReadingScopes reading) => Scope(tool) switch
    {
        "lectures" => reading.Lectures,
        "notes" => reading.Notes,
        "canvas" => reading.Canvas,
        _ => true,
    };

    sealed class Guarded(McpServerTool inner, Func<Task<(bool On, ReadingScopes Reading)>> access) : DelegatingMcpServerTool(inner)
    {
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var (on, reading) = await access();
            return !on || !Allowed(ProtocolTool.Name, reading)
                ? new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = Refused }] }
                : await base.InvokeAsync(request, cancellationToken);
        }
    }

    /// <summary>Each tool, refusing at call time instead of running it, once <paramref name="access"/> says access
    /// is off or the scope it needs is off. Called fresh for every call, so a change (or the off switch) takes
    /// effect at once, without reconnecting.</summary>
    public static List<McpServerTool> Guard(IEnumerable<McpServerTool> tools, Func<Task<(bool On, ReadingScopes Reading)>> access) =>
        [.. tools.Select(t => (McpServerTool)new Guarded(t, access))];
}
