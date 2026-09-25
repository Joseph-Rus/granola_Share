using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using StudyStash.Library;

namespace StudyStash.Core.Tests;

/// <summary>tests/test_sync_web.py's web tests and tests/test_ingest.py, plus every page compared with the Python engine's.</summary>
public class LibraryWebTests
{
    static readonly List<(string, double)> Models = [("qwen3:1.7b", 1.4), ("gemma4:e4b", 9.6)];
    static readonly TailscaleInfo Tailnet = new(true, true, "Running", "mini.tail.ts.net", ["100.64.0.9"]);
    static readonly Action<string> Quiet = _ => { };

    static LibraryWebOptions Options(Func<double, Task<Release?>>? latest = null, bool ollama = true, TailscaleInfo? ts = null,
        Func<Release, string, Task>? apply = null) => new()
    {
        ListModels = _ => Task.FromResult(ollama ? Models : null),
        Tailscale = () => ts ?? Tailnet,
        Latest = latest ?? (_ => Task.FromResult<Release?>(null)),
        Apply = apply,
        RamGb = () => 16,
        HostName = () => "library-pc",
        Nonce = () => "NONCE",
    };

    static async Task<TestSite> Site(Config cfg, Store store, Pipeline? pipeline = null, LibraryWebOptions? options = null) =>
        await TestSite.StartAsync(b => LibraryWeb.Build(b, cfg, store, pipeline ?? new Pipeline(cfg, store, log: Quiet), options ?? Options()));

    static Config MakeCfg(TempDir dir, string password = "", string admin = "") => new(dir["home"], dir["pool"])
    {
        PoolPassword = password, AdminPassword = admin, OllamaEnabled = false,
        Classes = [new ClassDef("CS 101", ["cs101"]), new ClassDef("Bio 110", ["biology"])],
    };

    static void SeedCells(Store store) => store.Save(new Meeting("n1")
    {
        Title = "Cells", Date = "2026-09-01", Owner = "Sam", NotesMarkdown = "# Cells\n\nmembranes",
        Transcript = "today we talk about the cell membrane and osmosis",
        Raw = new JsonObject { ["id"] = "n1", ["title"] = "Cells", ["date"] = "2026-09-01", ["notes"] = "# Cells\n\nmembranes" },
    }, new Classification(Configs.Unsorted, 0.3, "ollama", "Cells", ["cells"]));

