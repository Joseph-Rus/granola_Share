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

/// <summary>The setup page's HTML and its routes (library_setup.py's render and create_setup_app).</summary>
public static class SetupWeb
{
    static string Step(int n, string title, bool done, bool locked, string inner)
    {
        string cls = "step" + (done ? " done" : "") + (locked ? " locked" : "");
        return $"<section class=\"{cls}\"><div class=\"step-head\"><span class=\"step-n\">{(done ? "✓" : n.ToString(System.Globalization.CultureInfo.InvariantCulture))}</span>"
            + $"<h2>{Ui.Esc(title)}</h2></div><div class=\"group\"><div class=\"fields\">{inner}</div></div></section>";
    }

    static string Job(string name, Dictionary<string, JobView> jobs)
    {
        var j = jobs.GetValueOrDefault(name);
        string kind = j?.Error == true ? "bad" : j?.Running == true ? "wait" : "good";
        return $"<progress data-job=\"{name}\"></progress>"
            + $"<p class=\"say {(!string.IsNullOrEmpty(j?.Note) ? kind : "")}\" data-job-note=\"{name}\">{Ui.Esc(j?.Note ?? "")}</p>";
    }

    static string Row(string name, string what, string state, bool ok, string? color = null)
    {
        string dot = ok ? "var(--green)" : color ?? "var(--orange)";
        return $"<div class=\"row\"><span class=\"grow\">{Ui.Esc(name)}<span class=\"subtitle\">{Ui.Esc(what)}</span></span>"
            + $"<span class=\"value\">{Ui.Esc(state)}</span><span class=\"dot\" style=\"--tag:{dot}\"></span></div>";
    }

