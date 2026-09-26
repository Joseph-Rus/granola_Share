using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using StudyStash.Core;
using StudyStash.Core.Ai;

namespace StudyStash.Library;

/// <summary>What the library's pages ask of the world, so tests can answer instead.</summary>
public sealed class LibraryWebOptions
{
    public Func<string, Task<List<(string Name, double SizeGb)>?>> ListModels { get; init; } = host => Ollama.ListModelsAsync(host);
    public Func<TailscaleInfo> Tailscale { get; init; } = () => HostInfo.Tailscale();
    public Func<double, Task<Release?>> Latest { get; init; } = maxAge => Updates.CachedLatestAsync(maxAge);
    /// <summary>Installs a release. Null until the C# engine can update itself: then Settings only links to it.</summary>
    public Func<Release, string, Task>? Apply { get; init; }
    public Func<double?> RamGb { get; init; } = Machine.TotalRamGb;
    public Func<string> HostName { get; init; } = Machine.HostName;
    public Func<string> Nonce { get; init; } = () => Http.TokenUrlSafe(16);
    /// <summary>Who may read the library through Claude (claude.json). Null: kept beside the config.</summary>
    public ClaudeAccess? Claude { get; init; }
    /// <summary>Putting the Claude port on the tailnet or the internet (off unless the service turns it on).</summary>
    public ClaudeReach Reach { get; init; } = new();
    /// <summary>Asking your notes: the model's answer to a prompt. Null asks the library's Ollama.</summary>
    public LibraryReader.AskChatFn? AskChat { get; init; }
    /// <summary>The AI picked for each kind of work (ai.json): Settings shows and tests it. Null: kept beside the config.</summary>
    public StudyStash.Core.Ai.AiJobs? Ai { get; init; }
    /// <summary>Canvas through the Chrome extension. Null: made here, with the library's class folders.</summary>
    public StudyStash.Core.Canvas.CanvasSync? Canvas { get; init; }
    /// <summary>The course scout. Null: exploring isn't offered (tests, and `serve` without an AI).</summary>
    public StudyStash.Core.Canvas.Scout? Scout { get; init; }
    /// <summary>File search. Null: made here (and brought up to date only when folders change).</summary>
    public StudyStash.Core.Ai.FileIndex? Files { get; init; }
    /// <summary>Capture's Inbox. Null: made here.</summary>
    public StudyStash.Core.Ai.Inbox? Inbox { get; init; }
}

/// <summary>Small pieces of HTTP the Python engine got from its web framework.</summary>
public static class Http
{
    /// <summary>secrets.token_urlsafe(n).</summary>
    public static string TokenUrlSafe(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IResult Json(JsonNode? body, int status = 200) =>
        Results.Content(body?.ToJsonString(Relaxed) ?? "null", "application/json", Encoding.UTF8, status);

    /// <summary>{"detail": "..."}, the shape of every error the Python engine sent.</summary>
    public static IResult Detail(int status, string detail) => Json(new JsonObject { ["detail"] = detail }, status);

    public static IResult Html(string body, string csp, int status = 200) => new HtmlResult(body, csp, status);

    public static IResult SeeOther(string location) => new SeeOtherResult(location);

    /// <summary>A handler that takes only the request: as a plain Func, ASP.NET would take it for a RequestDelegate and
    /// throw away the page it returns.</summary>
    public static Delegate Handle(Func<HttpContext, Task<IResult>> handler) => handler;

    sealed class HtmlResult(string body, string csp, int status) : IResult
    {
        public Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers["Content-Security-Policy"] = csp;
            return ctx.Response.WriteAsync(body);
        }
    }

