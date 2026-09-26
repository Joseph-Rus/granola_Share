using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using StudyStash.Core;

namespace StudyStash.Library;

/// <summary>
/// The library's door for Claude, on a port of its own (the library's port + 1, on this computer only): the MCP
/// server, and the OAuth sign-in Claude uses to reach it. Tailscale Serve or Funnel puts this port, and nothing
/// else of the library, on https://&lt;library&gt;.ts.net. Claude signs in the way any app does: it registers, sends
/// you to the sign-in page, where the library's password allows it, and gets tokens that only read.
/// </summary>
public static class ClaudeWeb
{
    public const string McpPath = "/mcp";

    public static int PortFor(Config cfg) => cfg.WebPort + 1;

    public static WebApplication Build(WebApplicationBuilder builder, Config cfg, LibraryReader reader, ClaudeAccess access, StudyStash.Core.Canvas.CanvasSync? canvas = null, StudyStash.Core.Ai.FileIndex? files = null)
    {
        var source = new LocalLibrary(reader, canvas, cfg.Home, files);
        builder.Services.AddMcpServer(o =>
        {
            o.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = ClaudeTools.ServerName, Title = "Study Stash", Version = Engine.Version };
            o.ServerInstructions = ClaudeTools.Instructions;
        })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools(StudyStash.Core.Ai.ToolAccess.Guard(ClaudeTools.Tools(source), () => Task.FromResult((access.ToolsOn, access.Reading))))
            .WithPrompts(ClaudeTools.Prompts());
        var app = builder.Build();