    public static async Task<string> RenderAsync(LibrarySetup s)
    {
        var c = await s.ChecksAsync();
        var cfg = s.Cfg;
        var jobs = s.Jobs.View();
        string here = s.Host.System;
        var ts = c.Tailscale;
        string tsProblem = HostInfo.TailscaleProblem(ts);
        bool ollamaUp = c.Models is not null;
        string rec = Ollama.RecommendedModel(c.Ram);
        string Disabled(string job) => s.Jobs.Running(job) ? " disabled" : "";

        // 1. This computer
        var rows = new List<string>();
        if (c.Ram is double ram && ram != 0) rows.Add(Row("Memory", "How big a model this computer can run", $"{Py.FormatFixed(ram, 0)} GB", true));
        if (c.Disk is double disk) rows.Add(Row("Disk", "Models take 2 to 25 GB each", $"{Py.FormatFixed(disk, 0)} GB free", disk >= 30));
        rows.Add(Row("Tailscale", "Lets your laptop and phone reach this computer from anywhere", tsProblem.Length > 0 ? tsProblem : "connected", tsProblem.Length == 0));
        rows.Add(Row("Ollama", "Runs the model that writes your study notes, right here",
            ollamaUp ? "running" : c.OllamaInstalled ? "installed, not running" : "not installed", ollamaUp));
        var fixes = new List<string>();
        if (tsProblem.Length > 0)
        {
            string label = !ts.Installed ? "Install Tailscale" : "Open Tailscale";
            fixes.Add("<p>Tailscale is free for personal use. Sign in with the same account on your laptop.</p>"
                + "<div class=\"toolbar\"><button class=\"primary\" data-action=\"/api/tailscale\" data-out=\"ts-say\" "
                + $"data-busy=\"Working…\"{Disabled("tailscale")}>{label}</button></div>"
                + $"<p class=\"say\" id=\"ts-say\"></p>{Job("tailscale", jobs)}");
        }
        if (!ollamaUp)
        {
            string label = c.OllamaInstalled ? "Start Ollama" : "Install Ollama";
            fixes.Add("<p>Ollama is free, from ollama.com. Without it, lectures are still sorted by their Granola folder "
                + "and title, and keep Granola’s own notes.</p>"
                + "<div class=\"toolbar\"><button class=\"primary\" data-action=\"/api/ollama\" data-out=\"ol-say\" "
                + $"data-busy=\"Working…\"{Disabled("ollama")}>{label}</button></div>"
                + $"<p class=\"say\" id=\"ol-say\"></p>{Job("ollama", jobs)}");
        }
        string computer = $"<div class=\"group\" style=\"margin:-.8rem -1rem\">{string.Concat(rows)}</div>"
            + (fixes.Count > 0 ? $"<div style=\"margin-top:1rem\">{string.Concat(fixes)}</div>" : "");

        // 2. The library
        string library = "<p>Your laptop and browser use this name and password to reach it.</p>"
            + "<form class=\"stack\" data-action=\"/api/library\" data-out=\"lib-say\" data-busy=\"Saving…\">"
            + $"<label class=\"field\">Name<input type=\"text\" name=\"name\" value=\"{Ui.Esc(cfg.PoolName)}\" required></label>"
            + $"<label class=\"field\">Password<input type=\"text\" name=\"password\" value=\"{Ui.Esc(cfg.PoolPassword)}\" autocomplete=\"off\" "
            + "minlength=\"4\" required></label>"
            + $"<label class=\"field\">Folder for the notes<input type=\"text\" name=\"folder\" value=\"{Ui.Esc(cfg.PoolDir)}\" required></label>"
            + $"<label class=\"field\">Port<input type=\"text\" name=\"port\" value=\"{cfg.WebPort}\" inputmode=\"numeric\" required></label>"
            + $"<div class=\"actions\" style=\"margin:0\"><button class=\"{(s.DraftLibrary ? "" : "primary")}\">"
            + $"{(s.DraftLibrary ? "Save changes" : "Save")}</button></div></form><p class=\"say\" id=\"lib-say\"></p>";

        // 3. Classes
        string listed = string.Concat(cfg.Classes.Select((k, i) =>
            $"<div class=\"row\"><span class=\"grow\">{Ui.Esc(k.Name)}<span class=\"subtitle\">{Ui.Esc(string.Join(", ", k.Aliases))}"
            + $"</span></span><form data-action=\"/api/classes\" data-out=\"cls-say\"><input type=\"hidden\" name=\"remove\" value=\"{i}\">"
            + "<button>Remove</button></form></div>"));
        string classes = "<p>The folders lectures are sorted into. Other names are what you call it in Granola folder names or "
            + "titles, like cs101.</p>"
            + (listed.Length > 0 ? $"<div class=\"group\" style=\"margin:0 -1rem .8rem\">{listed}</div>"
                : "<p class=\"muted\">None yet: lectures land in Unsorted until you add some. You can add them later too.</p>")
            + "<form class=\"stack\" data-action=\"/api/classes\" data-out=\"cls-say\" data-busy=\"Adding…\">"
            + "<div class=\"two\"><input type=\"text\" name=\"name\" placeholder=\"Class, like CS 101\" aria-label=\"Class name\">"
            + "<input type=\"text\" name=\"aliases\" placeholder=\"Other names, comma separated\" aria-label=\"Other names\"></div>"
            + "<input type=\"text\" name=\"description\" placeholder=\"One line on what it covers (helps the model)\" aria-label=\"Description\">"
            + "<div class=\"actions\" style=\"margin:0\"><button>Add class</button></div></form><p class=\"say\" id=\"cls-say\"></p>";

        // 4. The model
        string notes;
        if (!ollamaUp)
        {
            notes = "<p>Ollama isn’t running, so there’s no model to pick yet. Install or start it above, or go on "
                + "without: lectures are sorted by folder and title, and keep Granola’s notes.</p>"
                + "<div class=\"toolbar\"><button data-action=\"/api/no-ai\" data-out=\"ai-say\">Go on without a model</button></div>"
                + "<p class=\"say\" id=\"ai-say\"></p>";
        }
        else
        {
            var names = await s.NamesAsync();
            var choices = names.Append(rec).Distinct().ToList();
            string chosen = s.DraftModels ? cfg.EffectiveSummaryModel : Ollama.PickDefaultModel(names, rec);
            string Select(string field, string pick, bool same = false) =>
                $"<select name=\"{field}\">" + (same ? "<option value=\"\">The same model (fastest)</option>" : "")
                + string.Concat(choices.Select(n => $"<option value=\"{Ui.Esc(n)}\"{(n == pick ? " selected" : "")}>{Ui.Esc(n)}"
                    + $"{(names.Contains(n) ? "" : " (downloads it)")}</option>")) + "</select>";
            string sortPick = s.DraftModels && cfg.OllamaModel != chosen ? cfg.OllamaModel : "";
            notes = $"<p>For this computer’s memory, {Ui.Esc(rec)} is a good fit. Notes are written from transcripts, "
                + "which Granola shares on its paid plans; other lectures keep Granola’s own summary.</p>"
                + "<form class=\"stack\" data-action=\"/api/models\" data-out=\"mod-say\" data-busy=\"Saving…\">"
                + $"<label class=\"field\">Writes the study notes{Select("summary", chosen)}</label>"
                + $"<label class=\"field\">Sorts lectures into classes{Select("sort", sortPick, same: true)}</label>"
                + "<div class=\"actions\" style=\"margin:0\"><button class=\"primary\">Use these</button></div></form>"
                + $"<p class=\"say\" id=\"mod-say\"></p>{Job("pull", jobs)}{Job("try", jobs)}";
        }

        // 5. Keep it running
        string running = "<form data-action=\"/api/prefs\" data-out=\"pref-say\" data-autosave><div class=\"group\" style=\"margin:-.8rem -1rem\">"
            + "<label class=\"row\"><span class=\"grow\">Start when this computer starts<span class=\"subtitle\">Keeps your library "
            + "running in the background, and restarts it if it stops</span></span>"
            + $"<input class=\"switch\" type=\"checkbox\" name=\"autostart\"{(s.DraftAutostart ? " checked" : "")}></label>"
            + "<label class=\"row\"><span class=\"grow\">Install new versions automatically</span>"
            + $"<input class=\"switch\" type=\"checkbox\" name=\"auto_update\"{(cfg.AutoUpdate ? " checked" : "")}></label>"
            + "</div></form><p class=\"say\" id=\"pref-say\"></p>";
        if (here == "Windows" && s.Host.Firewall(cfg.WebPort) == false)
            running += "<p style=\"margin-top:1.1rem\">Windows Firewall blocks your laptop from this computer. Let it through, for Tailscale and "
                + "this network only.</p><div class=\"toolbar\"><button class=\"primary\" data-action=\"/api/firewall\" "
                + $"data-out=\"fw-say\">Let my laptop in</button></div><p class=\"say\" id=\"fw-say\"></p>{Job("firewall", jobs)}";
        int? mins = Machine.SleepFix.ContainsKey(here) ? s.Host.SleepMinutes() : null;
        if (mins is int m && m != 0)
        {
            running += $"<p style=\"margin-top:1.1rem\">This computer sleeps after {m} minute{(m != 1 ? "s" : "")}, "
                + "and your library goes offline with it.</p>";
            running += here == "Windows"
                ? "<div class=\"toolbar\"><button data-action=\"/api/awake\" data-out=\"aw-say\">Keep it awake while plugged in"
                  + "</button></div><p class=\"say\" id=\"aw-say\"></p>"
                : $"<p class=\"muted\">{Ui.Esc(Machine.SleepFix[here])}.</p>";
        }

        // 6. Finish
        string finish;
        if (s.Finished)
        {
            var info = await s.ConnectInfoAsync();
            string exe = $"{LibrarySetup.Releases}/Study-Stash-Laptop-Setup.exe", dmg = $"{LibrarySetup.Releases}/Study-Stash-Laptop.dmg";
            finish = "<p>Your library is running. Open it here, or from your laptop and phone:</p>"
                + $"<p class=\"addr\">{Ui.Esc(info.Urls[0])}</p><p>Password: <span class=\"addr\">{Ui.Esc(info.Password)}</span></p>"
                + $"<div class=\"toolbar\"><a class=\"btn primary\" href=\"http://127.0.0.1:{cfg.WebPort}/\">Open your library</a></div>"
                + "<h3 style=\"margin:1.2rem 0 .3rem\">Connect your laptop</h3>"
                + $"<p>Install Study Stash on the laptop you record lectures on (<a href=\"{dmg}\">Mac</a> or "
                + $"<a href=\"{exe}\">Windows</a>), open it, and enter the address and password above.</p>"
                + "<details class=\"help\"><summary>Or with one line in a terminal</summary>"
                + $"<p>Mac:</p><pre class=\"code\">{Ui.Esc(info.Commands.Mac)}</pre>"
                + $"<p>Windows (PowerShell):</p><pre class=\"code\">{Ui.Esc(info.Commands.Windows)}</pre></details>";
        }
        else
        {
            finish = "<p>Saves your library and starts it. You can change any of this later, under Settings on the "
                + "library’s page.</p>"
                + "<div class=\"actions\" style=\"margin:0\"><button class=\"primary\" data-action=\"/api/finish\" "
                + $"data-out=\"fin-say\" data-busy=\"Finishing…\"{(s.DraftLibrary ? "" : " disabled")}>Finish setup</button>"
                + $"</div><p class=\"say\" id=\"fin-say\"></p>{Job("finish", jobs)}";
        }

        bool notesDone = s.DraftModels && (!cfg.OllamaEnabled || await s.ModelReadyAsync(cfg.EffectiveSummaryModel));
        string[] steps =
        [
            Step(1, "This computer", tsProblem.Length == 0 && ollamaUp, false, computer),
            Step(2, "Your library", s.DraftLibrary, false, library),
            Step(3, "Classes", cfg.Classes.Count > 0, !s.DraftLibrary, classes),
            Step(4, "Study notes", notesDone, !s.DraftLibrary, notes),
            Step(5, "Keep it running", s.Finished, !s.DraftLibrary, running),
            Step(6, s.Finished ? "Ready" : "Finish", s.Finished, !s.DraftLibrary, finish),
        ];
        return "<header><h1>Set up your library</h1><p class=\"sub\">This computer keeps your lectures: it writes their study "
            + "notes with a model that runs here, sorts them by class, and serves them to your laptop and phone. Your "
            + "answers are kept as you go.</p></header>" + string.Concat(steps)
            + "<p class=\"group-foot\">Study Stash is an independent project, not affiliated with or endorsed by Granola.</p>";
    }