    sealed class SeeOtherResult(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
            ctx.Response.Headers.Location = location;
            return Task.CompletedTask;
        }
    }

    /// <summary>The form, or an empty one when the request didn't send a form.</summary>
    public static async Task<IFormCollection> FormAsync(HttpRequest request) =>
        request.HasFormContentType ? await request.ReadFormAsync() : FormCollection.Empty;

    public static string Get(this IFormCollection form, string key, string fallback = "") =>
        form.TryGetValue(key, out var v) && v.Count > 0 ? v[0] ?? fallback : fallback;

    /// <summary>A request body that is a JSON object, or null.</summary>
    public static async Task<JsonObject?> JsonBodyAsync(HttpRequest request)
    {
        try
        {
            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            return Py.JsonLoads(await reader.ReadToEndAsync()) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The library's web server (web.py): the pages you browse, and the API your laptop sends lectures to. Every
/// address, form field, cookie, and JSON key is the Python engine's, so a laptop or a browser can't tell which
/// engine answers.
/// </summary>
public sealed partial class LibraryWeb
{
    static readonly Dictionary<string, string> SortedBy = new()
    {
        ["folder"] = "its Granola folder", ["rules"] = "its title", ["ollama"] = "AI", ["human"] = "a person", ["none"] = "nobody yet",
    };
    static readonly string[] ProxyHeaders = ["x-forwarded-for", "forwarded", "tailscale-user-login"];

    readonly Config cfg;
    readonly Store store;
    readonly Pipeline pipeline;
    readonly LibraryWebOptions options;
    readonly byte[] secret;

    LibraryWeb(Config cfg, Store store, Pipeline pipeline, LibraryWebOptions options)
    {
        this.cfg = cfg;
        this.store = store;
        this.pipeline = pipeline;
        this.options = options;
        secret = WebSecret(cfg);
    }

    /// <summary>The library's routes on this app. The caller decides where it listens (Kestrel, or a test server).</summary>
    public static WebApplication Build(WebApplicationBuilder builder, Config cfg, Store store, Pipeline pipeline, LibraryWebOptions? options = null)
    {
        var app = builder.Build();
        new LibraryWeb(cfg, store, pipeline, options ?? new LibraryWebOptions()).Map(app);
        return app;
    }

    /// <summary>Only same-site paths. Browsers read "\" as "/" and drop tabs and newlines, so "/\evil.com" or
    /// "/\t/evil.com" would otherwise leave the site.</summary>
    public static string SafeNext(string next) =>
        !next.StartsWith('/') || next.StartsWith("//", StringComparison.Ordinal) || next.Any(ch => ch == '\\' || ch <= ' ') ? "/" : next;

    /// <summary>The key login cookies are signed with, kept in web_secret so logins survive restarts, updates, and a
    /// switch between engines.</summary>
    static byte[] WebSecret(Config cfg)
    {
        string path = Path.Combine(cfg.Home, "web_secret");
        try
        {
            if (File.Exists(path)) return Convert.FromHexString(Py.Strip(Py.ReadText(path)));
            Directory.CreateDirectory(cfg.Home);
            byte[] key = RandomNumberGenerator.GetBytes(32);
            Py.WriteText(path, Convert.ToHexStringLower(key));
            Py.OwnerOnly(path);
            return key;
        }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException)
        {
            return RandomNumberGenerator.GetBytes(32);
        }
    }

    // --- who is asking ---------------------------------------------------------------------------------------

    string Sign(string what) => Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(what)));

    static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    static bool IsLocal(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return (ip.Equals(IPAddress.Loopback) || ip.Equals(IPAddress.IPv6Loopback))
            && !ProxyHeaders.Any(h => ctx.Request.Headers.ContainsKey(h));
    }

    /// <summary>"admin" (everything) for you: on this computer, with no password set, or logged in.</summary>
    string? RoleOf(HttpContext ctx)
    {
        string cookie = ctx.Request.Cookies["pool"] ?? "";
        if (IsLocal(ctx) || cfg.PoolPassword.Length == 0) return "admin";
        if (cookie.Length > 0 && new[] { "admin", "member" }.Any(w => Same(cookie, Sign(w)))) return "admin"; // 0.2 cookies too
        return null;
    }

    /// <summary>The role, or the redirect to the login page (one person, one password: members are admins).</summary>
    (string? Role, IResult? Login) Member(HttpContext ctx)
    {
        string? role = RoleOf(ctx);
        if (role is not null) return (role, null);
        string query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value! : "";
        return (null, Http.SeeOther("/login?next=" + Ui.Quote(ctx.Request.Path.Value + query, "")));
    }

    /// <summary>The laptop's key: Authorization: Bearer &lt;password&gt;. Null when it may come in.</summary>
    IResult? RequireKey(HttpContext ctx)
    {
        if (cfg.PoolPassword.Length == 0) return null;
        string h = ctx.Request.Headers.Authorization.ToString();
        string key = h.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : ctx.Request.Headers["x-pool-key"].ToString();
        return Same(key, cfg.PoolPassword) ? null : Http.Detail(401, "wrong password");
    }

    // --- rendering -----------------------------------------------------------------------------------------

    PageContext Context(string? role, string? current = null, (string, string)? back = null)
    {
        var counts = new Dictionary<string, int>();
        foreach (var (name, n) in store.ClassesSummary()) counts[name] = n;
        var names = cfg.ClassNames();
        var classes = names.Select(n => (n, counts.GetValueOrDefault(n, 0))).ToList();
        classes.AddRange(counts.Where(kv => !names.Contains(kv.Key) && kv.Key != Configs.Unsorted).Select(kv => (kv.Key, kv.Value)));
        classes.Add((Configs.Unsorted, counts.GetValueOrDefault(Configs.Unsorted, 0)));
        return new PageContext
        {
            PoolName = cfg.PoolName, Classes = classes, Total = counts.Values.Sum(), Processing = store.Processing().Count,
            Admin = role == "admin", Current = current, Password = cfg.PoolPassword.Length > 0, Back = back, Nonce = options.Nonce(),
            Chat = ChatOn,
            Inbox = ChatOn ? (Directory.Exists(Path.Combine(cfg.PoolDir, "Inbox")) ? Directory.EnumerateFiles(Path.Combine(cfg.PoolDir, "Inbox"), "*.md").Count() : 0) : null,
            Due = CanvasOn ? StudyStash.Core.Canvas.Assignments.Upcoming(StudyStash.Core.Canvas.Assignments.Load(cfg.Home), DateTime.Now, 7).Count : null,
        };
    }

    static string Csp(string nonce) => PageText.LibraryCsp.Replace("{nonce}", nonce);

    static IResult Respond(string body, string nonce, int status = 200) => Http.Html(body, Csp(nonce), status);

    static IResult Show(string title, string body, PageContext ctx, bool math = false) =>
        Respond(Ui.Page(title, body, ctx, math), ctx.Nonce);

    static string Plural(int n, string one, string many) => n != 1 ? many : one;

    /// <summary>What writes the notes: the Ollama model, or the AI picked in Settings.</summary>
    string NotesWriter => options.Ai?.Describe("notes", cfg) ?? cfg.EffectiveSummaryModel;

    string QueuePanel(bool adminView)
    {
        var rows = store.Processing();
        if (rows.Count == 0) return "";
        string model = NotesWriter;
        var items = new List<string>();
        foreach (var r in rows)
        {
            string retry = "", lead, state;
            if (r.Status == Store.Working)
            {
                lead = "<span class=\"spin\" aria-hidden=\"true\"></span>";
                state = $"Writing the summary now with {Ui.Esc(model)}";
            }
            else if (r.Status == Store.Failed)
            {
                lead = "<span class=\"dot\" style=\"--tag:var(--red)\"></span>";
                state = $"Failed: {Ui.Esc(Py.Head(r.Error ?? "", 160))}";
                if (adminView)
                    retry = $"<form method=\"post\" action=\"/note/{Ui.Quote(r.Id, "")}/resummarize\"><button>Try again</button></form>";
            }
            else
            {
                lead = "<span class=\"dot\" style=\"--tag:var(--label-3)\"></span>";
                state = "Waiting its turn";
            }
            string title = Ui.Esc(FirstOf(r.LectureTitle, r.Title, "Untitled"));
            if (!string.IsNullOrEmpty(r.MdPath)) title = $"<a href=\"/note/{Ui.Quote(r.Id, "")}\">{title}</a>";
            items.Add($"<div class=\"row\">{lead}<div class=\"grow\"><div class=\"title\">{title}</div>"
                + $"<div class=\"subtitle\">{state}</div></div>{retry}</div>");
        }
        string refresh = rows.Any(r => r.Status != Store.Failed) ? " data-refresh=\"20\"" : "";
        return $"<section id=\"queue\"{refresh}><h2>Being written</h2><div class=\"group\">{string.Concat(items)}</div>"
            + $"<p class=\"group-foot\">Summaries are written on this computer with {Ui.Esc(model)}. A lecture takes a "
            + "minute or two. This page refreshes on its own.</p></section>";
    }

    static string FirstOf(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

    IResult NotFound(string role, string what = "note")
    {
        var ctx = Context(role);
        string body = $"<h1>No such {what}</h1><p class=\"sub\">It may have been deleted or moved. "
            + "<a href=\"/\">Back to recent lectures</a></p>";
        return Respond(Ui.Page("Not found", body, ctx), ctx.Nonce, 404);
    }

    /// <summary>A class name from the address: ASP.NET leaves "%2F" encoded, so a class like "Lab / A" still opens.</summary>
    static string RouteName(string raw) => raw.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    // --- the routes ----------------------------------------------------------------------------------------------

    void Map(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers["X-Granola-Share"] = Engine.Version; // setup tells our server from another app on the port
                return Task.CompletedTask;
            });
            await next();
        });

        app.MapGet("/login", (string? next, int? bad) => LoginForm(next ?? "/", bad ?? 0));
        app.MapPost("/login", Http.Handle(Login));
        app.MapGet("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete("pool");
            return Http.SeeOther("/login");
        });

        app.MapGet("/", Home);
        app.MapGet("/class/{name}", (HttpContext ctx, string name) => ByClass(ctx, RouteName(name)));
        app.MapGet("/unsorted", (HttpContext ctx) => WithMember(ctx, role =>
            ClassPage(Configs.Unsorted, role, "unsorted", "The sorter wasn't sure where these go. Open one and pick its class.")));
        app.MapGet("/search", (HttpContext ctx, string? q) => WithMember(ctx, role => Search(role, q ?? "")));
        app.MapGet("/note/{noteId}", (HttpContext ctx, string noteId) => WithMember(ctx, role => Note(role, noteId)));
        app.MapPost("/note/{noteId}/class", Move);
        app.MapPost("/note/{noteId}/resummarize", (HttpContext ctx, string noteId) => WithMember(ctx, _ =>
        {
            if (store.Requeue(noteId)) pipeline.Wake();
            return Http.SeeOther($"/note/{Ui.Quote(noteId, "")}");
        }));
        app.MapPost("/note/{noteId}/delete", (HttpContext ctx, string noteId) => WithMember(ctx, _ =>
        {
            store.Delete(noteId);
            return Http.SeeOther("/");
        }));
        app.MapGet("/note/{noteId}/download", (HttpContext ctx, string noteId) => WithMember(ctx, _ =>
        {
            var r = store.Get(noteId);
            if (r is null || string.IsNullOrEmpty(r.MdPath) || !File.Exists(r.MdPath)) return Http.Detail(404, "Not Found");
            return Results.File(r.MdPath, "text/markdown; charset=utf-8", Path.GetFileName(r.MdPath));
        }));
        app.MapGet("/class/{name}/zip", (HttpContext ctx, string name) => WithMember(ctx, _ => ClassZip(RouteName(name))));

        app.MapGet("/settings", Settings);
        app.MapPost("/settings", Http.Handle(SaveSettings));
        app.MapPost("/settings/resummarize-all", (HttpContext ctx) => WithMember(ctx, _ =>
        {
            int n = store.RequeueAll();
            pipeline.Wake();
            return Http.SeeOther($"/settings?queued={n}");
        }));
        app.MapPost("/settings/update", Http.Handle(UpdateNow));
        Icons.Map(app);

        app.MapGet("/api/health", (HttpContext ctx) => RequireKey(ctx) ?? Http.Json(new JsonObject
        {
            ["ok"] = true, ["pool_name"] = cfg.PoolName,
            ["classes"] = new JsonArray(cfg.ClassNames().Select(n => (JsonNode?)n).ToArray()),
            ["version"] = Engine.Version, ["notes"] = store.ClassesSummary().Sum(c => c.Count),
        }));
        app.MapPost("/api/ingest", Http.Handle(Ingest));
        app.MapGet("/api/notes/{noteId}/status", (HttpContext ctx, string noteId) =>
        {
            if (RequireKey(ctx) is IResult refused) return refused;
            var r = store.Get(noteId);
            if (r is null) return Http.Detail(404, "no such note");
            return Http.Json(new JsonObject
            {
                ["id"] = noteId, ["status"] = string.IsNullOrEmpty(r.Status) ? Store.Done : r.Status, ["class_name"] = r.ClassName,
                ["summary_model"] = r.SummaryModel, ["has_transcript"] = r.HasTranscript is > 0,
                ["path"] = $"/note/{Ui.Quote(noteId, "")}", ["lecture_title"] = r.LectureTitle,
            });
        });
        app.MapGet("/api/notes", (HttpContext ctx, string? class_name) => WithMember(ctx, _ => Http.Json(new JsonArray(
            store.ListNotes(class_name).Select(r => (JsonNode?)new JsonObject
            {
                ["id"] = r.Id, ["title"] = r.Title, ["lecture_title"] = r.LectureTitle, ["date"] = r.Date, ["owner"] = r.Owner,
                ["class_name"] = r.ClassName, ["confidence"] = r.Confidence, ["classified_by"] = r.ClassifiedBy,
                ["has_transcript"] = r.HasTranscript, ["summary_model"] = r.SummaryModel,
                ["topics"] = JsonNode.Parse(string.IsNullOrEmpty(r.Topics) ? "[]" : r.Topics),
            }).ToArray()))));
        app.MapGet("/api/status", (HttpContext ctx) => WithMember(ctx, _ => Http.Json(new JsonObject
        {
            ["counts"] = new JsonObject(store.StatusCounts().Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["working_on"] = pipeline.Current, ["summary_model"] = NotesWriter, ["version"] = Engine.Version,
        })));

        MapApp(app);
        MapCanvas(app);
        MapChat(app);
        MapFiles(app);
        MapInbox(app);
        app.MapFallback(() => Http.Detail(404, "Not Found"));
    }

    IResult WithMember(HttpContext ctx, Func<string, IResult> page)
    {
        var (role, login) = Member(ctx);
        return login ?? page(role!);
    }

    async Task<IResult> WithMemberAsync(HttpContext ctx, Func<string, Task<IResult>> page)
    {
        var (role, login) = Member(ctx);
        return login ?? await page(role!);
    }

    // --- login ---------------------------------------------------------------------------------------------------

    IResult LoginForm(string next, int bad)
    {
        string nonce = options.Nonce();
        string err = bad != 0 ? "<p class=\"bad\">That password is not right.</p>" : "";
        string letter = Ui.Esc(Py.Head(cfg.PoolName.Length > 0 ? cfg.PoolName : "L", 1).ToUpperInvariant());
        string body = $"<div class=\"login\"><form method=\"post\" action=\"/login\"><div class=\"appicon\" aria-hidden=\"true\">{letter}</div>"
            + $"<h1>{Ui.Esc(cfg.PoolName)}</h1><p class=\"muted\">Enter your password to continue.</p>{err}"
            + "<input type=\"password\" name=\"password\" autocomplete=\"current-password\" placeholder=\"Password\" "
            + "aria-label=\"Password\" autofocus required>"
            + $"<input type=\"hidden\" name=\"next\" value=\"{Ui.Esc(next)}\">"
            + "<button class=\"primary\">Open</button></form></div>";
        return Respond(Ui.BarePage($"Log in · {cfg.PoolName}", body, nonce), nonce);
    }

    async Task<IResult> Login(HttpContext ctx)
    {
        var form = await Http.FormAsync(ctx.Request);
        string password = form.Get("password"), next = SafeNext(form.Get("next", "/"));
        // The 0.2 admin password still works, so an old bookmark or password manager entry isn't a dead end.
        if (!new[] { cfg.PoolPassword, cfg.AdminPassword }.Any(p => p.Length > 0 && Same(password, p)))
            return Http.SeeOther($"/login?bad=1&next={Ui.Quote(next, "")}");
        ctx.Response.Cookies.Append("pool", Sign("admin"), new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromDays(180), Path = "/",
        });
        return Http.SeeOther(next);
    }

    // --- browsing --------------------------------------------------------------------------------------------------

    IResult Home(HttpContext ctx) => WithMember(ctx, role =>
    {
        var c = Context(role, "home");
        var rows = store.ListNotes(limit: 40);
        int nClasses = c.Classes.Count(x => x.Count > 0 && x.Name != Configs.Unsorted);
        string lede = c.Total > 0
            ? $"{c.Total} lecture{Plural(c.Total, "", "s")} across {nClasses} class{Plural(nClasses, "", "es")}." : "";
        string empty = c.Processing > 0 ? ""
            : "<div class=\"empty\"><strong>No lectures yet</strong>Lectures you record in Granola show up here "
              + "once your laptop sends them. To connect it, see <a href=\"/settings\">Settings</a>.</div>";
        // Phones have no sidebar: the classes are a list here instead, like folders in Notes.
        string folders = string.Concat(c.Classes.Select(x =>
            $"<a class=\"row chev\" href=\"{(x.Name != Configs.Unsorted ? Ui.ClassUrl(x.Name) : "/unsorted")}\">"
            + $"<span class=\"dot\" style=\"{Ui.HueStyle(x.Name)}\"></span><span class=\"grow\">{Ui.Esc(x.Name)}</span>"
            + $"<span class=\"value\">{x.Count}</span></a>"));
        string body = $"<h1>{Ui.Esc(cfg.PoolName)}</h1>" + (lede.Length > 0 ? $"<p class=\"sub\">{lede}</p>" : "")
            + $"<div class=\"only-narrow\">{Ui.SearchBox()}<h2>Classes</h2><div class=\"group\">{folders}</div></div>"
            + QueuePanel(role == "admin")
            + (rows.Count > 0 ? $"<h2>Recent lectures</h2>{Ui.NoteList(rows, "", "")}" : empty);
        return Show(cfg.PoolName, body, c);
    });

    IResult ClassPage(string name, string role, string current, string lede, bool terminal = false, string? said = null)
    {
        var c = Context(role, current, ("/", cfg.PoolName));
        var rows = store.ListNotes(name);
        string canvasPart = ClassCanvas(name) + ClassFiles(name);
        // On the library's own computer, a class can open in a terminal with the AI (Claude Code by default).
        string open = terminal
            ? $"<form method=\"post\" action=\"/terminal\"><input type=\"hidden\" name=\"class\" value=\"{Ui.Esc(name)}\"><button>Open in {Ui.Esc(AgentCli)}</button></form>"
            : "";
        string zip = rows.Count > 0 || open.Length > 0
            ? $"<div class=\"toolbar\" style=\"margin:0 0 1.4rem\">{(rows.Count > 0 ? $"<a class=\"btn\" href=\"{Ui.ClassUrl(name)}/zip\">Download all as .zip</a>" : "")}{open}</div>"
              + (said is not null ? $"<div class=\"notice\"><div>{Ui.Esc(said)}</div></div>" : "")
            : "";
        string body = $"<h1>{Ui.Esc(name)}</h1><p class=\"sub\"><span class=\"tag\" style=\"{Ui.HueStyle(name)}\">{lede}</span></p>"
            + zip + canvasPart + (canvasPart.Length > 0 ? "<h2>Lectures</h2>" : "") + Ui.NoteList(rows, "Nothing here yet",
                name != Configs.Unsorted ? "Lectures sorted into this class show up here." : "Every lecture found its class.");
        return Show(name, body, c);
    }

    /// <summary>"Claude Code", "Codex" or "Antigravity": what Open in… starts.</summary>
    string AgentCli => AiSettings.Load(cfg.Home).For("agent").Provider switch { "codex" or "ollama" => "Codex", "gemini" => "Antigravity", _ => "Claude Code" };

    IResult ByClass(HttpContext ctx, string name) => WithMember(ctx, role =>
    {
        if (name == Configs.Unsorted) return Http.SeeOther("/unsorted");
        string desc = cfg.Classes.FirstOrDefault(c => c.Name == name)?.Description ?? "";
        int n = store.ListNotes(name).Count;
        string lede = Ui.Esc(desc + (desc.Length > 0 && !desc.EndsWith('.') ? ". " : desc.Length > 0 ? " " : ""))
            + $"{n} lecture{Plural(n, "", "s")}.";
        return ClassPage(name, role, $"class:{name}", lede, terminal: ChatOn && IsLocal(ctx), said: ctx.Request.Query["said"].FirstOrDefault());
    });

    IResult Search(string role, string query)
    {
        var c = Context(role, back: ("/", cfg.PoolName));
        string q = Py.Strip(query);
        c.Q = q;
        var rows = q.Length > 0 ? store.Search(q) : [];
        var snippets = new Dictionary<string, string>();
        foreach (var r in rows)
        {
            var m = store.Meeting(r);
            string text = string.Join(" ", new[] { r.SummaryMd, m.NotesMarkdown, m.Transcript }.Where(s => !string.IsNullOrEmpty(s)));
            snippets[r.Id] = Ui.Snippet(text, q);
        }
        string found = q.Length > 0 ? $"{rows.Count} lecture{Plural(rows.Count, "", "s")} mention “{Ui.Esc(q)}”." : "";
        string fileHits = FileResults(q);
        string body = "<h1>Search</h1>" + $"<div class=\"only-narrow\">{Ui.SearchBox(q)}</div>"
            + (q.Length > 0 ? $"<p class=\"sub\">{found}</p>" : "")
            + (fileHits.Length > 0 && rows.Count == 0 ? "" : q.Length > 0 ? Ui.NoteList(rows, "No matches", "Try a shorter word, a topic, or a name.", snippets) : "")
            + fileHits;
        return Show(q.Length > 0 ? $"Search: {q}" : "Search", body, c);
    }

    IResult Note(string role, string noteId)
    {
        var r = store.Get(noteId);
        if (r is null) return NotFound(role);
        var c = Context(role, r.ClassName != Configs.Unsorted ? $"class:{r.ClassName}" : "unsorted");
        var m = store.Meeting(r);
        string nid = Ui.Quote(noteId, "");
        string cls = r.ClassName ?? "";
        string options = string.Concat(cfg.ClassNames().Append(Configs.Unsorted).Select(x =>
            $"<option value=\"{Ui.Esc(x)}\"{(x == cls ? " selected" : "")}>{Ui.Esc(x)}</option>"));
        // The class is a popup menu right where it's shown: picking another moves the lecture.
        string picker = $"<form class=\"classpick\" method=\"post\" action=\"/note/{nid}/class\" data-autosubmit>"
            + $"<span class=\"dot\" style=\"{Ui.HueStyle(cls)}\"></span>"
            + $"<select name=\"class_name\" aria-label=\"Class (pick another to move this lecture)\">{options}</select>"
            + "<button class=\"js-hide\">Move</button></form>";
        var about = new List<(string, string)> { ("Class", picker), ("Date", Ui.Esc(Ui.LongDate(r.Date))) };
        if (!string.IsNullOrEmpty(r.SummaryMd)) about.Add(("Summary", $"Notes by {Ui.Esc(r.SummaryModel)}, from the transcript"));
        else about.Add(("Summary", "Granola's summary" + (r.HasTranscript is > 0 ? "" : " (no transcript shared)")));
        if (!string.IsNullOrEmpty(r.ClassifiedBy))
        {
            string sure = r.ClassifiedBy == "ollama"
                ? $", {Math.Round((r.Confidence ?? 0) * 100, MidpointRounding.ToEven).ToString(CultureInfo.InvariantCulture)}% sure" : "";
            about.Add(("Sorted by", "Sorted by " + Ui.Esc(SortedBy.GetValueOrDefault(r.ClassifiedBy, r.ClassifiedBy) + sure)));
        }
        string aboutHtml = string.Concat(about.Select(a => $"<div><dt>{Ui.Esc(a.Item1)}</dt><dd>{a.Item2}</dd></div>"));

        var actions = new List<string> { $"<a class=\"btn\" href=\"/note/{nid}/download\">Download .md</a>" };
        if (role == "admin")
        {
            if (Py.Strip(m.Transcript).Length > 0)
                actions.Add($"<form method=\"post\" action=\"/note/{nid}/resummarize\"><button>Rewrite summary</button></form>");
            actions.Add($"<form method=\"post\" action=\"/note/{nid}/delete\" data-confirm=\"Delete this lecture? Its file is removed too.\">"
                + "<button class=\"danger\">Delete</button></form>");
        }

        string notice = "";
        if (r.Status is Store.Queued or Store.Working)
            notice = "<div class=\"notice\" data-refresh=\"15\"><div>A new summary is being written with "
                + $"{Ui.Esc(NotesWriter)}. This page refreshes on its own.</div></div>";
        else if (!string.IsNullOrEmpty(r.Error))
        {
            string retry = role == "admin" ? $"<form method=\"post\" action=\"/note/{nid}/resummarize\"><button>Try again</button></form>" : "";
            notice = $"<div class=\"notice bad\"><div>{Ui.Esc(Py.Head(r.Error, 300))}. Showing Granola's summary instead.{retry}</div></div>";
        }

        var docs = new List<(string Id, string Label, string Html)>();
        if (!string.IsNullOrEmpty(r.SummaryMd))
            docs.Add(("summary", "Summary", $"<div class=\"prose\">{Ui.RenderMd(r.SummaryMd)}</div>"
                + $"<p class=\"byline\">Written by {Ui.Esc(r.SummaryModel)} from the transcript.</p>"));
        if (Py.Strip(m.NotesMarkdown).Length > 0 && (string.IsNullOrEmpty(r.SummaryMd) || cfg.KeepGranolaNotes))
            docs.Add(("granola", !string.IsNullOrEmpty(r.SummaryMd) ? "Granola's summary" : "Summary",
                $"<div class=\"prose\">{Ui.RenderMd(m.NotesMarkdown)}</div>"));
        if (Py.Strip(m.PrivateNotes).Length > 0) docs.Add(("typed", "Typed notes", $"<div class=\"prose\">{Ui.RenderMd(m.PrivateNotes)}</div>"));
        if (Py.Strip(m.Transcript).Length > 0) docs.Add(("transcript", "Transcript", $"<div class=\"transcript\">{Ui.Esc(Py.Strip(m.Transcript))}</div>"));
        if (docs.Count == 0) docs.Add(("summary", "Summary", "<p class=\"muted\">Granola returned no notes for this lecture.</p>"));
        string tabs = string.Concat(docs.Select(d =>
            $"<button type=\"button\" role=\"tab\" data-for=\"{d.Id}\" aria-controls=\"{d.Id}\">{Ui.Esc(d.Label)}</button>"));
        string panes = string.Concat(docs.Select(d => $"<section id=\"{d.Id}\" role=\"tabpanel\">{d.Html}</section>"));
        string tabbar = docs.Count > 1 ? $"<div class=\"seg\" role=\"tablist\" data-tabs>{tabs}</div>" : "<div style=\"height:1.6rem\"></div>";

        string title = FirstOf(r.LectureTitle, r.Title, "Untitled");
        var back = (cls != Configs.Unsorted ? Ui.ClassUrl(cls) : "/unsorted", cls);
        c.Back = back;
        string body = $"<a class=\"back only-wide\" href=\"{back.Item1}\">{Ui.Esc(back.cls)}</a>"
            + $"<h1>{Ui.Esc(title)}</h1><dl class=\"about\">{aboutHtml}</dl>"
            + $"<div class=\"toolbar\">{string.Concat(actions)}</div>"
            + $"<div style=\"height:1.4rem\"></div>{notice}{tabbar}<div class=\"sheet\">{panes}</div>";
        return Show(title, body, c, math: true);
    }

    async Task<IResult> Move(HttpContext ctx, string noteId) => await WithMemberAsync(ctx, async _ =>
    {
        string className = (await Http.FormAsync(ctx.Request)).Get("class_name");
        if (!cfg.ClassNames().Append(Configs.Unsorted).Contains(className)) return Http.Detail(400, "unknown class");
        store.SetClass(noteId, className, keepGranola: cfg.KeepGranolaNotes);
        return Http.SeeOther($"/note/{Ui.Quote(noteId, "")}");
    });

    IResult ClassZip(string name)
    {
        var rows = store.ListNotes(name);
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.MdPath) || !File.Exists(r.MdPath)) continue;
                zip.CreateEntryFromFile(r.MdPath, $"{name}/{Path.GetFileName(r.MdPath)}", CompressionLevel.Optimal);
            }
        }
        buffer.Position = 0;
        return new ZipResult(buffer, name);
    }

    sealed class ZipResult(MemoryStream zip, string name) : IResult
    {
        public async Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.ContentType = "application/zip";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename*=UTF-8''{Ui.Quote(name)}.zip";
            await zip.CopyToAsync(ctx.Response.Body);
        }
    }

    // --- settings ---------------------------------------------------------------------------------------------

    static string ModelSelect(string field, string current, List<(string Name, double SizeGb)>? models, string? blank = null)
    {
        var names = (models ?? []).Select(m => m.Name).ToList();
        var opts = new List<string>();
        if (blank is not null) opts.Add($"<option value=\"\">{Ui.Esc(blank)}</option>");
        if (current.Length > 0 && !names.Contains(current))
            opts.Add($"<option value=\"{Ui.Esc(current)}\" selected>{Ui.Esc(current)} (not installed)</option>");
        foreach (var m in models ?? [])
            opts.Add($"<option value=\"{Ui.Esc(m.Name)}\"{(m.Name == current ? " selected" : "")}>{Ui.Esc(m.Name)} ({Ui.Esc(Ollama.SizeLabel(m.SizeGb))})</option>");
        return $"<select id=\"{field}\" name=\"{field}\">{string.Concat(opts)}</select>";
    }

    async Task<IResult> Settings(HttpContext ctx, int? saved, int? queued, string? canvas, string? folders) => await WithMemberAsync(ctx, async role =>
    {
        var c = Context(role, "settings", ("/", cfg.PoolName));
        var models = await options.ListModels(cfg.OllamaHost);
        string flash = "";
        if (saved is int s && s != 0) flash = "<div class=\"notice good\"><div>Settings saved. New lectures use them right away.</div></div>";
        if (queued is int q && q >= 0)
            flash = $"<div class=\"notice good\"><div>{q} lecture{Plural(q, "", "s")} queued for a new summary.</div></div>";

        string aiState = models is null
            ? $"<div class=\"notice\"><div>Ollama isn't answering at {Ui.Esc(cfg.OllamaHost)}, so summaries and AI "
              + "sorting are paused. Open the Ollama app on this computer, then reload.</div></div>"
            : models.Count == 0
                ? "<div class=\"notice\"><div>Ollama has no models yet. On this computer run "
                  + $"<code>ollama pull {Ui.Esc(Ollama.RecommendedModel(options.RamGb()))}</code>, then reload.</div></div>"
                : "";
        static string Checked(bool b) => b ? " checked" : "";
        static string Switch(string name, bool on, string text) =>
            $"<label class=\"row\"><span class=\"grow\">{text}</span><input class=\"switch\" type=\"checkbox\" name=\"{name}\" value=\"1\"{Checked(on)}></label>";

        // Which AI does the work (ai.json): Ollama on this computer, or Claude, ChatGPT or Gemini with your own account.
        var picked = AiSettings.Load(cfg.Home);
        var providers = AiProviders.All(() => cfg.OllamaHost);
        string ProviderSelect(string field, string current, string? blank)
        {
            var opts = blank is null ? "" : $"<option value=\"\">{Ui.Esc(blank)}</option>";
            foreach (var p in providers)
                opts += $"<option value=\"{p.Id}\"{(p.Id == current ? " selected" : "")}>{Ui.Esc(p.Name)}{(p.Available() ? "" : " (not installed)")}</option>";
            return $"<select id=\"{field}\" name=\"{field}\">{opts}</select>";
        }
        string JobRow(string job, string label) =>
            $"<div class=\"row\"><label class=\"grow\" for=\"ai_job_{job}\">{label}</label>"
            + ProviderSelect($"ai_job_{job}", picked.ByJob.TryGetValue(job, out var jc) ? jc.Provider : "", "Same as above") + "</div>";
        var main = providers.First(p => p.Id == picked.Provider);
        string modelOpts = string.Concat(main.Models.Select(m =>
            $"<option value=\"{Ui.Esc(m.Id)}\"{(m.Id == picked.Models.GetValueOrDefault(main.Id, "") ? " selected" : "")}>{Ui.Esc(m.Label)}</option>"));
        string tested = picked.Tests.GetValueOrDefault(main.Id, "") switch
        {
            "works" => "<span class=\"value\">Works</span>",
            "" => "",
            string why => $"<span class=\"value\" title=\"{Ui.Esc(why)}\">Didn't answer</span>",
        };
        string brain = "<div class=\"group-head\">AI</div><div class=\"group\">"
            + $"<div class=\"row\"><label class=\"grow\" for=\"ai_provider\">Does the work</label>{tested}{ProviderSelect("ai_provider", picked.Provider, null)}</div>"
            + (main.Id == "ollama" ? "" : $"<div class=\"row\"><label class=\"grow\" for=\"ai_model\">Model</label><select id=\"ai_model\" name=\"ai_model\">{modelOpts}</select></div>")
            + JobRow("notes", "Writes study notes") + JobRow("sort", "Sorts lectures") + JobRow("ask", "Answers questions")
            + (Terminal.Available() is { Count: > 0 } terms
                ? "<div class=\"row\"><label class=\"grow\" for=\"terminal\">Open in… uses</label><select id=\"terminal\" name=\"terminal\">"
                  + string.Concat(terms.Select(t => $"<option value=\"{t.Id}\"{(t.Id == picked.Terminal ? " selected" : "")}>{Ui.Esc(t.Name)}</option>")) + "</select></div>"
                : "")
            + "</div><p class=\"group-foot\">Claude, ChatGPT and Gemini run through their own apps (Claude Code, Codex, "
            + "Antigravity), signed in with your own account, so they use your plan. The local model stays on this computer "
            + "and costs nothing.</p>";

        string ai = brain + $"<div class=\"group-head\">Local models</div>{aiState}<div class=\"group\">"
            + "<div class=\"row\"><label class=\"grow\" for=\"summary_model\">Writes summaries</label>"
            + $"{ModelSelect("summary_model", cfg.SummaryModel, models, "Same as the sorting model")}</div>"
            + "<div class=\"row\"><label class=\"grow\" for=\"ollama_model\">Sorts lectures into classes</label>"
            + $"{ModelSelect("ollama_model", cfg.OllamaModel, models)}</div></div>"
            + "<p class=\"group-foot\">The summary model reads each transcript and writes the lecture notes. Bigger "
            + "models write better notes but take longer. Using the same model for both avoids reloading it between "
            + "the two steps.</p>"
            + "<div class=\"group-head\">Notes and sorting</div><div class=\"group\">"
            + Switch("summary_enabled", cfg.SummaryEnabled, "Write our own summary when a transcript is shared")
            + Switch("keep_granola_notes", cfg.KeepGranolaNotes, "Also keep Granola's summary next to ours")
            + Switch("ollama_enabled", cfg.OllamaEnabled, "Use AI to sort lectures into classes")
            + "<div class=\"row\"><label class=\"grow\" for=\"min_confidence\">Minimum confidence to file a lecture</label>"
            + "<input id=\"min_confidence\" name=\"min_confidence\" type=\"number\" min=\"0\" max=\"1\" step=\"0.05\" "
            + $"value=\"{Py.FloatRepr(cfg.MinConfidence)}\" style=\"width:5.5rem\"></div></div>"
            + "<p class=\"group-foot\">Below the minimum confidence, a lecture goes to Unsorted for you to file. With AI "
            + "off, only Granola folder and title rules sort lectures.</p>";

        var rows = new List<string>();
        var all = cfg.Classes.Append(new ClassDef("")).ToList();
        for (int i = 0; i < all.Count; i++)
        {
            var k = all[i];
            string remove = k.Name.Length > 0
                ? $"<label class=\"remove\">Remove<input class=\"switch\" type=\"checkbox\" name=\"class_remove_{i}\" value=\"1\"></label>"
                : "<span></span>";
            rows.Add("<div class=\"fields class-edit\">"
                + $"<input type=\"text\" name=\"class_name_{i}\" value=\"{Ui.Esc(k.Name)}\" placeholder=\"New class, e.g. CS 101\" aria-label=\"Class name\">"
                + $"<input type=\"text\" name=\"class_aliases_{i}\" value=\"{Ui.Esc(string.Join(", ", k.Aliases))}\" placeholder=\"Other names\" aria-label=\"Other names, comma separated\">"
                + remove
                + $"<input class=\"wide\" type=\"text\" name=\"class_desc_{i}\" value=\"{Ui.Esc(k.Description)}\" placeholder=\"What it covers (helps the AI)\" aria-label=\"Description\">"
                + "</div>");
        }
        string classes = $"<div class=\"group-head\">Classes</div><div class=\"group\">{string.Concat(rows)}</div>"
            + "<p class=\"group-foot\">Lectures are sorted into these. A Granola folder or a title that matches a "
            + "name here files the lecture without asking the AI. Removing a class keeps its lectures; move them "
            + "from their pages. Fill in the last row to add a class.</p>";
        string form = $"<form method=\"post\" action=\"/settings\">{ai}{classes}"
            + "<div class=\"actions\"><button class=\"primary\">Save settings</button></div></form>";

        var ts = options.Tailscale();
        string url = HostInfo.ServerUrls(cfg.WebPort, ts, options.HostName())[0];
        var (mac, windows) = HostInfo.InviteCommands(url, cfg.PoolPassword);
        string tsNote = ts.Running ? ""
            : "<div class=\"notice\"><div>Tailscale isn't running on this computer, so your laptop can only reach "
              + "it on the same Wi-Fi. Install Tailscale on both and sign in to the same account.</div></div>";
        static string Command(string label, string cid, string text) =>
            "<div class=\"fields\"><div class=\"toolbar\" style=\"justify-content:space-between\">"
            + $"<strong>{label}</strong><button type=\"button\" class=\"link\" data-copy=\"{cid}\">Copy</button></div>"
            + $"<pre class=\"code\" id=\"{cid}\">{Ui.Esc(text)}</pre></div>";
        const string dl = "https://github.com/Joseph-Rus/study-stash/releases/latest/download";
        string invite = $"<div class=\"group-head\">Connect your laptop</div>{tsNote}<div class=\"group\">"
            + $"<div class=\"row\"><span class=\"grow\">Address</span><span class=\"value\">{Ui.Esc(url)}</span></div>"
            + $"<div class=\"row\"><span class=\"grow\">Password</span><span class=\"value\">{Ui.Esc(cfg.PoolPassword.Length > 0 ? cfg.PoolPassword : "none")}</span></div>"
            + "<div class=\"row\"><span class=\"grow\">Study Stash for the laptop</span><span class=\"value\">"
            + $"<a href=\"{dl}/Study-Stash-Laptop.dmg\">Mac</a> · <a href=\"{dl}/Study-Stash-Laptop-Setup.exe\">Windows</a>"
            + "</span></div></div><p class=\"group-foot\">On the computer you record lectures on, install Study Stash, "
            + "open it, and enter this address and password.</p>"
            + "<details class=\"help\"><summary>Or with one line in a terminal</summary><div class=\"group\">"
            + Command("Mac or Linux", "inv-mac", mac) + Command("Windows", "inv-win", windows)
            + "</div><p class=\"group-foot\">Paste one into Terminal (Mac) or PowerShell (Windows). It installs "
            + "everything with this address and password filled in.</p></details>";

        var rel = await options.Latest(3600);
        string upd;
        if (Updates.IsNewer(rel))
        {
            string button = options.Apply is not null
                ? "<form method=\"post\" action=\"/settings/update\"><button class=\"primary\">Update now</button></form>" : "";
            upd = $"<div class=\"row\"><span class=\"grow\">Version {Ui.Esc(rel!.Tag)} is out "
                + $"(<a href=\"{Ui.Esc(rel.Page)}\">what changed</a>). You have {Engine.Version}.</span>{button}</div>";
        }
        else
        {
            upd = $"<div class=\"row\"><span class=\"grow\">Version {Engine.Version}</span><span class=\"value\">The newest</span></div>";
        }
        string auto = cfg.AutoUpdate ? "On" : "Off";
        string maintenance = $"<div class=\"group-head\">Updates</div><div class=\"group\">{upd}"
            + $"<div class=\"row\"><span class=\"grow\">Automatic updates</span><span class=\"value\">{auto}</span></div></div>"
            + "<p class=\"group-foot\">Set with auto_update in config.toml.</p>"
            + "<div class=\"group-head\">Rewrite every summary</div><div class=\"group\">"
            + "<form class=\"row\" method=\"post\" action=\"/settings/resummarize-all\" data-confirm=\"Rewrite all "
            + $"{c.Total} summaries with {Ui.Esc(NotesWriter)}? This can take a while.\">"
            + $"<span class=\"grow\">Rewrite all summaries with {Ui.Esc(NotesWriter)}</span>"
            + "<button>Rewrite all</button></form></div>"
            + "<p class=\"group-foot\">Useful after switching models. Lectures stay readable while they are "
            + "rewritten, one at a time.</p>";
        // Canvas shows once it's set up or asked about, so a library without it looks as it always has.
        string canvasGroup = CanvasOn || canvas is not null || Canvas.Settings.On ? CanvasSettingsGroup(canvas)
            : "<div class=\"group-head\" id=\"canvas\">Canvas</div><div class=\"group\"><form class=\"row\" method=\"get\" action=\"/settings#canvas\">"
              + "<input type=\"hidden\" name=\"canvas\" value=\"start\"><span class=\"grow\">Bring in assignments, feedback and course files from Canvas</span><button>Set up Canvas</button></form></div>";
        string body = $"<h1>Settings</h1>{flash}{form}{canvasGroup}{FoldersSettingsGroup(folders)}{invite}{maintenance}";
        return Show("Settings", body, c);
    });

    async Task<IResult> SaveSettings(HttpContext ctx) => await WithMemberAsync(ctx, async _ =>
    {
        var f = await Http.FormAsync(ctx.Request);
        cfg.SummaryModel = Py.Strip(f.Get("summary_model", cfg.SummaryModel));
        string sort = Py.Strip(f.Get("ollama_model", cfg.OllamaModel));
        if (sort.Length > 0) cfg.OllamaModel = sort;
        cfg.SummaryEnabled = f.Get("summary_enabled") == "1";
        cfg.KeepGranolaNotes = f.Get("keep_granola_notes") == "1";
        cfg.OllamaEnabled = f.Get("ollama_enabled") == "1";
        if (double.TryParse(Py.Strip(f.Get("min_confidence", Py.FloatRepr(cfg.MinConfidence))), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double confidence) && !double.IsNaN(confidence))
            cfg.MinConfidence = Math.Clamp(confidence, 0.0, 1.0);
        var classes = new List<ClassDef>();
        var seen = new HashSet<string>();
        for (int i = 0; f.ContainsKey($"class_name_{i}"); i++)
        {
            string name = Py.Strip(f.Get($"class_name_{i}"));
            if (name.Length == 0 || name == Configs.Unsorted || seen.Contains(name) || f.Get($"class_remove_{i}") == "1") continue;
            var aliases = f.Get($"class_aliases_{i}").Split(',').Select(Py.Strip).Where(a => a.Length > 0).ToList();
            classes.Add(new ClassDef(name, aliases, Py.Strip(f.Get($"class_desc_{i}"))));
            seen.Add(name);
        }
        cfg.Classes = classes;
        Configs.Save(cfg);
        if (f.ContainsKey("ai_provider"))
        {
            var picked = AiSettings.Load(cfg.Home);
            var known = AiProviders.All().Select(p => p.Id).ToHashSet();
            string provider = Py.Strip(f.Get("ai_provider"));
            if (known.Contains(provider))
            {
                if (provider != picked.Provider) picked.Models.Remove(provider); // a new AI starts on its default model
                else if (f.ContainsKey("ai_model")) picked.Models[provider] = Py.Strip(f.Get("ai_model"));
                picked.Provider = provider;
            }
            if (Terminal.Available().Any(t => t.Id == f.Get("terminal"))) picked.Terminal = f.Get("terminal");
            foreach (string job in new[] { "notes", "sort", "ask" })
            {
                string p = Py.Strip(f.Get($"ai_job_{job}"));
                if (known.Contains(p)) picked.ByJob[job] = new AiChoice(p);
                else picked.ByJob.Remove(job);
            }
            picked.Save(cfg.Home);
        }
        return Http.SeeOther("/settings?saved=1");
    });

    async Task<IResult> UpdateNow(HttpContext ctx) => await WithMemberAsync(ctx, async role =>
    {
        var rel = await options.Latest(0);
        if (Updates.IsNewer(rel) && options.Apply is { } apply) _ = Task.Run(() => apply(rel!, cfg.Home));
        string nonce = options.Nonce();
        string body = "<div class=\"login\"><form><span class=\"spin\" style=\"margin:0 auto .4rem;width:1.6rem;height:1.6rem\"></span>"
            + "<h1>Updating</h1><p class=\"muted\">This restarts on the new version in about a minute.</p>"
            + "<a class=\"btn primary\" href=\"/settings\">Back to settings</a></form></div>";
        return Respond(Ui.BarePage("Updating", body, nonce), nonce);
    });

    // --- the API ------------------------------------------------------------------------------------------------

    /// <summary>Your laptop sends one finished lecture. It is queued; the pipeline writes its notes and files it.</summary>
    async Task<IResult> Ingest(HttpContext ctx)
    {
        if (RequireKey(ctx) is IResult refused) return refused;
        var payload = await Http.JsonBodyAsync(ctx.Request);
        if (payload is null) return Http.Detail(422, "Input should be a valid dictionary");
        Meeting m;
        try
        {
            m = Wire.MeetingFromJson(payload);
        }
        catch (PayloadException e)
        {
            return Http.Detail(400, e.Message);
        }
        store.Enqueue(m);
        pipeline.Wake();
        var c = Classify.ByRules(m, cfg.Classes);
        return Http.Json(new JsonObject
        {
            ["id"] = m.Id, ["status"] = "queued", ["class_name"] = c?.ClassName, ["has_transcript"] = Py.Strip(m.Transcript).Length > 0,
        });
    }
}