        // Only a signed-in Claude (or a token from Settings) gets to the MCP server. Anyone else is told where to
        // sign in (RFC 9728), which is how Claude finds the sign-in on its own.
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (!ctx.Request.Path.StartsWithSegments(McpPath))
            {
                await next();
                return;
            }
            if (!access.ToolsOn)
            {
                await Http.Detail(403, "AI tool access is off in Study Stash.").ExecuteAsync(ctx);
                return;
            }
            string auth = ctx.Request.Headers.Authorization.ToString();
            string bearer = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
            if (access.Check(bearer) is null)
            {
                string metadata = Base(ctx, access) + "/.well-known/oauth-protected-resource" + McpPath;
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = bearer.Length > 0
                    ? $"Bearer error=\"invalid_token\", resource_metadata=\"{metadata}\""
                    : $"Bearer resource_metadata=\"{metadata}\"";
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized", error_description = "Sign in to Study Stash first." });
                return;
            }
            await next();
        });

        IResult Resource(HttpContext ctx) => Http.Json(new JsonObject
        {
            ["resource"] = Base(ctx, access) + McpPath,
            ["authorization_servers"] = new JsonArray(Base(ctx, access)),
            ["bearer_methods_supported"] = new JsonArray("header"),
            ["scopes_supported"] = new JsonArray("library:read"),
            ["resource_name"] = "Study Stash",
        });
        app.MapGet("/.well-known/oauth-protected-resource", Resource);
        app.MapGet("/.well-known/oauth-protected-resource" + McpPath, Resource);
        IResult Server(HttpContext ctx)
        {
            string b = Base(ctx, access);
            return Http.Json(new JsonObject
            {
                ["issuer"] = b,
                ["authorization_endpoint"] = b + "/authorize",
                ["token_endpoint"] = b + "/token",
                ["registration_endpoint"] = b + "/register",
                ["response_types_supported"] = new JsonArray("code"),
                ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
                ["code_challenge_methods_supported"] = new JsonArray("S256"),
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
                ["scopes_supported"] = new JsonArray("library:read"),
                ["service_documentation"] = "https://github.com/Joseph-Rus/study-stash",
            });
        }
        app.MapGet("/.well-known/oauth-authorization-server", Server);
        app.MapGet("/.well-known/oauth-authorization-server" + McpPath, Server);
        app.MapGet("/.well-known/openid-configuration", Server);

        app.MapPost("/register", Http.Handle(async ctx =>
        {
            var body = await Http.JsonBodyAsync(ctx.Request);
            var uris = (body?["redirect_uris"] as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "").ToList();
            string name = body?["client_name"] is JsonValue nv && nv.TryGetValue(out string? cn) ? cn ?? "" : "";
            ClaudeClient client;
            try
            {
                client = access.Register(name.Length > 0 ? Py.Head(name, 80) : "Claude", uris);
            }
            catch (ArgumentException e)
            {
                return Http.Json(new JsonObject { ["error"] = "invalid_redirect_uri", ["error_description"] = e.Message }, 400);
            }
            return Http.Json(new JsonObject
            {
                ["client_id"] = client.ClientId,
                ["client_id_issued_at"] = (long)client.Created,
                ["client_name"] = client.Name,
                ["redirect_uris"] = new JsonArray(client.RedirectUris.Select(u => (JsonNode?)u).ToArray()),
                ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
                ["response_types"] = new JsonArray("code"),
                ["token_endpoint_auth_method"] = "none",
            }, 201);
        }));

        app.MapGet("/authorize", (HttpContext ctx) => Authorize(ctx, cfg, access, post: null));
        app.MapPost("/authorize", Http.Handle(async ctx => Authorize(ctx, cfg, access, await Http.FormAsync(ctx.Request))));

        app.MapPost("/token", Http.Handle(async ctx =>
        {
            var form = await Http.FormAsync(ctx.Request);
            ctx.Response.Headers.CacheControl = "no-store";
            var (tokens, error) = form.Get("grant_type") switch
            {
                "authorization_code" => access.Exchange(form.Get("code"), form.Get("code_verifier"), form.Get("client_id"), form.Get("redirect_uri")),
                "refresh_token" => access.Refresh(form.Get("refresh_token"), form.Get("client_id")),
                _ => (null, "unsupported_grant_type"),
            };
            if (tokens is null) return Http.Json(new JsonObject { ["error"] = error }, 400);
            return Http.Json(new JsonObject
            {
                ["access_token"] = tokens.AccessToken, ["token_type"] = "Bearer", ["expires_in"] = tokens.ExpiresIn,
                ["refresh_token"] = tokens.RefreshToken, ["scope"] = "library:read",
            });
        }));

        app.MapMcp(McpPath);
        app.MapGet("/", () => Results.Text(
            "Study Stash: the MCP server for your lecture library.\nAdd " + McpPath + " on this address to Claude as a connector.\n",
            "text/plain; charset=utf-8"));
        app.MapGet("/healthz", () => Http.Json(new JsonObject { ["ok"] = true, ["app"] = "study-stash-claude", ["version"] = Engine.Version }));
        Icons.Map(app);
        app.MapFallback(() => Http.Detail(404, "Not Found"));
        return app;
    }

    /// <summary>This server's address as Claude sees it: the public one when a request comes through it, else
    /// the address the request came to (Tailscale Serve passes on the name it was reached by).</summary>
    public static string Base(HttpContext ctx, ClaudeAccess access)
    {
        string host = ctx.Request.Headers["X-Forwarded-Host"].ToString() is { Length: > 0 } fh ? fh.Split(',')[0].Trim() : ctx.Request.Host.Value ?? "localhost";
        foreach (string known in new[] { access.PublicUrl, access.TailnetUrl ?? "" })
            if (known.Length > 0 && Uri.TryCreate(known, UriKind.Absolute, out var u) && string.Equals(u.Authority, host, StringComparison.OrdinalIgnoreCase))
                return known;
        string scheme = ctx.Request.Headers["X-Forwarded-Proto"].ToString() is { Length: > 0 } fp ? fp.Split(',')[0].Trim() : ctx.Request.Scheme;
        return $"{scheme}://{host}";
    }

    static string Q(IFormCollection? post, HttpContext ctx, string key) =>
        post is not null ? post.Get(key) : ctx.Request.Query[key].ToString();

    /// <summary>The sign-in page: what's asking, and the library's password to allow it. Allowed, it goes back to
    /// Claude with a code; turned down, with access_denied.</summary>
    static IResult Authorize(HttpContext ctx, Config cfg, ClaudeAccess access, IFormCollection? post)
    {
        string clientId = Q(post, ctx, "client_id"), redirect = Q(post, ctx, "redirect_uri"), state = Q(post, ctx, "state");
        string challenge = Q(post, ctx, "code_challenge"), method = Q(post, ctx, "code_challenge_method"), resource = Q(post, ctx, "resource");
        var client = access.Client(clientId);
        // Nothing is sent back to a redirect we can't vouch for: the page says what's wrong instead.
        if (client is null) return Problem("This sign-in link isn't one Study Stash knows. Start again from Claude.", 400);
        if (!client.RedirectUris.Contains(redirect)) return Problem("This sign-in link goes somewhere Study Stash doesn't know. Start again from Claude.", 400);
        string Back(string query) => redirect + (redirect.Contains('?') ? "&" : "?") + query
            + (state.Length > 0 ? "&state=" + Uri.EscapeDataString(state) : "") + "&iss=" + Uri.EscapeDataString(Base(ctx, access));
        if (Q(post, ctx, "response_type") != "code") return Results.Redirect(Back("error=unsupported_response_type"));
        if (challenge.Length < 43 || method != "S256") return Results.Redirect(Back("error=invalid_request&error_description=" + Uri.EscapeDataString("PKCE (S256) is required")));
        if (cfg.PoolPassword.Length == 0)
            return Problem("Set a password for your library first (in its Settings), so only you can let Claude in.", 403);
        var fields = new[] { ("client_id", clientId), ("redirect_uri", redirect), ("state", state), ("code_challenge", challenge),
            ("code_challenge_method", method), ("resource", resource), ("response_type", "code") };
        string csp = $"default-src 'none'; style-src 'unsafe-inline'; img-src 'self'; form-action 'self' {new Uri(redirect).GetLeftPart(UriPartial.Authority)}; frame-ancestors 'none'; base-uri 'none'";
        if (post is null) return Form(cfg, client, fields, csp, error: null);
        if (post.Get("decision") == "deny") return Results.Redirect(Back("error=access_denied"));
        if (access.LockedOut()) return Form(cfg, client, fields, csp, "Too many wrong passwords. Try again in 15 minutes.", 429);
        string password = post.Get("password");
        bool right = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(cfg.PoolPassword));
        if (!right)
        {
            access.Failed();
            return Form(cfg, client, fields, csp, "That password isn't right.", 401);
        }
        return Results.Redirect(Back("code=" + Uri.EscapeDataString(access.NewCode(clientId, redirect, challenge, resource))));
    }

    static IResult Form(Config cfg, ClaudeClient client, (string, string)[] fields, string csp, string? error, int status = 200)
    {
        string hidden = string.Concat(fields.Select(f => $"<input type=\"hidden\" name=\"{f.Item1}\" value=\"{Ui.Esc(f.Item2)}\">"));
        string err = error is null ? "" : $"<p class=\"bad\">{Ui.Esc(error)}</p>";
        string body = $"""
            <main><form method="post" action="/authorize">
            <img src="/icon.png" alt="" width="56" height="56">
            <h1>Let {Ui.Esc(client.Name.Length > 0 ? client.Name : "Claude")} read your lectures?</h1>
            <p>It will see the notes and transcripts in {Ui.Esc(cfg.PoolName)}. It can't change or delete anything.</p>
            {err}{hidden}
            <input type="password" name="password" placeholder="Library password" aria-label="Library password" autocomplete="current-password" autofocus required>
            <button name="decision" value="allow">Allow</button>
            <button name="decision" value="deny" class="quiet" formnovalidate>Don't allow</button>
            </form></main>
            """;
        return Http.Html(Shell("Allow Claude · Study Stash", body), csp, status);
    }

    static IResult Problem(string text, int status) =>
        Http.Html(Shell("Study Stash", $"<main><form><img src=\"/icon.png\" alt=\"\" width=\"56\" height=\"56\"><h1>Can't sign in</h1><p>{Ui.Esc(text)}</p></form></main>"),
            "default-src 'none'; style-src 'unsafe-inline'; img-src 'self'; frame-ancestors 'none'; base-uri 'none'", status);

    /// <summary>A page in the Study Stash look, light or dark with the system, self-contained (no scripts).</summary>
    static string Shell(string title, string body) => $$$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{{Ui.Esc(title)}}}</title><link rel="icon" href="/favicon.ico">
        <style>
        :root{color-scheme:light dark;--bg:#F4F4F4;--card:#fff;--fg:#1D1D1F;--fg2:rgba(0,0,0,.56);--sep:rgba(0,0,0,.1);--accent:#E5484D;--bad:#C4383D}
        @media (prefers-color-scheme:dark){:root{--bg:#161616;--card:#1E1E1E;--fg:#F5F5F7;--fg2:rgba(255,255,255,.58);--sep:rgba(255,255,255,.1);--accent:#EC5D5E;--bad:#F4979A}}
        *{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;padding:24px;background:var(--bg);color:var(--fg);
        font:15px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI Variable Text","Segoe UI",system-ui,sans-serif;-webkit-font-smoothing:antialiased}
        main{width:min(380px,100%)}form{display:grid;gap:12px;padding:32px 28px;border-radius:16px;background:var(--card);box-shadow:0 12px 40px rgba(0,0,0,.12),0 0 0 .5px var(--sep);text-align:center}
        img{margin:0 auto 4px;border-radius:12px}h1{font-size:20px;line-height:1.25;margin:0;letter-spacing:-.01em}p{margin:0;color:var(--fg2);font-size:14px}
        p.bad{color:var(--bad)}input{height:40px;padding:0 12px;border-radius:8px;border:1px solid var(--sep);background:transparent;color:inherit;font:inherit}
        input:focus{outline:2px solid var(--accent);outline-offset:1px}button{height:40px;border:0;border-radius:8px;background:var(--accent);color:#fff;font:inherit;font-weight:600;cursor:pointer}
        button.quiet{background:transparent;color:var(--fg2);font-weight:500}button:focus-visible{outline:2px solid var(--accent);outline-offset:2px}
        </style></head><body>{{{body}}}</body></html>
        """;
}