    // --- the routes -----------------------------------------------------------------------------------------------

    static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>The setup page's routes. Only this computer's Study Stash Library app can use them.</summary>
    public static WebApplication Build(WebApplicationBuilder builder, LibrarySetup s, int port = LibrarySetup.SetupPort,
        IEnumerable<string>? extraHosts = null, Func<string>? nonce = null)
    {
        var app = builder.Build();
        string token = AppPage.Token(s.Home);
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
            s.LastSeen = DateTime.UtcNow;
            await next();
        });

        bool Authed(HttpContext ctx) => Same(ctx.Request.Cookies[AppPage.Cookie] ?? "", token);

        IResult? Refused(HttpContext ctx) => Authed(ctx) && ctx.Request.Headers["x-granola-share"] == "1" ? null
            : Http.Detail(403, "Open the setup from the Study Stash Library app.");

        async Task<IResult> Act(HttpContext ctx, Func<JsonObject, Task<string>> step, bool reload = true)
        {
            if (Refused(ctx) is IResult refused) return refused;
            var data = await Http.JsonBodyAsync(ctx.Request) ?? new JsonObject();
            try
            {
                return Http.Json(new JsonObject { ["message"] = await step(data), ["reload"] = reload, ["delay"] = 400 });
            }
            catch (SetupProblem e)
            {
                return Http.Detail(400, e.Message);
            }
        }

        app.MapGet("/", async (HttpContext ctx, string? t) =>
        {
            if (!string.IsNullOrEmpty(t) && Same(t, token))
            {
                ctx.Response.Cookies.Append(AppPage.Cookie, token, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(365), Path = "/",
                });
                return Http.SeeOther("/");
            }
            if (!Authed(ctx)) return Results.Content("<p>Open the setup from the Study Stash Library app.</p>", "text/html; charset=utf-8", Encoding.UTF8, 403);
            string n = nonce();
            string page = Ui.Head("Set up your library", n).Replace("</style>", PageText.AppCss + PageText.SetupCss + "</style>")
                + $"<body><div class=\"solo\">{await RenderAsync(s)}</div>"
                + $"<script nonce=\"{n}\">{PageText.AppJs}{PageText.SetupJs}</script></body></html>";
            return Http.Html(page, AppPage.Csp(n));
        });

        app.MapGet("/api/state", Http.Handle(async ctx =>
        {
            if (!Authed(ctx)) return Http.Detail(403, "not signed in");
            var jobs = new JsonObject(s.Jobs.View().Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonObject
            {
                ["running"] = kv.Value.Running, ["done"] = kv.Value.Done, ["total"] = kv.Value.Total, ["note"] = kv.Value.Note,
                ["error"] = kv.Value.Error,
            })));
            return Http.Json(new JsonObject { ["key"] = await s.KeyAsync(), ["jobs"] = jobs, ["finished"] = s.Finished, ["version"] = Engine.Version });
        }));

        app.MapPost("/api/tailscale", Http.Handle(ctx => Act(ctx, _ => s.FixTailscaleAsync(), reload: false)));
        app.MapPost("/api/ollama", Http.Handle(ctx => Act(ctx, _ => s.FixOllamaAsync(), reload: false)));
        app.MapPost("/api/library", Http.Handle(ctx => Act(ctx, s.SetLibraryAsync)));
        app.MapPost("/api/classes", Http.Handle(ctx => Act(ctx, data => Task.FromResult(s.ChangeClasses(data)))));
        app.MapPost("/api/models", Http.Handle(ctx => Act(ctx, s.SetModelsAsync, reload: false)));
        app.MapPost("/api/no-ai", Http.Handle(ctx => Act(ctx, _ => Task.FromResult(s.WithoutAi()))));
        app.MapPost("/api/prefs", Http.Handle(ctx => Act(ctx, data => Task.FromResult(s.SetPrefs(data)), reload: false)));
        app.MapPost("/api/firewall", Http.Handle(ctx => Act(ctx, _ => Task.FromResult(s.FixFirewall()), reload: false)));
        app.MapPost("/api/awake", Http.Handle(ctx => Act(ctx, _ => Task.FromResult(s.StayAwake()))));
        app.MapPost("/api/finish", Http.Handle(ctx => Act(ctx, _ => Task.FromResult(s.Finish()), reload: false)));
        app.MapGet("/healthz", () => Http.Json(new JsonObject { ["ok"] = true, ["app"] = "granola-share-setup", ["version"] = Engine.Version }));
        Icons.Map(app);
        return app;
    }

    /// <summary>
    /// `setup --page`: the page on 127.0.0.1 (its port in &lt;home&gt;/setup_port), until setup is finished (and ten
    /// minutes after, for the last page) or nobody has looked at it for an hour.
    /// </summary>
    public static async Task ServeAsync(string home, bool browser = true, Action<string>? log = null, CancellationToken stop = default)
    {
        log ??= Console.WriteLine;
        var s = new LibrarySetup(home, SetupHost.ThisComputer());
        int port = AppPage.FreePort(LibrarySetup.SetupPort);
        string portFile = Path.Combine(s.Home, "setup_port");
        Directory.CreateDirectory(s.Home);
        Py.WriteText(portFile, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string url = $"http://127.0.0.1:{port}/?t={AppPage.Token(s.Home)}";
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        var app = Build(builder, s, port);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _ = Task.Run(async () =>
        {
            DateTime? doneAt = null;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (s.Finished && doneAt is null) doneAt = DateTime.UtcNow;
                if ((doneAt is DateTime d && DateTime.UtcNow - d > TimeSpan.FromMinutes(10)) || DateTime.UtcNow - s.LastSeen > TimeSpan.FromHours(1))
                    await cts.CancelAsync();
            }
        });
        try
        {
            await app.StartAsync(cts.Token);
            log(url);
            if (browser) s.Host.OpenUrl(url);
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            await app.StopAsync(CancellationToken.None);
        }
        finally
        {
            File.Delete(portFile);
        }
    }
}
