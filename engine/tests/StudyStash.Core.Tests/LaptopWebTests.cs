using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using StudyStash.Library;

namespace StudyStash.Core.Tests;

/// <summary>A laptop runtime whose watcher, sign-in and copier are stand-ins (test_client_app.py's FakeRuntime).</summary>
public sealed class FakeLaptopRuntime(string home, string system = "Linux", LaptopInfo? ready = null, LaptopHost? host = null)
    : LaptopRuntime(home, host ?? new LaptopHost { System = system }, new CancellationTokenSource(), _ => { })
{
    public int Started, Logins, Permission, Checks, Reloads;
    public bool IsWatching;
    public bool Allowed;
    public string? FakeProblem, FakeSendKind;
    public string CopyWhy = "none here";

    public override bool Watching => IsWatching;
    public override LaptopInfo Readiness(bool fresh = false) => ready ?? new LaptopInfo(null, false, new TailscaleInfo(true, true));
    public override string? Problem => FakeProblem;
    public override string? SendProblemKind => FakeSendKind;

    public override CopyStatus CopyStatus() => Host.System == "Darwin"
        ? new CopyStatus(false, Config().CopyTranscripts, Allowed, CopyWhy)
        : new CopyStatus(false, false, false, "only on macOS for now");

    public override void StartWatching()
    {
        Started++;
        IsWatching = true;
    }

    public override void StartLogin()
    {
        Logins++;
        Login = new SignInState(true, null, 1);
    }

    public override void RequestPermission() => Permission++;
    public override void CheckNow() => Checks++;
    public override void Reload() => Reloads++;
}

/// <summary>tests/test_client_app.py: the laptop's Study Stash page (setup and status). Plus the page itself, in every
/// state golden.py drew it in, against the Python engine's bytes.</summary>
public class LaptopWebTests
{
    static Task<TestSite> Site(LaptopRuntime rt) => TestSite.StartAsync(b => LaptopWeb.Build(b, rt, 8765, ["localhost"], () => "NONCE"));

    /// <summary>A laptop whose library answers with this.</summary>
    static FakeLaptopRuntime Laptop(string home, Func<string, string, Task<JsonObject>> check, string system = "Linux") =>
        new(home, system, host: new LaptopHost { System = system, CheckServer = check, UserName = () => "sam" });

    static async Task SignIn(TestSite site, string home)
    {
        string token = File.ReadAllText(Path.Combine(home, "ui_token")).Trim();
        var r = await site.Get($"/?t={token}");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal(token, site.Cookie(AppPage.Cookie));
    }