    static void Sql(Config cfg, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={cfg.DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // --- every page, as the Python engine serves it ------------------------------------------------------------

    /// <summary>golden.py's seed_library, lecture for lecture.</summary>
    static void SeedLibrary(Config cfg, Store store)
    {
        void Save(string id, string title, string date, string cls, string by, double conf, List<string> topics, string notes = "", string transcript = "") =>
            store.Save(new Meeting(id) { Title = title, Date = date, Owner = "Sam", NotesMarkdown = notes, Transcript = transcript },
                new Classification(cls, conf, by, "", topics));
        Save("n1", "Cells", "2026-09-01T09:00:00", "Bio 110", "folder", 0.95, ["cells", "membranes"], "membranes",
            "today we talk about the cell membrane and osmosis");
        Save("n2", "Loops & <b>ranges</b>", "2026-09-03", "CS 101", "ollama", 0.875, ["loops"], "for loops");
        Save("n3", "Lunch", "2026-09-02T12:30:00", "Unsorted", "none", 0.0, []);
        Save("n4", "Old class lecture", "2026-08-30", "History 9", "human", 1.0, [], "x");
        store.Enqueue(new Meeting("q1") { Title = "Waiting lecture", Date = "2026-09-04" });
        store.Enqueue(new Meeting("f1") { Title = "Broken lecture", Date = "2026-09-05" });
        Sql(cfg, $"UPDATE notes SET status='failed', error='Summary with big:35b failed: {new string('x', 200)}' WHERE id='f1'");
    }

    [Fact]
    public async Task Every_page_matches_the_python_engine_byte_for_byte()
    {
        using var dir = new TempDir();
        var cfg = new Config(dir["home"], dir["pool"])
        {
            PoolName = "Fall \"26\" & co", PoolPassword = "pw", OllamaModel = "qwen3:1.7b", SummaryModel = "gemma4:e4b", MinConfidence = 0.7,
            Classes = [new ClassDef("CS 101", ["cs101"], "Intro to programming"), new ClassDef("Bio 110"), new ClassDef("Calc II", ["math"], "Series.")],
        };
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        SeedLibrary(cfg, store);
        var newer = new Release("v9.9.9", [9, 9, 9], "https://x/v9.9.9.tar.gz", "https://x/releases/v9.9.9");
        var pages = Golden.PageCases()["library"]!.AsObject();
        await using var site = await Site(cfg, store);
        await using var noOllama = await Site(cfg, store, options: Options(_ => Task.FromResult<Release?>(newer), ollama: false,
            ts: new TailscaleInfo(), apply: (_, _) => Task.CompletedTask));
        foreach (var s in new[] { site, noOllama })
            await s.PostForm("/login", ("password", "pw"), ("next", "/"));
        foreach (var (key, want) in pages)
        {
            if (key.StartsWith("open:", StringComparison.Ordinal)) continue;
            var (s, path) = key.StartsWith("no-ollama:", StringComparison.Ordinal) ? (noOllama, key[10..]) : (site, key);
            var r = await s.Get(path);
            Assert.True((int)r.StatusCode == want!["status"]!.GetValue<int>(), $"{key}: {(int)r.StatusCode}");
            Assert.Equal(want["csp"]?.S(), r.Headers.TryGetValues("Content-Security-Policy", out var csp) ? csp.Single() : null);
            string html = await r.Content.ReadAsStringAsync();
            if (html != want["html"].S()) Assert.Fail($"{key} differs from Python's:\n{FirstDifference(want["html"].S(), html)}");
        }
        cfg.PoolPassword = ""; // no password: the library is open, with no Log out
        Assert.Equal(pages["open:/"]!["html"].S(), await (await site.Stranger().GetAsync("/")).Content.ReadAsStringAsync());
    }

    static string FirstDifference(string want, string got)
    {
        int i = 0;
        while (i < want.Length && i < got.Length && want[i] == got[i]) i++;
        int from = Math.Max(0, i - 80);
        return $"python: …{want[from..Math.Min(want.Length, i + 80)]}\nc#:     …{got[from..Math.Min(got.Length, i + 80)]}";
    }

    // --- tests/test_sync_web.py ---------------------------------------------------------------------------------

    [Fact]
    public async Task Login_browse_move_download()
    {
        using var dir = new TempDir();
        var cfg = MakeCfg(dir, "pw", "boss");
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        SeedCells(store);
        await using var c = await Site(cfg, store);
        var r = await c.Get("/note/n1");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.StartsWith("/login?next=", r.Headers.Location!.OriginalString);
        var bad = await c.PostForm("/login", ("password", "nope"), ("next", "/"));
        Assert.Equal(HttpStatusCode.SeeOther, bad.StatusCode);
        Assert.Contains("bad=1", bad.Headers.Location!.OriginalString);
        r = await c.PostForm("/login", ("password", "pw"), ("next", "/note/n1"));
        Assert.Equal(("/note/n1", true), (r.Headers.Location!.OriginalString, c.Cookie("pool") is not null));

        string home = await c.Text("/");
        Assert.Contains("Recent lectures", home);
        Assert.Contains("Cells", home);
        Assert.Contains("Unsorted", home);
        Assert.True((await c.Get("/")).Headers.Contains("Content-Security-Policy"));
        Assert.Contains("Cells", await c.Text("/unsorted"));
        string page = await c.Text("/note/n1");
        foreach (string want in new[] { "membranes", "<select", "Transcript", "osmosis", "Rewrite summary" }) Assert.Contains(want, page);
        Assert.DoesNotContain("from Sam", page); // one person: everything, no "from" labels
        r = await c.PostForm("/note/n1/class", ("class_name", "Bio 110"));
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("Bio 110", store.Get("n1")!.ClassName);
        // moving keeps the transcript in the file (0.1 dropped it)
        var dl = await c.Get("/note/n1/download");
        string md = await dl.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, dl.StatusCode);
        Assert.Contains("membranes", md);
        Assert.Contains("osmosis", md);
        Assert.StartsWith("text/markdown", dl.Content.Headers.ContentType!.ToString());
        var z = await c.Get("/class/Bio%20110/zip");
        Assert.Equal("application/zip", z.Content.Headers.ContentType!.MediaType);
        using (var zip = new ZipArchive(await z.Content.ReadAsStreamAsync()))
            Assert.Equal(["Bio 110/2026-09-01 Cells.md"], zip.Entries.Select(e => e.FullName));
        var api = await c.Client.GetFromJsonAsync<JsonArray>("/api/notes?class_name=Bio%20110");
        Assert.Equal(("n1", "[\"cells\"]"), (api![0]!["id"].S(), api[0]!["topics"]!.ToJsonString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostForm("/note/n1/class", ("class_name", "Nope"))).StatusCode);

        string hits = await c.Text("/search?q=osmosis");
        Assert.Contains("1 lecture mention", hits);
        Assert.Contains("<mark>osmosis</mark>", hits);
        Assert.Contains("No matches", await c.Text("/search?q=quantum"));

        Assert.Equal(HttpStatusCode.OK, (await c.Get("/settings")).StatusCode); // the one password opens Settings too
        var stranger = c.Stranger();
        Assert.Equal(HttpStatusCode.SeeOther, (await stranger.PostAsync("/note/n1/delete", null)).StatusCode);
        Assert.NotNull(store.Get("n1")); // not deleted without a login
    }

    [Fact]
    public async Task Settings_models_classes_and_invite()
    {
        using var dir = new TempDir();
        var cfg = MakeCfg(dir, "pw", "boss");
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        SeedCells(store);
        var pipeline = new Pipeline(cfg, store, log: Quiet);
        await using var c = await Site(cfg, store, pipeline);
        await c.PostForm("/login", ("password", "boss"), ("next", "/")); // the 0.2 admin password still opens it
        string s = await c.Text("/settings");
        Assert.Contains("qwen3:1.7b", s);
        Assert.Contains("Connect your laptop", s);
        Assert.Contains("GRANOLA_SHARE_SERVER=http://mini.tail.ts.net:8787", s);
        Assert.Contains("Rewrite summary", await c.Text("/note/n1"));

        var r = await c.PostForm("/settings",
            ("summary_model", "qwen3:1.7b"), ("ollama_model", "qwen3:1.7b"), ("summary_enabled", "1"), ("ollama_enabled", "1"),
            ("min_confidence", "0.7"),
            ("class_name_0", "CS 101"), ("class_aliases_0", "cs101, intro"), ("class_desc_0", "Programming"),
            ("class_name_1", "Bio 110"), ("class_aliases_1", ""), ("class_desc_1", ""), ("class_remove_1", "1"),
            ("class_name_2", "Chem 1A"), ("class_aliases_2", "chem"), ("class_desc_2", ""),
            ("class_name_3", ""), ("class_aliases_3", ""), ("class_desc_3", ""));
        Assert.Equal((HttpStatusCode.SeeOther, "/settings?saved=1"), (r.StatusCode, r.Headers.Location!.OriginalString));
        var back = Configs.Load(cfg.Home);
        Assert.Equal(("qwen3:1.7b", 0.7, false), (back.SummaryModel, back.MinConfidence, back.KeepGranolaNotes));
        Assert.Equal(["CS 101", "Chem 1A"], back.ClassNames());
        Assert.Equal(["cs101", "intro"], back.Classes[0].Aliases);
        Assert.Equal(["CS 101", "Chem 1A"], cfg.ClassNames()); // the running app sees it right away

        r = await c.PostForm("/settings/resummarize-all");
        Assert.Equal("/settings?queued=1", r.Headers.Location!.OriginalString);
        Assert.Equal("queued", store.Get("n1")!.Status);
        Assert.Contains("Cells", await c.Text("/unsorted")); // still readable while it waits
        Assert.Equal(HttpStatusCode.SeeOther, (await c.PostForm("/note/n1/delete")).StatusCode);
        Assert.Null(store.Get("n1"));
    }

    [Fact]
    public async Task Settings_offers_an_update_when_a_newer_release_is_out()
    {
        using var dir = new TempDir();
        var cfg = MakeCfg(dir, admin: "boss");
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var rel = new Release("v9.9.9", [9, 9, 9], "https://x/v9.9.9.tar.gz", "https://x/releases/v9.9.9");
        Release? applied = null;
        await using var c = await Site(cfg, store, options: Options(_ => Task.FromResult<Release?>(rel), apply: (r, _) =>
        {
            applied = r;
            return Task.CompletedTask;
        }));
        string settings = await c.Text("/settings");
        Assert.Contains("v9.9.9 is out", settings);
        Assert.Contains("Update now", settings);
        Assert.Contains("Updating", await (await c.PostForm("/settings/update")).Content.ReadAsStringAsync());
        for (int i = 0; i < 50 && applied is null; i++) await Task.Delay(20);
        Assert.Same(rel, applied);

        // An engine that can't install updates yet links to the release instead of offering a button.
        await using var plain = await Site(cfg, store, options: Options(_ => Task.FromResult<Release?>(rel)));
        string linked = await plain.Text("/settings");
        Assert.Contains("v9.9.9 is out", linked);
        Assert.DoesNotContain("Update now", linked);
    }

    [Fact]
    public async Task Note_html_is_neutralized()
    {
        using var dir = new TempDir();
        var cfg = MakeCfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        store.Save(new Meeting("x") { Title = "<b>Lec</b>", NotesMarkdown = "hi <script>alert(1)</script> [a](javascript:alert(2)) $x_1^2$ [b](JaVaScRiPt:x) <javascript:y>" },
            new Classification("CS 101", 1, "human"));
        await using var c = await Site(cfg, store);
        string page = await c.Text("/note/x");
        Assert.DoesNotContain("<script>alert(1)", page);
        Assert.Contains("&lt;script&gt;", page);
        Assert.DoesNotContain("javascript:alert", page);
        Assert.DoesNotContain("href=\"JaVaScRiPt", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;b&gt;Lec", page);
        Assert.Contains("$x_1^2$", page); // math survives Markdown for KaTeX
    }

    [Fact]
    public async Task No_password_means_open()
    {
        using var dir = new TempDir();
        var cfg = MakeCfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        await using var c = await Site(cfg, store);
        Assert.Equal(HttpStatusCode.OK, (await c.Get("/")).StatusCode);
        Assert.Contains("No lectures yet", await c.Text("/"));
    }

    [Fact]
    public void Login_never_redirects_off_site()
    {
        Assert.Equal("/note/n1?x=1", LibraryWeb.SafeNext("/note/n1?x=1"));
        foreach (string bad in new[] { "https://evil.com", "//evil.com", "/\\evil.com", "/\t/evil.com", "/\n/evil.com", "evil.com", "" })
            Assert.Equal("/", LibraryWeb.SafeNext(bad));
        foreach (var c in (JsonArray)Golden.Library("safe_next")!)
            Assert.Equal(c![1].S(), LibraryWeb.SafeNext(c[0].S()));
    }

    [Fact]
    public async Task A_login_cookie_from_the_python_engine_still_works()
    {
        // Both engines sign it with the key in web_secret, so switching engines doesn't sign anyone out.
        using var dir = new TempDir();
        var cfg = MakeCfg(dir, "pw");
        Directory.CreateDirectory(cfg.Home);
        File.WriteAllText(Path.Combine(cfg.Home, "web_secret"), "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        await using var c = await Site(cfg, store);
        // hmac.new(bytes.fromhex(that), b"admin", hashlib.sha256).hexdigest()
        c.SetCookie("pool", "274fba6ac9d9127073375a2d3c345f69729c751b36420b304f67219b21269d82");
        var r = await c.Get("/settings");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    // --- tests/test_ingest.py: the API the laptop talks to ------------------------------------------------------------

    static HttpRequestMessage Req(HttpMethod method, string path, string? key = null, object? json = null)
    {
        var r = new HttpRequestMessage(method, path);
        if (key is not null) r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (json is not null) r.Content = JsonContent.Create(json);
        return r;
    }

    [Fact]
    public async Task Health_needs_the_password_and_reports_the_library()
    {
        using var dir = new TempDir();
        var cfg = MakeCfg(dir, "pw");
        cfg.PoolName = "Fall pool";
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        await using var c = await Site(cfg, store);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Get("/api/health")).StatusCode);
        var nope = await c.Client.SendAsync(Req(HttpMethod.Get, "/api/health", "nope"));
        Assert.Equal(HttpStatusCode.Unauthorized, nope.StatusCode);
        Assert.Equal("{\"detail\":\"wrong password\"}", await nope.Content.ReadAsStringAsync());
        var r = await c.Client.SendAsync(Req(HttpMethod.Get, "/api/health", "pw"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""{"ok": true, "pool_name": "Fall pool", "classes": ["CS 101", "Bio 110"], "notes": 0, "version": "{{Engine.Version}}"}"""),
            JsonNode.Parse(await r.Content.ReadAsStringAsync())));
        Assert.Equal(Engine.Version, r.Headers.GetValues("X-Granola-Share").Single());
    }

    [Fact]
    public async Task Ingest_queues_then_the_pipeline_files()
    {
        using var dir = new TempDir();
        var cfg = MakeCfg(dir, "pw");
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var pipeline = new Pipeline(cfg, store, log: Quiet);
        await using var c = await Site(cfg, store, pipeline);
        var payload = new
        {
            id = "not_abc", title = "CS101 lecture 2", date = "2026-09-14T10:00:00Z", owner = "Sam", attendees = new[] { "Sam" },
            folder = "", notes_markdown = "# Loops\nfor and while", private_notes = "", transcript = "", raw = new { id = "not_abc" },
        };
        var r = await c.Client.SendAsync(Req(HttpMethod.Post, "/api/ingest", "pw", payload));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"id": "not_abc", "status": "queued", "class_name": "CS 101", "has_transcript": false}"""),
            JsonNode.Parse(await r.Content.ReadAsStringAsync())));
        Assert.Equal("queued", store.Get("not_abc")!.Status);
        Assert.Empty(store.ListNotes());

        Assert.Equal(1, await pipeline.RunPendingAsync());
        var row = store.Get("not_abc")!;
        Assert.Equal(("done", "CS 101", "Sam"), (row.Status, row.ClassName, row.Owner));
        Assert.Single(store.ListNotes());
        var status = await c.Client.SendAsync(Req(HttpMethod.Get, "/api/notes/not_abc/status", "pw"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"id": "not_abc", "status": "done", "class_name": "CS 101", "summary_model": null, "has_transcript": false, "path": "/note/not_abc", "lecture_title": "CS101 lecture 2"}"""),
            JsonNode.Parse(await status.Content.ReadAsStringAsync())));
        Assert.Equal(HttpStatusCode.NotFound, (await c.Client.SendAsync(Req(HttpMethod.Get, "/api/notes/nope/status", "pw"))).StatusCode);

        // sharing again queues the same lecture again instead of making a second one
        await c.Client.SendAsync(Req(HttpMethod.Post, "/api/ingest", "pw", payload));
        await pipeline.RunPendingAsync();
        Assert.Single(store.ListNotes());
        Assert.Single(Directory.GetFiles(Path.Combine(cfg.PoolDir, "CS 101")));

        // one no rule matches goes to Unsorted (AI off)
        var r3 = await c.Client.SendAsync(Req(HttpMethod.Post, "/api/ingest", "pw", new { id = "x2", title = "Lunch" }));
        Assert.Null(JsonNode.Parse(await r3.Content.ReadAsStringAsync())!["class_name"]);
        await pipeline.RunPendingAsync();
        Assert.Equal(Configs.Unsorted, store.Get("x2")!.ClassName);
    }

    [Fact]
    public async Task Ingest_refuses_a_wrong_password_and_a_lecture_without_an_id()
    {
        using var dir = new TempDir();
        var cfg = MakeCfg(dir, "pw");
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        await using var c = await Site(cfg, store);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Client.SendAsync(Req(HttpMethod.Post, "/api/ingest", json: new { id = "a" }))).StatusCode);
        var noId = await c.Client.SendAsync(Req(HttpMethod.Post, "/api/ingest", "pw", new { title = "no id" }));
        Assert.Equal((HttpStatusCode.BadRequest, "{\"detail\":\"payload needs a non-empty 'id'\"}"), (noId.StatusCode, await noId.Content.ReadAsStringAsync()));
        var notObject = new HttpRequestMessage(HttpMethod.Post, "/api/ingest") { Content = new StringContent("[1]", System.Text.Encoding.UTF8, "application/json") };
        notObject.Headers.TryAddWithoutValidation("Authorization", "Bearer pw");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.Client.SendAsync(notObject)).StatusCode);
    }

    [Fact]
    public async Task An_open_library_needs_no_password()
    {
        using var dir = new TempDir();
        var cfg = MakeCfg(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        await using var c = await Site(cfg, store);
        Assert.Equal(HttpStatusCode.OK, (await c.Get("/api/health")).StatusCode);
    }

    [Fact]
    public async Task Icons_and_unknown_addresses()
    {
        using var dir = new TempDir();
        var cfg = MakeCfg(dir, "pw");
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        await using var c = await Site(cfg, store);
        var icon = await c.Get("/favicon.ico"); // no sign-in needed
        Assert.Equal(("image/x-icon", "public, max-age=86400"), (icon.Content.Headers.ContentType!.MediaType, icon.Headers.CacheControl!.ToString()));
        Assert.Equal(File.ReadAllBytes(Path.Combine(RepoAssets(), "study-stash.ico")), await icon.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", (await c.Get("/apple-touch-icon.png")).Content.Headers.ContentType!.MediaType);
        var missing = await c.Get("/nowhere");
        Assert.Equal((HttpStatusCode.NotFound, "{\"detail\":\"Not Found\"}"), (missing.StatusCode, await missing.Content.ReadAsStringAsync()));
    }

    static string RepoAssets()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "granola_share", "assets"))) return Path.Combine(d.FullName, "granola_share", "assets");
        throw new DirectoryNotFoundException("granola_share/assets");
    }

    [Fact]
    public async Task A_class_with_a_slash_in_its_name_still_opens()
    {
        using var dir = new TempDir();
        var cfg = MakeCfg(dir);
        cfg.Classes.Add(new ClassDef("Lab / A"));
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        store.Save(new Meeting("l1") { Title = "Titration", Date = "2026-09-01" }, new Classification("Lab / A", 1, "human"));
        await using var c = await Site(cfg, store);
        var r = await c.Get(Ui.ClassUrl("Lab / A"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Titration", await r.Content.ReadAsStringAsync());
    }
}
