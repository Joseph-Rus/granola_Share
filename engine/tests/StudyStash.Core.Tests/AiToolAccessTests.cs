using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StudyStash.Core.Ai;

namespace StudyStash.Core.Tests;

/// <summary>A library with one lecture and no Canvas: enough for <see cref="ClaudeTools.Tools"/> to build its
/// read-only tool set, so the stdio guard can be tested without a real library.</summary>
sealed class FakeSource : ILibrarySource
{
    public Task<JsonObject> OverviewAsync() => Task.FromResult(new JsonObject { ["name"] = "Test", ["classes"] = new JsonArray() });
    public Task<JsonArray> LecturesAsync(string? className, int limit, string? before) => Task.FromResult(new JsonArray());
    public Task<JsonObject?> LectureAsync(string id) => Task.FromResult<JsonObject?>(id == "rec-1" ? new JsonObject { ["id"] = "rec-1", ["notes"] = "notes" } : null);
    public Task<JsonObject> SearchAsync(string query, string? className, int limit) => Task.FromResult(new JsonObject { ["lectures"] = new JsonArray(), ["passages"] = new JsonArray() });
}

/// <summary>Which reading toggle each tool needs, and the guard that refuses a call by kind, or all of them while
/// tool access is off — never by hiding a tool from the list.</summary>
public class AiToolAccessTests
{
    // A call needs a real (if unconnected) server to build its RequestContext: never one that runs, just one to hand to InvokeAsync.
    static readonly McpServer Server = McpServer.Create(new StdioServerTransport("test"), new McpServerOptions { ServerInfo = new Implementation { Name = "test", Version = "1" } });

    static McpServerTool Tool(string name) => McpServerTool.Create(() => "ok", new McpServerToolCreateOptions { Name = name });

    static async Task<string> Say(McpServerTool tool)
    {
        var request = new RequestContext<CallToolRequestParams>(Server, new JsonRpcRequest { Method = "tools/call" }, new CallToolRequestParams { Name = tool.ProtocolTool.Name });
        var result = await tool.InvokeAsync(request, CancellationToken.None);
        return ((TextContentBlock)result.Content[0]).Text;
    }

    [Theory]
    [InlineData("list_lectures", "lectures")]
    [InlineData("search_notes", "lectures")]
    [InlineData("get_transcript", "lectures")]
    [InlineData("get_lecture", "notes")]
    [InlineData("search_files", "canvas")]
    [InlineData("due_assignments", "canvas")]
    [InlineData("canvas_courses", "canvas")]
    [InlineData("canvas_api", "canvas")]
    [InlineData("canvas_page", "canvas")]
    [InlineData("canvas_download", "canvas")]
    [InlineData("list_classes", null)]
    public void Each_tool_needs_the_scope_the_plan_gives_it(string tool, string? scope) => Assert.Equal(scope, ToolAccess.Scope(tool));

    [Fact]
    public async Task Off_refuses_every_tool_no_matter_its_scope()
    {
        var guarded = ToolAccess.Guard([Tool("list_classes"), Tool("get_lecture")], () => Task.FromResult((false, new ReadingScopes())));
        foreach (var t in guarded) Assert.Equal(ToolAccess.Refused, await Say(t));
    }

    [Fact]
    public async Task On_but_the_scope_off_refuses_only_the_tools_that_need_it()
    {
        var reading = new ReadingScopes(Lectures: true, Notes: false, Canvas: true);
        var guarded = ToolAccess.Guard([Tool("list_classes"), Tool("list_lectures"), Tool("get_lecture"), Tool("due_assignments")],
            () => Task.FromResult((true, reading)));
        var byName = guarded.ToDictionary(t => t.ProtocolTool.Name);
        Assert.Equal("ok", await Say(byName["list_classes"]));
        Assert.Equal("ok", await Say(byName["list_lectures"]));
        Assert.Equal("ok", await Say(byName["due_assignments"]));
        Assert.Equal(ToolAccess.Refused, await Say(byName["get_lecture"]));
    }

    [Fact]
    public async Task Access_is_checked_fresh_on_every_call()
    {
        bool on = true;
        var guarded = ToolAccess.Guard([Tool("list_classes")], () => Task.FromResult((on, new ReadingScopes())));
        var tool = Assert.Single(guarded);
        Assert.Equal("ok", await Say(tool));
        on = false;
        Assert.Equal(ToolAccess.Refused, await Say(tool));
    }

    [Fact]
    public async Task The_stdio_doors_tools_over_a_fake_library_are_guarded_the_same_way()
    {
        var reading = new ReadingScopes(Lectures: true, Notes: false, Canvas: true);
        var guarded = ToolAccess.Guard(ClaudeTools.Tools(new FakeSource()), () => Task.FromResult((true, reading)));
        var byName = guarded.ToDictionary(t => t.ProtocolTool.Name);

        var lecture = await Say(byName["get_lecture"]); // notes: off
        Assert.Equal(ToolAccess.Refused, lecture);
        var lectures = await Say(byName["list_lectures"]); // lectures: on
        Assert.NotEqual(ToolAccess.Refused, lectures);
        var classes = await Say(byName["list_classes"]); // no scope at all
        Assert.NotEqual(ToolAccess.Refused, classes);
    }

    [Fact]
    public void Old_claude_json_loads_with_tools_on_and_every_scope_but_audio()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir["home"]);
        File.WriteAllText(Path.Combine(dir["home"], "claude.json"), "{}");
        var access = new ClaudeAccess(dir["home"]);
        Assert.True(access.ToolsOn);
        Assert.Equal(new ReadingScopes(), access.Reading);
        Assert.False(access.Reading.Audio);
    }

    [Fact]
    public void Turning_tools_off_makes_a_working_token_check_null()
    {
        using var dir = new TempDir();
        var access = new ClaudeAccess(dir["home"]);
        var (token, _) = access.CreateToken("Cursor");
        Assert.NotNull(access.Check(token));

        access.ToolsOn = false;
        Assert.Null(access.Check(token));

        access.ToolsOn = true;
        Assert.NotNull(access.Check(token));
    }

    [Fact]
    public void Reading_scopes_persist_across_a_reload()
    {
        using var dir = new TempDir();
        new ClaudeAccess(dir["home"]) { Reading = new ReadingScopes(Lectures: true, Notes: false, Canvas: false, Audio: true) };
        var reloaded = new ClaudeAccess(dir["home"]);
        Assert.Equal(new ReadingScopes(true, false, false, true), reloaded.Reading);
    }
}
