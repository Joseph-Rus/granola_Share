using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using StudyStash.Core;

namespace StudyStash.Library;

/// <summary>
/// /api/v2: the library for the Study Stash app (and Claude, through <see cref="RemoteLibrary"/>). Everything takes the
/// laptop's key (Authorization: Bearer &lt;library password&gt;), like /api/ingest.
/// </summary>
public sealed partial class LibraryWeb
{
    LibraryReader? reader;
    ClaudeAccess? claude;

    LibraryReader Reader => reader ??= new LibraryReader(cfg, store);

    public ClaudeAccess Claude => claude ??= options.Claude ?? new ClaudeAccess(cfg.Home);

    IResult Api(HttpContext ctx, Func<IResult> answer) => RequireKey(ctx) ?? answer();

    async Task<IResult> ApiAsync(HttpContext ctx, Func<Task<IResult>> answer) => RequireKey(ctx) ?? await answer();

    static string? Str(JsonObject? body, string key) => body?[key] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    void MapApp(WebApplication app)
    {
        if (store.IndexMissing() is int n and > 0) Console.WriteLine($"[library] indexed {n} lecture(s) for search");

        app.MapGet("/api/v2/library", (HttpContext ctx) => Api(ctx, () => Http.Json(Reader.Overview())));
        app.MapGet("/api/v2/lectures", (HttpContext ctx, string? @class, int? limit, string? before) =>
            Api(ctx, () => Http.Json(Reader.Lectures(@class, limit ?? 50, before))));
        app.MapGet("/api/v2/lectures/{id}", (HttpContext ctx, string id) =>
            Api(ctx, () => Reader.Lecture(id) is { } l ? Http.Json(l) : Http.Detail(404, "no such lecture")));
        app.MapPost("/api/v2/lectures/{id}/class", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            string? cls = Str(await Http.JsonBodyAsync(ctx.Request), "class");
            if (cls is null) return Http.Detail(400, "which class?");
            if (cls != Configs.Unsorted && !cfg.ClassNames().Contains(cls)) return Http.Detail(400, $"there's no class {cls}");
            return store.SetClass(id, cls) is null && store.Get(id) is null ? Http.Detail(404, "no such lecture")
                : Http.Json(Reader.Lecture(id));
        })));
        app.MapPost("/api/v2/lectures/{id}/rewrite", (HttpContext ctx, string id) => Api(ctx, () =>
        {
            if (!store.Requeue(id)) return Http.Detail(404, "no such lecture");
            pipeline.Wake();
            return Http.Json(new JsonObject { ["id"] = id, ["status"] = Store.Queued });
        }));
        app.MapDelete("/api/v2/lectures/{id}", (HttpContext ctx, string id) =>
            Api(ctx, () => store.Delete(id) ? Http.Json(new JsonObject { ["deleted"] = id }) : Http.Detail(404, "no such lecture")));
        app.MapGet("/api/v2/search", (HttpContext ctx, string? q, string? @class, int? limit) =>
            Api(ctx, () => Http.Json(Reader.Search(q ?? "", @class, Math.Clamp(limit ?? 8, 1, 50)))));
        app.MapPost("/api/v2/ask", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            var body = await Http.JsonBodyAsync(ctx.Request);
            string? question = Str(body, "question");
            if (question is null) return Http.Detail(400, "what's the question?");
            if (!cfg.OllamaEnabled && options.AskChat is null)
                return Http.Detail(503, "Asking needs the library's AI model: turn it on in the library's Settings.");
            try
            {
                return Http.Json(await Reader.AskAsync(question, Str(body, "lecture"), Str(body, "class"), Str(body, "live"),
                    Str(body, "live_title") ?? "This lecture", options.AskChat));
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutException or InvalidOperationException)
            {
                return Http.Detail(503, $"The library's model didn't answer: {e.Message}");
            }
        })));
        app.MapPost("/api/v2/classes", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            string? name = Str(await Http.JsonBodyAsync(ctx.Request), "name");
            if (name is null || name.Length > 60) return Http.Detail(400, "a class needs a name (up to 60 characters)");
            if (name.Equals(Configs.Unsorted, StringComparison.OrdinalIgnoreCase)) return Http.Detail(400, "that name is taken");
            if (!cfg.ClassNames().Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                cfg.Classes.Add(new ClassDef(name, []));
                Configs.Save(cfg);
            }
            return Http.Json(Reader.Overview());
        })));

        // Connecting Claude: what's on, the connections that can read, and turning the web address on or off.
        app.MapGet("/api/v2/claude", (HttpContext ctx) => Api(ctx, () => Http.Json(ClaudeJson())));
        app.MapPost("/api/v2/claude/reach", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            var body = await Http.JsonBodyAsync(ctx.Request);
            bool internet = body?["internet"] is JsonValue iv && iv.TryGetValue(out bool i) && i;
            bool on = body?["on"] is not JsonValue ov || !ov.TryGetValue(out bool o) || o;
            if (on && cfg.PoolPassword.Length == 0) return Http.Detail(400, "Set a library password first, so only you can let Claude in.");
            var (url, problem) = options.Reach.Set(ClaudeWeb.PortFor(cfg), internet, on);
            if (problem is not null) return Http.Detail(409, problem);
            if (internet) Claude.PublicUrl = url ?? "";
            else Claude.TailnetUrl = url;
            return Http.Json(ClaudeJson());
        })));
        app.MapPost("/api/v2/claude/tokens", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            var (token, grant) = Claude.CreateToken(Str(await Http.JsonBodyAsync(ctx.Request), "name") ?? "MCP client");
            var result = ClaudeJson();
            result["token"] = token;
            result["token_id"] = grant.Id;
            return Http.Json(result);
        })));
        app.MapDelete("/api/v2/claude/connections/{id}", (HttpContext ctx, string id) =>
            Api(ctx, () => Claude.Revoke(id) ? Http.Json(ClaudeJson()) : Http.Detail(404, "no such connection")));
    }

    JsonObject ClaudeJson()
    {
        int port = ClaudeWeb.PortFor(cfg);
        return new JsonObject
        {
            ["local_url"] = $"http://127.0.0.1:{port}{ClaudeWeb.McpPath}",
            ["tailnet_url"] = Claude.TailnetUrl is { Length: > 0 } t ? t + ClaudeWeb.McpPath : null,
            ["public_url"] = Claude.PublicUrl.Length > 0 ? Claude.PublicUrl + ClaudeWeb.McpPath : null,
            ["has_password"] = cfg.PoolPassword.Length > 0,
            ["connections"] = new JsonArray(Claude.Grants().Select(g => (JsonNode?)new JsonObject
            {
                ["id"] = g.Id, ["name"] = g.Name, ["kind"] = g.Kind, ["created"] = g.Created, ["last_used"] = g.LastUsed > 0 ? g.LastUsed : null,
            }).ToArray()),
        };
    }
}
