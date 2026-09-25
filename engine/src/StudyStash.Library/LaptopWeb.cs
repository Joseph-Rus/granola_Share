using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using StudyStash.Core;

namespace StudyStash.Library;

/// <summary>
/// The Study Stash app on your laptop (client_app.py): a small page on this computer for setup and status. The
/// background watcher serves it on http://127.0.0.1:&lt;port&gt;, so setting up never needs the terminal: connect to
/// your library, sign in to Granola, choose how to send, done. Afterwards it shows what was sent and where it was
/// filed. Only this computer can reach it (127.0.0.1, and the Host header is checked), and it needs the token the
/// Study Stash launcher passes in, so websites and other programs can't drive it.
/// </summary>
public static class LaptopWeb
{
    public const int DefaultPort = 8765;

    static string Esc(string? s) => Ui.Esc(s ?? "");

    public static string WhereTheAppIs(string system) => system switch
    {
        "Darwin" => "Applications folder",
        "Windows" => "Start Menu",
        _ => "apps menu",
    };

    // --- first run: the installer hands over the library's address ----------------------------------------------

    /// <summary>The install line's address and password, for the setup page to fill in (deleted once connected).</summary>
    public static void WritePrefill(string home, string? server, string? key)
    {
        if (string.IsNullOrEmpty(server) && string.IsNullOrEmpty(key)) return;
        Directory.CreateDirectory(home);
        string path = Path.Combine(home, "ui_prefill.json");
        Py.WriteText(path, PyJson.Dumps(new JsonObject { ["server"] = server ?? "", ["key"] = key ?? "" }));
        Py.OwnerOnly(path);
    }