    static Task<HttpResponseMessage> Post(TestSite site, string path, object? body = null, bool header = true)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        if (header) r.Headers.TryAddWithoutValidation("X-Granola-Share", "1");
        return site.Client.SendAsync(r);
    }

    static async Task<JsonObject> Json(HttpResponseMessage r) => (JsonObject)JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    static Func<string, string, Task<JsonObject>> Library(string name = "Fall pool") =>
        (_, _) => Task.FromResult(new JsonObject { ["pool_name"] = name, ["classes"] = new JsonArray("CS 101") });

    static TailscaleInfo Ts(JsonNode? t) => new(t?["installed"]?.GetValue<bool>() ?? false, t?["running"]?.GetValue<bool>() ?? false,
        t?["state"]?.S() ?? "", t?["dns"]?.S() ?? "", t?["ips"]?.AsArray().Select(i => i.S()).ToList());

    [Fact]
    public async Task Every_state_of_the_page_matches_the_python_engine_byte_for_byte()
    {
        if (OperatingSystem.IsWindows()) return; // the status page names the laptop's folder, and Windows spells it with backslashes
        var pages = (JsonObject)JsonNode.Parse(Golden.Text("laptop.json"))!["pages"]!;
        foreach (var (name, state) in pages["states"]!.AsObject())
        {
            var s = state!.AsArray();
            string system = s[0].S();
            var fields = s[1]!.AsObject();
            using var dir = new TempDir();
            string home = dir["home"];
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "ui_token"), "TOKEN");
            var cc = new ClientConfig(home);
            if (fields["server_url"] is JsonNode url) cc.ServerUrl = url.S();
            if (fields["pool_key"] is JsonNode key) cc.PoolKey = key.S();
            if (fields["pool_name"] is JsonNode pool) cc.PoolName = pool.S();
            if (fields["display_name"] is JsonNode who) cc.DisplayName = who.S();
            if (fields["mode"] is JsonNode mode) cc.Mode = mode.S();
            if (fields["copy_transcripts"] is JsonNode copy) cc.CopyTranscripts = copy.GetValue<bool>();
            if (fields.Count > 0) Configs.SaveClient(cc);
            if (s[2] is JsonObject prefill) File.WriteAllText(Path.Combine(home, "ui_prefill.json"), prefill.ToJsonString());
            if (s[3]!.GetValue<bool>()) File.WriteAllText(cc.TokensPath, "{}");
            if (s[6] is JsonObject seen) File.WriteAllText(cc.StatePath, new JsonObject { ["seen"] = seen.DeepClone(), ["last_poll"] = null }.ToJsonString());
            var r = s[7]!;
            var rt = new FakeLaptopRuntime(home, system, new LaptopInfo(r["granola"]?.GetValue<string>(), r["granola_here"]!.GetValue<bool>(), Ts(r["tailscale"])))
            {
                IsWatching = s[4]!.GetValue<bool>(),
            };
            var extra = s[5]!.AsObject();
            if (extra["login"] is JsonObject login)
                rt.Login = new SignInState(login["running"]!.GetValue<bool>(), login["error"]?.GetValue<string>(), login["started"]?.GetValue<double>());
            if (extra["tailscale_job"] is JsonObject job) rt.TailscaleJob = new JobState(job["running"]!.GetValue<bool>(), job["error"]?.GetValue<string>());
            if (extra["client"] is JsonObject client)
                (rt.FakeProblem, rt.FakeSendKind) = (client["last_error"]?.GetValue<string>(), client["send_problem_kind"]?.GetValue<string>());
            await using var site = await Site(rt);
            var want = pages["rendered"]![name]!;
            string Scrub(string text) => text.Replace(home, "{home}");
            Assert.Equal(want["unauthed"].S(), await site.Text("/"));
            site.SetCookie(AppPage.Cookie, "TOKEN");
            var page = await site.Get("/");
            Assert.Equal(want["status"]!.GetValue<int>(), (int)page.StatusCode);
            string html = Scrub(await page.Content.ReadAsStringAsync());
            Assert.True(want["html"].S() == html, $"{name}: differs from Python's at {Diff(want["html"].S(), html)}");
            Assert.Equal(want["csp"].S(), page.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal(want["state"].S(), Scrub(await site.Text("/api/state")));
            var library = await site.Get("/library");
            Assert.Equal(want["library"]!["status"]!.GetValue<int>(), (int)library.StatusCode);
            Assert.Equal(want["library"]!["html"].S(), await library.Content.ReadAsStringAsync());
            Assert.Equal(want["library"]!["csp"]?.GetValue<string>(),
                library.Headers.TryGetValues("Content-Security-Policy", out var csp) ? csp.Single() : null);
        }
    }

    static string Diff(string want, string got)
    {
        int i = 0;
        while (i < want.Length && i < got.Length && want[i] == got[i]) i++;
        return $"{i}:\npython: {want[Math.Max(0, i - 80)..Math.Min(want.Length, i + 80)]}\nc#:     {got[Math.Max(0, i - 80)..Math.Min(got.Length, i + 80)]}";
    }

    [Fact]
    public async Task The_page_needs_the_token_and_this_computer()
    {
        using var dir = new TempDir();
        var rt = new FakeLaptopRuntime(dir.Path);
        await using var site = await Site(rt);
        Assert.Contains("from your apps menu to see this page", await site.Text("/")); // no token: nothing about the setup
        Assert.Contains("from your apps menu to see this page", await site.Text("/?t=wrong"));
        Assert.Equal(("Applications folder", "Start Menu", "apps menu"), (LaptopWeb.WhereTheAppIs("Darwin"), LaptopWeb.WhereTheAppIs("Windows"), LaptopWeb.WhereTheAppIs("Linux")));
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(site, "/api/pool", new { server = "x" })).StatusCode);
        var rebinding = new HttpRequestMessage(HttpMethod.Get, "/");
        rebinding.Headers.Host = "evil.example:8765"; // DNS rebinding
        Assert.Equal(HttpStatusCode.Forbidden, (await site.Client.SendAsync(rebinding)).StatusCode);
        await SignIn(site, dir.Path);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(site, "/api/prefs", header: false)).StatusCode); // a cross-site form can't do this
        Assert.Contains("Connect to your library", await site.Text("/"));
        var health = (JsonObject)JsonNode.Parse(await site.Text("/healthz"))!;
        Assert.Equal(("granola-share", Engine.Version), (health["app"].S(), health["version"].S()));
    }

    [Fact]
    public async Task Setup_from_the_invite_to_finished()
    {
        using var dir = new TempDir();
        LaptopWeb.WritePrefill(dir.Path, "http://mini:8787", "pw");
        var rt = Laptop(dir.Path, Library(), "Darwin");
        await using var site = await Site(rt);
        await SignIn(site, dir.Path);
        string page = await site.Text("/");
        Assert.Contains("value=\"http://mini:8787\"", page); // from the invite line
        Assert.Contains("value=\"pw\"", page);
        Assert.True(File.Exists(Path.Combine(dir.Path, "ui_prefill.json"))); // kept until it connects, so a reload doesn't lose it

        var r = await Post(site, "/api/pool", new { server = "mini:8787", key = "pw" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Fall pool", (await Json(r))["message"].S());
        var cc = Configs.LoadClient(dir.Path);
        Assert.Equal(("http://mini:8787", "Fall pool", "sam"), (cc.ServerUrl, cc.PoolName, cc.DisplayName));
        Assert.False(File.Exists(Path.Combine(dir.Path, "ui_prefill.json")));

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(site, "/api/finish")).StatusCode); // not signed in yet
        Assert.Equal(HttpStatusCode.OK, (await Post(site, "/api/login")).StatusCode);
        Assert.Equal(1, rt.Logins);
        Assert.Contains("Waiting for you to finish signing in", await site.Text("/"));
        File.WriteAllText(cc.TokensPath, "{}"); // the Granola sign-in finished
        rt.Login = new SignInState();

        Assert.Equal(HttpStatusCode.OK, (await Post(site, "/api/prefs", new { display_name = "Alex", mode = "auto", copy_transcripts = true })).StatusCode);
        cc = Configs.LoadClient(dir.Path);
        Assert.Equal(("Alex", "auto", true), (cc.DisplayName, cc.Mode, cc.CopyTranscripts));
        Assert.False((await Json(await Post(site, "/api/allow")))["reload"]!.GetValue<bool>());
        Assert.Equal(1, rt.Permission);

        Assert.Equal(HttpStatusCode.OK, (await Post(site, "/api/finish")).StatusCode);
        Assert.Equal((1, 1), (rt.Started, rt.Checks));
        Assert.Contains("Sending your lectures to Fall pool", await site.Text("/"));
        Assert.Equal(HttpStatusCode.OK, (await Post(site, "/api/check")).StatusCode);
        Assert.Equal(2, rt.Checks);
        var state = (JsonObject)JsonNode.Parse(await site.Text("/api/state"))!;
        Assert.True(state["configured"]!.GetValue<bool>() && state["watching"]!.GetValue<bool>());
        Assert.Equal("Fall pool", state["pool_name"].S());
    }

    [Fact]
    public async Task Library_errors_say_what_to_check()
    {
        using var dir = new TempDir();
        var rt = Laptop(dir.Path, (_, _) => throw new InvalidOperationException("wrong password"));
        await using var site = await Site(rt);
        await SignIn(site, dir.Path);
        var r = await Post(site, "/api/pool", new { server = "mini:8787", key = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("Couldn't connect: wrong password. Check the password from your library's setup.", (await Json(r))["detail"].S());
        await using var down = await Site(Laptop(dir["b"], (_, _) => throw new InvalidOperationException("could not reach http://x/api/health: refused")));
        await SignIn(down, dir["b"]);
        Assert.EndsWith("Is Tailscale on on both computers, and is the library's computer awake?",
            (await Json(await Post(down, "/api/pool", new { server = "x", key = "k" })))["detail"].S());
    }

    [Fact]
    public async Task Connecting_to_a_different_library_restarts_into_setup()
    {
        using var dir = new TempDir();
        Configs.SaveClient(new ClientConfig(dir.Path) { ServerUrl = "http://mini:8787", PoolKey = "pw", PoolName = "Fall" });
        var rt = new FakeLaptopRuntime(dir.Path);
        await using var site = await Site(rt);
        await SignIn(site, dir.Path);
        Assert.Equal("Restarting into setup…", (await Json(await Post(site, "/api/reset-pool")))["message"].S());
        Assert.Equal("", Configs.LoadClient(dir.Path).ServerUrl);
        for (int i = 0; i < 50 && !rt.Stop.IsCancellationRequested; i++) await Task.Delay(50);
        Assert.True(rt.Stop.IsCancellationRequested); // the service stops, and starts again into setup
    }

    [Fact]
    public async Task Removing_is_off_unless_this_laptop_turns_it_on()
    {
        using var dir = new TempDir();
        var removed = new TaskCompletionSource();
        var rt = new LaptopRuntime(dir.Path, new LaptopHost { Remove = () => removed.SetResult() }, new CancellationTokenSource(), _ => { });
        await using var site = await Site(rt);
        await SignIn(site, dir.Path);
        Assert.False((await Json(await Post(site, "/api/remove")))["reload"]!.GetValue<bool>());
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => new LaptopHost().Remove());
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LaptopHost().SignIn(new ClientConfig(dir.Path), _ => { }));
        Assert.Throws<InvalidOperationException>(() => new LaptopHost().Granola(new ClientConfig(dir.Path)));
    }

    [Fact]
    public async Task The_status_page_asks_for_the_new_password_when_the_library_refuses_it()
    {
        using var dir = new TempDir();
        var cc = new ClientConfig(dir.Path) { ServerUrl = "http://mini:8787", PoolKey = "old", PoolName = "Fall pool", DisplayName = "Alex" };
        Configs.SaveClient(cc);
        File.WriteAllText(cc.TokensPath, "{}");
        File.WriteAllText(cc.StatePath, """{"seen": {"n1": {"decision": "pending", "title": "Membranes", "date": "2026-09-24", "transcript_chars": 8180, "at": "2026-09-24T19:37:49+00:00", "error": "401 Unauthorized"}}}""");
        var rt = new FakeLaptopRuntime(dir.Path)
        {
            IsWatching = true, FakeSendKind = "password", FakeProblem = "Fall pool turned down this laptop's password, so lectures are waiting here.",
        };
        await using var site = await Site(rt);
        await SignIn(site, dir.Path);
        string page = await site.Text("/");
        Assert.Contains("needs its new password", page);
        Assert.Contains("turned down this laptop", page);
        Assert.Contains("data-action=\"/api/pool\"", page);
        Assert.Contains("value=\"http://mini:8787\"", page);
        Assert.Contains("Reconnect", page);
        Assert.Contains("Waiting to send with its transcript", page);
        Assert.DoesNotContain("Waiting for your answer", page);
    }

    [Fact]
    public void Reconnecting_clears_the_password_warning_and_sends_right_away()
    {
        using var dir = new TempDir();
        Configs.SaveClient(new ClientConfig(dir.Path) { ServerUrl = "http://mini:8787", PoolKey = "old", PoolName = "Fall pool" });
        var rt = new LaptopRuntime(dir.Path, new LaptopHost(), new CancellationTokenSource(), _ => { });
        var client = new ShareClient(rt.Config(), new FakeGranola([]), rt.Host, log: _ => { })
        {
            SendProblem = "turned down", SendProblemKind = "password", LastError = "turned down",
        };
        rt.Client = client;
        Configs.SaveClient(new ClientConfig(dir.Path) { ServerUrl = "http://mini:8787", PoolKey = "new", PoolName = "Fall pool" }); // what Reconnect saves
        rt.Reload();
        Assert.Equal("new", client.Cc.PoolKey);
        Assert.Null(client.LastError); // no stale red card
        Assert.Null(client.SendProblemKind);
    }

    [Fact]
    public async Task The_status_page_asks_to_sign_in_again_when_granola_signed_out()
    {
        using var dir = new TempDir();
        var cc = new ClientConfig(dir.Path) { ServerUrl = "http://mini:8787", PoolKey = "pw", PoolName = "Fall pool", DisplayName = "Alex" };
        Configs.SaveClient(cc);
        File.WriteAllText(cc.TokensPath, "{}");
        var rt = new FakeLaptopRuntime(dir.Path) { IsWatching = true, FakeProblem = "token refresh failed (401): invalid_grant. Run `granola-share login`." };
        await using var site = await Site(rt);
        await SignIn(site, dir.Path);
        string page = await site.Text("/");
        Assert.Contains("Granola signed you out", page);
        Assert.Contains("Sign in to Granola again", page);
        Assert.Contains("needs you to sign in again", page);
        Assert.DoesNotContain("granola-share login", page); // no terminal commands on the page
    }

    [Fact]
    public async Task Open_your_library_signs_in_from_a_browser()
    {
        using var dir = new TempDir();
        var cc = new ClientConfig(dir.Path) { ServerUrl = "http://mini.tail.ts.net:8787", PoolKey = "p&w", PoolName = "Fall pool" };
        Configs.SaveClient(cc);
        File.WriteAllText(cc.TokensPath, "{}");
        var rt = new FakeLaptopRuntime(dir.Path, "Windows") { IsWatching = true };
        await using var site = await Site(rt);
        Assert.Equal(HttpStatusCode.SeeOther, (await site.Get("/library")).StatusCode); // not without the page's own cookie
        await SignIn(site, dir.Path);
        Assert.Contains("href=\"/library\"", await site.Text("/"));
        var r = await site.Get("/library");
        string html = await r.Content.ReadAsStringAsync();
        Assert.Contains("action=\"http://mini.tail.ts.net:8787/login\"", html);
        Assert.Contains("name=\"password\" value=\"p&amp;w\"", html);
        Assert.Contains(".submit()", html);
        string csp = r.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("form-action http://mini.tail.ts.net:8787", csp);
        Assert.DoesNotContain("'unsafe-inline'", csp.Split("script-src")[1].Split(';')[0]);
        Assert.Equal("no-store", r.Headers.CacheControl!.ToString());
        await using var mac = await Site(new FakeLaptopRuntime(dir.Path, "Darwin") { IsWatching = true }); // the Mac app signs in by itself
        await SignIn(mac, dir.Path);
        Assert.Contains("href=\"http://mini.tail.ts.net:8787\"", await mac.Text("/"));
    }

    [Fact]
    public async Task Pages_have_the_study_stash_icon()
    {
        using var dir = new TempDir();
        await using var site = await Site(new FakeLaptopRuntime(dir.Path));
        var r = await site.Get("/favicon.ico");
        Assert.Equal("image/x-icon", r.Content.Headers.ContentType!.MediaType);
        Assert.True((await r.Content.ReadAsByteArrayAsync()).Length > 1000);
        Assert.Contains("rel=\"icon\" href=\"/favicon.ico\"", await site.Text("/"));
    }

    [Fact]
    public void The_study_stash_icon_goes_where_each_system_keeps_apps()
    {
        using var dir = new TempDir();
        var at = new AppPlaces(dir["Applications"], dir["mine"], dir["Local"], dir["Roaming"], dir["userhome"]);
        var run = new FakeRunner();
        string? app = Launcher.Install(dir["home"], "Darwin", run.Run, at, ["/opt/studystash"]);
        Assert.Equal(Path.Combine(dir["mine"], "Study Stash.app"), app); // no /Applications here: this account's own
        string plist = File.ReadAllText(Path.Combine(app!, "Contents", "Info.plist"));
        Assert.Contains("<string>granola-share-app</string>", plist);
        Assert.Contains("LSUIElement", plist);
        string script = File.ReadAllText(Path.Combine(app!, "Contents", "MacOS", "granola-share-app"));
        Assert.Contains($"exec '/opt/studystash' '--home' '{dir["home"]}' 'client' 'open'", script);
        Assert.True(Launcher.Installed("Darwin", at));
        Launcher.Uninstall("Darwin", at);
        Assert.False(Launcher.Installed("Darwin", at));
        // the real app, once it's there, is never swapped for the script
        string native = Path.Combine(dir["mine"], "Study Stash.app", "Contents", "MacOS", "Study Stash");
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllText(native, "app");
        Assert.Equal(Path.Combine(dir["mine"], "Study Stash.app"), Launcher.Install(dir["home"], "Darwin", run.Run, at, ["/opt/studystash"]));
        Assert.Equal("app", File.ReadAllText(native));

        string? link = Launcher.Install(@"C:\Users\x\.granola-share", "Windows", run.Run, at, [@"C:\SS\studystash.exe"], icon: null);
        Assert.EndsWith("Study Stash.lnk", link);
        string ps = run.Calls[^1][^1];
        Assert.Contains("CreateShortcut", ps);
        Assert.Contains(@"$s.TargetPath='C:\SS\studystash.exe';$s.Arguments='--home C:\Users\x\.granola-share client open';", ps);
        Assert.Contains("$s.WindowStyle=7;", ps); // a console program: only a flash, minimized
        Assert.Equal("--home \"C:\\a b\\x\" client \"\" \"say \\\"hi\\\"\" C:\\\\end\\\\",
            Py.List2CmdLine(["--home", @"C:\a b\x", "client", "", "say \"hi\"", @"C:\\end\\"]));
    }
}