/// <summary>The Study Stash icon: the browser tab, the Windows app's taskbar button, and an iPhone's home screen.</summary>
public static class Icons
{
    static readonly (string Path, string Resource, string Type)[] Files =
        [("/favicon.ico", "study-stash.ico", "image/x-icon"), ("/icon.png", "icon.png", "image/png"), ("/apple-touch-icon.png", "apple-touch-icon.png", "image/png")];

    /// <summary>One icon file's bytes (study-stash.ico, icon.png, apple-touch-icon.png), or null.</summary>
    public static byte[]? Bytes(string resource)
    {
        using var stream = typeof(Icons).Assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;
        var data = new MemoryStream();
        stream.CopyTo(data);
        return data.ToArray();
    }

    /// <summary>The icon files; they need no sign-in.</summary>
    public static void Map(WebApplication app)
    {
        foreach (var (path, resource, type) in Files)
        {
            app.MapGet(path, (HttpContext ctx) =>
            {
                using var stream = typeof(Icons).Assembly.GetManifestResourceStream(resource);
                if (stream is null) return Results.StatusCode(404);
                var data = new MemoryStream();
                stream.CopyTo(data);
                ctx.Response.Headers.CacheControl = "public, max-age=86400";
                return Results.Bytes(data.ToArray(), type);
            });
        }
    }
}