    public static JsonObject ReadPrefill(string home)
    {
        try
        {
            return Py.JsonLoads(Py.ReadText(Path.Combine(home, "ui_prefill.json"))) as JsonObject ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    static void ClearPrefill(string home)
    {
        try
        {
            File.Delete(Path.Combine(home, "ui_prefill.json"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>wizard._normalize_url: "mini:8787" → "http://mini:8787".</summary>
    public static string NormalizeUrl(string url)
    {
        url = Py.Strip(url).TrimEnd('/');
        return url.Length > 0 && !url.Contains("://", StringComparison.Ordinal) ? "http://" + url : url;
    }

    // --- pieces of the page ------------------------------------------------------------------------------------

    /// <summary>The newest dozen lectures the watcher has seen.</summary>
    public static List<JsonObject> Recent(ClientConfig cc, int limit = 12)
    {
        JsonObject? seen;
        try
        {
            seen = (Py.JsonLoads(Py.ReadText(cc.StatePath)) as JsonObject)?["seen"] as JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
        return (seen ?? []).Select(kv => kv.Value).OfType<JsonObject>()
            .OrderByDescending(e => Py.AsString(e["at"]) ?? "", StringComparer.Ordinal).Take(limit).ToList();
    }

    static bool IsTrue(JsonNode? v) => v is JsonValue j && j.GetValueKind() == System.Text.Json.JsonValueKind.True;

    static bool IsFalse(JsonNode? v) => v is JsonValue j && j.GetValueKind() == System.Text.Json.JsonValueKind.False;

    public static string Describe(JsonObject e)
    {
        string? d = Py.AsString(e["decision"]);
        if (d == "shared")
        {
            string where = Py.Truthy(e["class_name"]) ? $" in {Py.Str(e["class_name"])}" : "";
            if (IsTrue(e["filed"])) return $"Filed{where}" + (Py.Truthy(e["transcript_chars"]) ? " with its transcript" : "");
            return IsFalse(e["filed"]) ? "Sent, being summarized" : $"Shared{where}";
        }
        if (d == "pending" && Py.Truthy(e["error"])) return "Waiting to send" + (Py.Truthy(e["transcript_chars"]) ? " with its transcript" : "");
        return d switch { "skipped" => "Skipped", "pending" => "Waiting for your answer", _ => Py.Truthy(e["decision"]) ? Py.Str(e["decision"]) : "" };
    }

    public static string StateColor(JsonObject e) => Py.AsString(e["decision"]) switch
    {
        "shared" => IsFalse(e["filed"]) ? "var(--accent)" : "var(--green)",
        "pending" => "var(--orange)",
        _ => "var(--gray)",
    };

    public static JsonArray ReadyKey(LaptopInfo checks, JobState job) =>
        new(checks.Granola is { Length: > 0 }, HostInfo.TailscaleProblem(checks.Tailscale), job.Running);

    /// <summary>What this laptop needs besides Study Stash: Granola, which records the lectures, and Tailscale, which
    /// reaches the library from anywhere. Each with its fix.</summary>
    public static string ThisComputer(LaptopInfo checks, JobState job)
    {
        var rows = new List<(string Name, string What, string State, bool Ok)>();
        var fixes = new List<string>();
        if (checks.GranolaHere)
        {
            bool ok = checks.Granola is { Length: > 0 };
            rows.Add(("Granola", "Records your lectures", ok ? "installed" : "not installed", ok));
            if (!ok)
                fixes.Add("<p>Granola isn’t on this computer yet. It’s the app you record your lectures in.</p>"
                    + $"<div class=\"toolbar\"><a class=\"btn primary\" href=\"{Ready.GranolaDownload}\" target=\"_blank\" "
                    + "rel=\"noopener\">Get Granola</a></div>");
        }
        var ts = checks.Tailscale;
        string problem = HostInfo.TailscaleProblem(ts);
        rows.Add(("Tailscale", "Reaches your library from anywhere", problem.Length > 0 ? problem : "connected", problem.Length == 0));
        if (problem.Length > 0)
        {
            var (text, label) = ts.Installed
                ? ("Open Tailscale and sign in with the same account as your library’s computer.", "Open Tailscale")
                : ("Without Tailscale, this computer reaches your library only on the same Wi-Fi.", "Install Tailscale");
            string busy = job.Running ? " disabled" : "";
            fixes.Add($"<p>{text}</p><div class=\"toolbar\"><button class=\"primary\" data-action=\"/api/tailscale\" "
                + $"data-out=\"ts-say\" data-busy=\"Working…\"{busy}>{label}</button></div>"
                + $"<p class=\"say{(job.Error is { Length: > 0 } ? " bad" : "")}\" id=\"ts-say\">"
                + $"{(job.Error is { Length: > 0 } error ? Esc("Tailscale didn’t install: " + error) : "")}</p>");
        }
        // Without Granola there's nothing to record with (red); without Tailscale, only home Wi-Fi works (orange).
        string items = string.Concat(rows.Select(r =>
            $"<div class=\"row\"><span class=\"grow\">{Esc(r.Name)}<span class=\"subtitle\">{Esc(r.What)}</span></span>"
            + $"<span class=\"value{(!r.Ok && r.Name == "Granola" ? " bad" : "")}\">{Esc(r.State)}</span>"
            + $"<span class=\"dot\" style=\"--tag:{(r.Ok ? "var(--green)" : r.Name == "Granola" ? "var(--red)" : "var(--orange)")}\">"
            + "</span></div>"));
        string notice = fixes.Count > 0 ? $"<div class=\"notice\" style=\"margin-top:.75rem\"><div>{string.Concat(fixes)}</div></div>" : "";
        return $"<div class=\"group-head\">This computer</div><div class=\"group\">{items}</div>{notice}";
    }

    static string Step(int n, string title, bool done, bool locked, string inner)
    {
        string cls = "step" + (done ? " done" : "") + (locked ? " locked" : "");
        string mark = done ? "✓" : n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"<section class=\"{cls}\"><div class=\"step-head\"><span class=\"step-n\">{mark}</span>"
            + $"<h2>{Esc(title)}</h2></div><div class=\"group\"><div class=\"fields\">{inner}</div></div></section>";
    }

    const string CopySwitch = "<label class=\"row\"><span class=\"grow\">Copy each transcript from the Granola app<span class=\"subtitle\">Presses Granola’s Copy transcript for you when a lecture ends. This automates the Granola app, which may go against Granola’s terms of service, so it’s off unless you turn it on.</span></span>";

    static string ModeRows(bool ask) =>
        $"<label class=\"row pick\"><input type=\"radio\" name=\"mode\" value=\"auto\"{(ask ? "" : " checked")}>"
        + "<span class=\"grow\">Send every lecture automatically</span><span class=\"tick\"></span></label>"
        + $"<label class=\"row pick\"><input type=\"radio\" name=\"mode\" value=\"ask\"{(ask ? " checked" : "")}>"
        + "<span class=\"grow\">Ask me before sending each one</span><span class=\"tick\"></span></label>";

    static string CopyRow(bool mac, ClientConfig cc) =>
        mac ? CopySwitch + $"<input class=\"switch\" type=\"checkbox\" name=\"copy_transcripts\"{(cc.CopyTranscripts ? " checked" : "")}></label>" : "";

    // --- the two pages -------------------------------------------------------------------------------------------

    public static (string Title, string Body, int Watch) SetupPage(LaptopRuntime rt)
    {
        var cc = rt.Config();
        var prefill = ReadPrefill(rt.Home);
        string server = Py.Truthy(prefill["server"]) ? Py.Str(prefill["server"]) : cc.ServerUrl;
        string key = Py.Truthy(prefill["key"]) ? Py.Str(prefill["key"]) : cc.PoolKey;
        bool connected = cc.ServerUrl.Length > 0 && cc.PoolName.Length > 0;
        bool signed = rt.SignedIn();
        var login = rt.Login;
        bool mac = rt.Host.System == "Darwin";

        string poolInner = "<p>The address and password from your library's setup (also under Settings in its web page).</p>"
            + "<form class=\"stack\" data-action=\"/api/pool\" data-out=\"pool-say\" data-busy=\"Connecting…\">"
            + $"<input type=\"url\" name=\"server\" value=\"{Esc(server)}\" placeholder=\"http://mac-mini:8787\" aria-label=\"Library address\" required>"
            + $"<input type=\"password\" name=\"key\" value=\"{Esc(key)}\" placeholder=\"Password\" aria-label=\"Password\">"
            + $"<div class=\"actions\" style=\"margin:0\"><button class=\"{(connected ? "" : "primary")}\">"
            + $"{(connected ? "Reconnect" : "Connect")}</button></div></form>"
            + (connected ? $"<p class=\"say good\" id=\"pool-say\">Connected to {Esc(cc.PoolName)}.</p>" : "<p class=\"say\" id=\"pool-say\"></p>");
        string granolaInner;
        if (signed)
            granolaInner = "<p class=\"say good\" style=\"margin:0\">Signed in to Granola.</p>";
        else if (login.Running)
            granolaInner = "<p>A browser tab opened for Granola. Sign in there, then come back to this tab.</p>"
                + "<p class=\"say\"><span class=\"spin\" style=\"display:inline-block;vertical-align:-2px;"
                + "margin-right:.4rem\"></span>Waiting for you to finish signing in…</p>";
        else
        {
            string err = login.Error is { Length: > 0 } e ? $"<p class=\"say bad\">{Esc(e)}. Try again.</p>" : "";
            granolaInner = "<p>Sign in with your Granola account, the one you record lectures with.</p>"
                + $"{err}<div class=\"actions\" style=\"margin:0\"><button class=\"primary\" data-action=\"/api/login\" "
                + "data-out=\"login-say\" data-busy=\"Opening Granola…\">Sign in to Granola</button></div>"
                + "<p class=\"say\" id=\"login-say\"></p>";
        }
        string prefsInner = "<form data-action=\"/api/prefs\" data-out=\"prefs-say\" data-autosave><div class=\"group\" style=\"margin:-.8rem -1rem\">"
            + ModeRows(cc.Mode == "ask") + CopyRow(mac, cc)
            + "</div></form><p class=\"say\" id=\"prefs-say\" style=\"margin-top:1.3rem\"></p>";
        var steps = new List<string>
        {
            Step(1, "Connect to your library", connected, false, poolInner),
            Step(2, "Sign in to Granola", signed, !connected, granolaInner),
            Step(3, "How to send", connected && signed, !signed, prefsInner),
        };
        // Python's step 4 here, allowing transcript copying, belongs to the copier: the Study Stash app asks for that.
        int n = 4;
        bool ready = connected && signed;
        string finishInner = "<p>Study Stash keeps running in the background and starts when you log in. "
            + "When a lecture finishes in Granola, it's sent to your library.</p>"
            + "<div class=\"actions\" style=\"margin:0\"><button class=\"primary\" data-action=\"/api/finish\" "
            + $"data-out=\"finish-say\"{(ready ? "" : " disabled")}>Finish setup</button></div>"
            + "<p class=\"say\" id=\"finish-say\"></p>";
        steps.Add(Step(n, "Start sending", false, !ready, finishInner));
        string body = "<header><h1>Set up Study Stash</h1><p class=\"sub\">Send your Granola lectures to your library, on your "
            + $"own computer, where your own model writes their notes. {n} short steps.</p></header>"
            + $"{ThisComputer(rt.Readiness(), rt.TailscaleJob)}{string.Concat(steps)}";
        return ("Set up Study Stash", body, 2);
    }

    public static (string Title, string Body, int Watch) StatusPage(LaptopRuntime rt)
    {
        var cc = rt.Config();
        var copy = rt.CopyStatus();
        bool mac = rt.Host.System == "Darwin";
        bool signedIn = rt.SignedIn();
        var facts = new List<(string Key, string Value, bool Bad)>
        {
            ("Library", cc.PoolName.Length > 0 ? cc.PoolName : "not connected", false),
            ("Granola", signedIn ? "signed in" : "signed out", !signedIn),
            ("Watching", rt.Watching ? "every few minutes" : "stopped", !rt.Watching),
        };
        // Only where transcripts can be copied: Python asked for permission here even where nothing could copy.
        bool copying = mac && cc.CopyTranscripts && copy.Available;
        if (copying) facts.Add(("Transcripts", copy.Allowed ? "copied from Granola" : "needs permission", !copy.Allowed));
        var checks = rt.Readiness();
        if (checks.GranolaHere && checks.Granola is not { Length: > 0 }) facts.Add(("Granola app", "not installed on this computer", true));
        string? problem = rt.Problem;
        bool signinProblem = problem is not null && problem.Contains("granola-share login", StringComparison.Ordinal); // every sign-in failure says this
        string? sendKind = problem is not null ? rt.SendProblemKind : null;
        if (signinProblem) facts[1] = ("Granola", "needs you to sign in again", true);
        else if (sendKind == "password") facts[0] = ("Library", "needs its new password", true);
        else if (sendKind == "unreachable") facts[0] = ("Library", "can't be reached", true);
        string factsHtml = string.Concat(facts.Select(f =>
            $"<div class=\"row\"><span class=\"grow\">{Esc(f.Key)}</span><span class=\"value{(f.Bad ? " bad" : "")}\">{Esc(f.Value)}</span>"
            + $"<span class=\"dot\" style=\"--tag:{(f.Bad ? "var(--red)" : "var(--green)")}\"></span></div>"));
        string problemHtml;
        if (problem is not null && problem.Length > 0 && sendKind == "password")
        {
            problemHtml = $"<div class=\"notice bad\"><div>{Esc(problem)}"
                + "<form class=\"stack\" data-action=\"/api/pool\" data-out=\"problem-say\" data-busy=\"Connecting…\">"
                + $"<input type=\"hidden\" name=\"server\" value=\"{Esc(cc.ServerUrl)}\">"
                + "<input type=\"password\" name=\"key\" placeholder=\"The new password\" aria-label=\"Library password\" required>"
                + "<div class=\"actions\" style=\"margin:0\"><button class=\"primary\">Reconnect</button></div></form>"
                + "<p class=\"say\" id=\"problem-say\"></p>"
                + "<p class=\"small muted\" style=\"margin:.5rem 0 0\">It's shown on your library's computer, in the library's "
                + "Settings under Connect your laptop.</p></div></div>";
        }
        else if (problem is not null && problem.Length > 0)
        {
            string fix = signinProblem ? "<div class=\"toolbar\"><button class=\"primary\" data-action=\"/api/login\" data-out=\"problem-say\">"
                + "Sign in to Granola again</button></div>" : "";
            string said = signinProblem ? "Granola signed you out, so new lectures can't be checked."
                : sendKind is not null ? problem : $"The last check didn't work: {Py.Head(problem, 240)}";
            problemHtml = $"<div class=\"notice bad\"><div>{Esc(said)}{fix}<p class=\"say\" id=\"problem-say\"></p></div></div>";
        }
        else
        {
            problemHtml = "";
        }
        // Python's "Allow transcript copying" card belongs to the copier, and shows only where one runs.
        var rows = Recent(cc);
        string items = string.Concat(rows.Select(e =>
            $"<div class=\"row\"><div class=\"grow\"><div class=\"title\">{Esc(Py.AsString(e["title"]) ?? "")}</div>"
            + $"<div class=\"subtitle\"><span>{Esc(Ui.ShortDate(Py.AsString(e["date"])))}</span><span>{Esc(Describe(e))}</span></div></div>"
            + $"<span class=\"dot\" style=\"--tag:{StateColor(e)}\"></span></div>"));
        string recent = rows.Count > 0 ? $"<div class=\"group\">{items}</div>"
            : "<div class=\"empty\"><strong>Nothing sent yet</strong>When a lecture finishes in Granola, "
              + "it's sent to your library and shows up here.</div>";
        string settings = "<div class=\"group-head\">Sending</div>"
            + "<form data-action=\"/api/prefs\" data-out=\"prefs-say\" data-autosave><div class=\"group\">"
            + ModeRows(cc.Mode == "ask") + CopyRow(mac, cc)
            + "</div></form><p class=\"say\" id=\"prefs-say\"></p>"
            + "<div class=\"group-head\">Account</div><div class=\"group\">"
            + "<button class=\"row\" data-action=\"/api/login\" data-out=\"more-say\">Sign in to Granola again</button>"
            + "<button class=\"row\" data-action=\"/api/reset-pool\" data-out=\"more-say\" "
            + "data-confirm=\"Connect to a different library? Your lectures stay where they are.\">Connect to a different library</button>"
            + "<button class=\"row danger\" data-action=\"/api/remove\" data-out=\"more-say\" "
            + "data-confirm=\"Stop Study Stash and remove it from this computer? Nothing more is sent.\">"
            + "Stop and remove Study Stash</button></div><p class=\"say\" id=\"more-say\"></p>"
            + $"<p class=\"group-foot\">Version {Engine.Version}. Your settings and copied transcripts are in {Esc(rt.Home)}.</p>"
            + "<p class=\"group-foot\">Study Stash is an independent project, not affiliated with or endorsed by Granola. Granola is a trademark of its owner.</p>";
        string body = $"<header><h1>Study Stash</h1><p class=\"sub\">Sending your lectures to {Esc(cc.PoolName)}.</p></header>"
            + $"{problemHtml}<div class=\"group\">{factsHtml}</div>"
            + "<div class=\"toolbar\" style=\"margin-top:1rem\"><button class=\"primary\" data-action=\"/api/check\" "
            + "data-out=\"check-say\">Check for new lectures now</button>"
            // The Mac app signs in to the library itself; in a browser, /library does it.
            + $"<a class=\"btn\" href=\"{Esc(mac ? cc.ServerUrl : "/library")}\" target=\"_blank\" rel=\"noopener\">"
            + "Open your library</a></div>"
            + "<p class=\"say\" id=\"check-say\"></p>"
            + $"<h2>Recent lectures</h2>{recent}{settings}";
        return ("Study Stash", body, 15);
    }

    public static string Respond(string title, string body, int watch, string nonce) =>
        Ui.Head(title, nonce).Replace("</style>", PageText.AppCss + "</style>")
        + $"<body data-watch=\"{(watch != 0 ? watch.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")}\"><div class=\"solo\">{body}</div>"
        + $"<script nonce=\"{nonce}\">{PageText.AppJs}</script></body></html>";

    public static string Unauthed(string system) =>
        "<header><h1>Study Stash</h1></header><p class=sub>Open <strong>Study Stash</strong> from your " + WhereTheAppIs(system) + " to see this page.</p>";

    /// <summary>"Open your library", already signed in: posts the password this laptop has to the library's login form,
    /// as if you'd typed it. The strict cookie keeps other sites from opening this.</summary>
    public static (string Page, string Csp) LibraryPage(ClientConfig cc, string nonce)
    {
        var target = new Uri(cc.ServerUrl);
        string origin = $"{target.Scheme}://{target.Authority}";
        string body = $"<form id=\"go\" method=\"post\" action=\"{Esc(origin)}/login\"><input type=\"hidden\" name=\"password\" "
            + $"value=\"{Esc(cc.PoolKey)}\"><input type=\"hidden\" name=\"next\" value=\"/\">"
            + $"<p class=\"sub\">Opening {Esc(cc.PoolName.Length > 0 ? cc.PoolName : "your library")}…</p>"
            + "<noscript><button class=\"primary\">Open your library</button></noscript></form>"
            + $"<script nonce=\"{nonce}\">document.getElementById(\"go\").submit()</script>";
        string page = Ui.Head("Opening your library", nonce) + $"<body><div class=\"solo\">{body}</div></body></html>";
        string csp = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; img-src 'self'; "
            + $"form-action {origin}; frame-ancestors 'none'; base-uri 'none'";
        return (page, csp);
    }

    // --- the routes -----------------------------------------------------------------------------------------------

    static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static WebApplication Build(WebApplicationBuilder builder, LaptopRuntime rt, int port = DefaultPort,
        IEnumerable<string>? extraHosts = null, Func<string>? nonce = null)
    {
        var app = builder.Build();
        string home = rt.Home;
        string token = AppPage.Token(home);
        var allowed = new HashSet<string>([$"127.0.0.1:{port}", $"localhost:{port}", .. extraHosts ?? []]);
        nonce ??= () => Http.TokenUrlSafe(16);

        app.Use(async (ctx, next) =>
        {
            // DNS rebinding: a web page can point a name at 127.0.0.1, but it can't fake the Host header.
            if (!allowed.Contains(ctx.Request.Host.Value ?? ""))
            {
                await Http.Detail(403, "not here").ExecuteAsync(ctx);
                return;
            }
            await next();
        });

        bool Authed(HttpContext ctx) => Same(ctx.Request.Cookies[AppPage.Cookie] ?? "", token);

        // The custom header can't be sent cross-site without a CORS preflight we never allow.
        IResult? Refused(HttpContext ctx) => Authed(ctx) && ctx.Request.Headers["x-granola-share"] == "1" ? null
            : Http.Detail(403, "Open Study Stash from its app icon.");

        IResult Page((string Title, string Body, int Watch) p)
        {
            string n = nonce();
            return Http.Html(Respond(p.Title, p.Body, p.Watch, n), AppPage.Csp(n));
        }

        static JsonObject Said(string message, bool? reload = null, int? delay = null)
        {
            var o = new JsonObject { ["message"] = message };
            if (reload is bool r) o["reload"] = r;
            if (delay is int d) o["delay"] = d;
            return o;
        }

        app.MapGet("/", (HttpContext ctx, string? t) =>
        {
            if (!string.IsNullOrEmpty(t) && Same(t, token))
            {
                ctx.Response.Cookies.Append(AppPage.Cookie, token, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(365), Path = "/",
                });
                return Http.SeeOther("/");
            }
            if (!Authed(ctx)) return Page(("Study Stash", Unauthed(rt.Host.System), 0));
            return Page(rt.Configured() && rt.Watching ? StatusPage(rt) : SetupPage(rt));
        });

        app.MapPost("/api/pool", Http.Handle(async ctx =>
        {
            if (Refused(ctx) is IResult no) return no;
            var data = await Http.JsonBodyAsync(ctx.Request) ?? [];
            string url = NormalizeUrl(data["server"] is JsonNode s ? Py.Str(s) : ""), key = Py.Strip(data["key"] is JsonNode k ? Py.Str(k) : "");
            JsonObject info;
            try
            {
                info = await rt.Host.CheckServer(url, key);
            }
            catch (Exception e)
            {
                string hint = e.Message.Contains("password", StringComparison.Ordinal) ? " Check the password from your library's setup."
                    : " Is Tailscale on on both computers, and is the library's computer awake?";
                return Http.Detail(400, $"Couldn't connect: {e.Message}.{hint}");
            }
            var cc = rt.Config();
            (cc.ServerUrl, cc.PoolKey, cc.PoolName) = (url, key, Py.Truthy(info["pool_name"]) ? Py.Str(info["pool_name"]) : "your library");
            if (cc.DisplayName.Length == 0) cc.DisplayName = rt.Host.UserName();
            Configs.SaveClient(cc);
            ClearPrefill(home);
            rt.Reload();
            return Http.Json(Said($"Connected to {cc.PoolName}. Anything waiting is being sent now."));
        }));

        app.MapPost("/api/login", Http.Handle(ctx =>
        {
            if (Refused(ctx) is IResult no) return Task.FromResult(no);
            rt.StartLogin();
            return Task.FromResult(Http.Json(Said("A browser tab opened for Granola. Sign in there.", true, 300)));
        }));

        app.MapPost("/api/prefs", Http.Handle(async ctx =>
        {
            if (Refused(ctx) is IResult no) return no;
            var data = await Http.JsonBodyAsync(ctx.Request) ?? [];
            var cc = rt.Config();
            string name = Py.Truthy(data["display_name"]) ? Py.Str(data["display_name"]) : cc.DisplayName.Length > 0 ? cc.DisplayName : rt.Host.UserName();
            cc.DisplayName = Py.Head(Py.Strip(name), 80);
            cc.Mode = Py.AsString(data["mode"]) == "auto" ? "auto" : "ask";
            if (data.ContainsKey("copy_transcripts")) cc.CopyTranscripts = Py.Truthy(data["copy_transcripts"]);
            Configs.SaveClient(cc);
            rt.Reload();
            return Http.Json(Said("Saved."));
        }));

        app.MapPost("/api/allow", Http.Handle(ctx =>
        {
            if (Refused(ctx) is IResult no) return Task.FromResult(no);
            rt.RequestPermission();
            return Task.FromResult(Http.Json(Said("System Settings is open. Turn on python3.12, then come back.", false)));
        }));

        app.MapPost("/api/finish", Http.Handle(ctx =>
        {
            if (Refused(ctx) is IResult no) return Task.FromResult(no);
            if (!rt.Configured()) return Task.FromResult(Http.Detail(400, "Finish the steps above first."));
            var cc = rt.Config();
            if (cc.DisplayName.Length == 0)
            {
                cc.DisplayName = rt.Host.UserName();
                Configs.SaveClient(cc);
            }
            rt.StartWatching();
            rt.CheckNow();
            return Task.FromResult(Http.Json(Said("All set. Checking Granola now…")));
        }));

        app.MapPost("/api/tailscale", Http.Handle(ctx =>
        {
            if (Refused(ctx) is IResult no) return Task.FromResult(no);
            return Task.FromResult(Http.Json(Said(rt.FixTailscale(), false)));
        }));

        app.MapPost("/api/check", Http.Handle(ctx =>
        {
            if (Refused(ctx) is IResult no) return Task.FromResult(no);
            rt.CheckNow();
            return Task.FromResult(Http.Json(Said("Checking Granola now.", true, 8000)));
        }));

        app.MapPost("/api/reset-pool", Http.Handle(ctx =>
        {
            if (Refused(ctx) is IResult no) return Task.FromResult(no);
            var cc = rt.Config();
            (cc.ServerUrl, cc.PoolKey, cc.PoolName) = ("", "", "");
            Configs.SaveClient(cc);
            rt.RestartSoon(); // the service starts again, into setup
            return Task.FromResult(Http.Json(Said("Restarting into setup…", delay: 4000)));
        }));

        app.MapPost("/api/remove", Http.Handle(ctx =>
        {
            if (Refused(ctx) is IResult no) return Task.FromResult(no);
            _ = Task.Delay(1000).ContinueWith(_ => rt.Host.Remove(), TaskScheduler.Default); // this stops this very process
            return Task.FromResult(Http.Json(Said("Stopped and removed. You can close this tab.", false)));
        }));

        app.MapGet("/api/state", Http.Handle(ctx =>
        {
            if (!Authed(ctx)) return Task.FromResult(Http.Detail(403, "not signed in"));
            var cc = rt.Config();
            var copy = rt.CopyStatus();
            var copyJson = rt.Host.System == "Darwin"
                ? new JsonObject { ["available"] = copy.Available, ["enabled"] = copy.Enabled, ["why"] = copy.Why, ["allowed"] = copy.Allowed }
                : new JsonObject { ["available"] = copy.Available, ["enabled"] = copy.Enabled, ["allowed"] = copy.Allowed, ["why"] = copy.Why };
            var recent = new JsonArray(Recent(cc).Take(5).Select(e => (JsonNode?)new JsonArray(e["title"]?.DeepClone(), e["decision"]?.DeepClone(), e["filed"]?.DeepClone())).ToArray());
            return Task.FromResult(Http.Json(new JsonObject
            {
                ["configured"] = rt.Configured(), ["signed_in"] = rt.SignedIn(), ["login"] = rt.LoginJson(), ["problem"] = rt.Problem,
                ["copy"] = copyJson, ["watching"] = rt.Watching, ["pool_name"] = cc.PoolName, ["recent_key"] = recent,
                ["ready"] = ReadyKey(rt.Readiness(), rt.TailscaleJob), ["version"] = Engine.Version,
            }));
        }));

        app.MapGet("/library", (HttpContext ctx) =>
        {
            var cc = rt.Config();
            if (!Authed(ctx) || cc.ServerUrl.Length == 0) return Http.SeeOther("/");
            var (page, csp) = LibraryPage(cc, nonce());
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            return Http.Html(page, csp);
        });

        app.MapGet("/healthz", () => Http.Json(new JsonObject { ["ok"] = true, ["app"] = "granola-share", ["version"] = Engine.Version }));
        Icons.Map(app);
        return app;
    }

    // --- running it ---------------------------------------------------------------------------------------------

    /// <summary>The page, on 127.0.0.1 (its port in &lt;home&gt;/ui_port), until `stop`.</summary>
    public static async Task ServeAsync(LaptopRuntime rt, int? port = null, CancellationToken stop = default)
    {
        int p = port ?? AppPage.FreePort(DefaultPort);
        Directory.CreateDirectory(rt.Home);
        Py.WriteText(Path.Combine(rt.Home, "ui_port"), p.ToString(System.Globalization.CultureInfo.InvariantCulture));
        rt.Log($"[app] Study Stash page on http://127.0.0.1:{p}");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, p));
        var app = Build(builder, rt, p);
        await app.StartAsync(stop);
        try
        {
            await Task.Delay(Timeout.Infinite, stop);
        }
        catch (OperationCanceledException)
        {
        }
        await app.StopAsync(CancellationToken.None);
    }

    public static string? AppUrl(string home)
    {
        try
        {
            int port = int.Parse(Py.Strip(Py.ReadText(Path.Combine(home, "ui_port"))), System.Globalization.CultureInfo.InvariantCulture);
            return $"http://127.0.0.1:{port}/?t={AppPage.Token(home)}";
        }
        catch (Exception e) when (e is IOException or FormatException or OverflowException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>The page's address once the background service answers, else null.</summary>
    public static async Task<string?> WaitForAppAsync(string home, TimeSpan? timeout = null, HttpClient? http = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            if (AppUrl(home) is string url)
            {
                try
                {
                    using var r = await (http ?? Probe).GetAsync(url.Split("/?")[0] + "/healthz");
                    if (r.StatusCode == HttpStatusCode.OK && Py.AsString((Py.JsonLoads(await r.Content.ReadAsStringAsync()) as JsonObject)?["app"]) == "granola-share")
                        return url;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                }
            }
            await Task.Delay(500);
        }
        return null;
    }
}
