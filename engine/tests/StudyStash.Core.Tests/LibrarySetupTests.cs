using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using StudyStash.Library;

namespace StudyStash.Core.Tests;

/// <summary>tests/test_library_setup.py: the library's setup as a page, so nobody needs a terminal. Plus the page itself
/// against the Python engine's, in four states.</summary>
public class LibrarySetupTests
{
    static readonly List<(string, double)> Models = [("qwen3:1.7b", 1.4)];
    static readonly TailscaleInfo Tailnet = new(true, true, "Running", "pc.tail.ts.net", ["100.64.0.9"]);

    /// <summary>golden.py's fakes: every check answered, and everything that changes the computer succeeds.</summary>
    static SetupHost Fakes(string system = "Darwin", Func<string, Task<List<(string, double)>?>>? listModels = null, Func<bool>? ollamaInstalled = null,
        Func<Action<string>, Action<long, long>, Task<bool>>? installOllama = null, Func<string, string, Action<long, long>, Task<(bool, string)>>? pull = null,
        Func<TailscaleInfo>? tailscale = null, Func<Action<string>, Action<long, long>, Task<bool>>? installTailscale = null, Func<int, bool?>? firewall = null,
        Func<int, Task<bool>>? openFirewall = null, Func<int?>? sleep = null, Func<double?>? ram = null, Func<double?>? disk = null,
        Func<int, Task<string>>? portStatus = null, Func<string, string, Task>? autostart = null) => new()
    {
        System = system,
        ListModels = listModels ?? (_ => Task.FromResult<List<(string, double)>?>(Models)),
        OllamaInstalled = ollamaInstalled ?? (() => true),
        StartOllama = _ => Task.FromResult(true),
        InstallOllama = installOllama ?? ((_, _) => throw new InvalidOperationException("Ollama is there")),
        PullModel = pull ?? ((_, _, _) => Task.FromResult((true, ""))),
        TryModel = (_, _) => Task.FromResult<(double?, string)>((2.0, "")),
        Tailscale = tailscale ?? (() => Tailnet),
        InstallTailscale = installTailscale ?? ((_, _) => throw new InvalidOperationException("Tailscale is there")),
        OpenTailscale = () => true,
        Firewall = firewall ?? (_ => true),
        OpenFirewall = openFirewall ?? (_ => Task.FromResult(true)),
        SleepMinutes = sleep ?? (() => 0),
        KeepAwake = () => true,
        RamGb = ram ?? (() => 32.0),
        DiskFree = disk ?? (() => 200.0),
        PortStatus = portStatus ?? (_ => Task.FromResult("free")),
        InstallAutostart = autostart ?? ((_, _) => Task.CompletedTask),
        WaitHealthy = _ => Task.FromResult(true),
        HostName = () => "library-pc",
        OpenUrl = _ => { },
    };

    static async Task<(LibrarySetup, TestSite)> Client(TempDir dir, SetupHost host)
    {
        var s = new LibrarySetup(dir["home"], host);
        var site = await TestSite.StartAsync(b => SetupWeb.Build(b, s, 8764, ["localhost"], () => "NONCE"));
        string token = File.ReadAllText(Path.Combine(dir["home"], "ui_token")).Trim();
        var first = await site.Get($"/?t={token}");
        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        Assert.Equal(token, site.Cookie(AppPage.Cookie));
        return (s, site);
    }

    static Task<HttpResponseMessage> Post(TestSite site, string path, object? body = null)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        r.Headers.TryAddWithoutValidation("X-Study-Stash", "1");
        return site.Client.SendAsync(r);
    }

    static async Task<JsonObject> Json(HttpResponseMessage r) => (JsonObject)JsonNode.Parse(await r.Content.ReadAsStringAsync())!;

    static async Task<JobView> Settle(LibrarySetup s, string name, int timeoutMs = 5000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 20)
        {
            if (s.Jobs.Get(name) is { Running: false } done) return done;
            await Task.Delay(20);
        }
        return s.Jobs.Get(name)!;
    }

    [Fact]
    public async Task Setup_from_start_to_finish_without_a_terminal()
    {
        using var dir = new TempDir();
        var started = new List<string>();
        var (s, c) = await Client(dir, Fakes(autostart: (role, _) =>
        {
            started.Add(role);
            return Task.CompletedTask;
        }));
        await using var _ = c;
        string page = await c.Text("/");
        Assert.Contains("Set up your library", page);
        Assert.Contains("connected", page);
        Assert.Contains("running", page);
        Assert.False(File.Exists(Path.Combine(dir["home"], "config.toml")));
        var r = await Post(c, "/api/library", new { name = "Fall 2026", password = "maple-otter", folder = dir["notes"], port = "8791" });
        Assert.Equal((HttpStatusCode.OK, "Saved."), (r.StatusCode, (await Json(r))["message"].S()));
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/classes", new { name = "Bio 110", aliases = "bio, bio110" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/classes", new { name = "Calc II" })).StatusCode);
        Assert.Equal("Removed Calc II.", (await Json(await Post(c, "/api/classes", new { remove = "1" })))["message"].S());
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/models", new { summary = "qwen3:1.7b", sort = "" })).StatusCode);
        Assert.Contains("answered in 2 s", (await Settle(s, "try")).Note);
        Assert.False(File.Exists(Path.Combine(dir["home"], "config.toml"))); // nothing is set up until you finish
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/finish")).StatusCode);
        Assert.Equal("Your library is ready.", (await Settle(s, "finish")).Note);
        Assert.Equal(["server"], started);
        var cfg = Configs.Load(dir["home"]);
        Assert.Equal(("Fall 2026", "maple-otter", 8791), (cfg.PoolName, cfg.PoolPassword, cfg.WebPort));
        Assert.Equal(["Bio 110"], cfg.ClassNames());
        Assert.Equal(["bio", "bio110"], cfg.Classes[0].Aliases);
        Assert.Equal(("qwen3:1.7b", "qwen3:1.7b", true), (cfg.SummaryModel, cfg.OllamaModel, cfg.OllamaEnabled));
        string done = await c.Text("/");
        Assert.Contains("http://pc.tail.ts.net:8791", done);
        Assert.Contains("maple-otter", done);
        Assert.Contains("Study-Stash-Laptop-Setup.exe", done);
        Assert.False(File.Exists(Path.Combine(dir["home"], "setup_draft.json")));
        var state = await Json(await c.Get("/api/state"));
        Assert.Equal((true, Engine.Version), (state["finished"]!.GetValue<bool>(), state["version"].S()));
    }

    [Fact]
    public async Task Only_this_computers_app_can_use_it()
    {
        using var dir = new TempDir();
        var (_, c) = await Client(dir, Fakes());
        await using var __ = c;
        var noHeader = await c.Client.PostAsync("/api/finish", null); // no header: a form on another site can't send it
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);
        c.ClearCookies();
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Get("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(c, "/api/library", new { })).StatusCode);
        var rebinding = new HttpRequestMessage(HttpMethod.Get, "/");
        rebinding.Headers.Host = "evil.example:8764"; // DNS rebinding
        Assert.Equal(HttpStatusCode.Forbidden, (await c.Client.SendAsync(rebinding)).StatusCode);
    }

    [Fact]
    public async Task Bad_answers_say_what_to_fix()
    {
        using var dir = new TempDir();
        var (_, c) = await Client(dir, Fakes(portStatus: _ => Task.FromResult("busy")));
        await using var __ = c;
        var r = await Post(c, "/api/library", new { name = "L", password = "pw", folder = dir.Path, port = "80" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("at least 4", (await Json(r))["detail"].S());
        r = await Post(c, "/api/library", new { name = "L", password = "long enough", folder = dir.Path, port = "8787" });
        Assert.Contains("Another app is using port 8787", (await Json(r))["detail"].S());
        r = await Post(c, "/api/library", new { name = "L", password = "long enough", folder = dir.Path, port = "80" });
        Assert.Contains("1024 to 65535", (await Json(r))["detail"].S());
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, "/api/finish")).StatusCode); // nothing saved yet
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, "/api/classes", new { name = "" })).StatusCode);
    }

    [Fact]
    public async Task Answers_survive_closing_the_window()
    {
        using var dir = new TempDir();
        var (_, c) = await Client(dir, Fakes());
        await using var __ = c;
        await Post(c, "/api/library", new { name = "Spring", password = "tulip-2027", folder = dir["n"], port = "8792" });
        await Post(c, "/api/classes", new { name = "Chem 101" });
        var again = new LibrarySetup(dir["home"], Fakes());
        Assert.Equal(("Spring", 8792), (again.Cfg.PoolName, again.Cfg.WebPort));
        Assert.Equal(["Chem 101"], again.Cfg.ClassNames());
        Assert.True(again.DraftLibrary);
    }

    [Fact]
    public async Task Missing_ollama_and_tailscale_get_installed_from_the_page()
    {
        using var dir = new TempDir();
        bool ollama = false, tailscale = false;
        var (s, c) = await Client(dir, Fakes(
            listModels: _ => Task.FromResult(ollama ? Models : null),
            ollamaInstalled: () => ollama,
            installOllama: (_, progress) =>
            {
                progress(50, 100);
                ollama = true;
                return Task.FromResult(true);
            },
            tailscale: () => tailscale ? Tailnet : new TailscaleInfo(),
            installTailscale: (_, _) =>
            {
                tailscale = true;
                return Task.FromResult(true);
            }));
        await using var __ = c;
        string page = await c.Text("/");
        Assert.Contains("Install Ollama", page);
        Assert.Contains("Install Tailscale", page);
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/ollama")).StatusCode);
        Assert.Equal("Ollama is running.", (await Settle(s, "ollama")).Note);
        Assert.Equal(HttpStatusCode.OK, (await Post(c, "/api/tailscale")).StatusCode);
        Assert.Contains("installer is open", (await Settle(s, "tailscale")).Note);
        await s.ChecksAsync(fresh: true);
        page = await c.Text("/");
        Assert.DoesNotContain("Install Ollama", page);
        Assert.DoesNotContain("Install Tailscale", page);
    }

    [Fact]
    public async Task A_missing_model_downloads_then_answers_once()
    {
        using var dir = new TempDir();
        var have = new List<(string, double)>();
        var (s, c) = await Client(dir, Fakes(
            listModels: _ => Task.FromResult<List<(string, double)>?>([.. have]),
            pull: (model, _, progress) =>
            {
                progress(1, 2);
                progress(2, 2);
                have.Add((model, 2.0));
                return Task.FromResult((true, ""));
            }));
        await using var __ = c;
        await Post(c, "/api/library", new { name = "L", password = "long enough", folder = dir["n"], port = "8793" });
        Assert.Equal("Downloading gemma4:e4b.", (await Json(await Post(c, "/api/models", new { summary = "gemma4:e4b" })))["message"].S());
        Assert.Equal("Downloaded gemma4:e4b.", (await Settle(s, "pull")).Note);
        await Task.Delay(50);
        Assert.Contains("answered", (await Settle(s, "try")).Note);
        var jobs = (await Json(await c.Get("/api/state")))["jobs"]!;
        Assert.Equal((2L, 2L), (jobs["pull"]!["done"]!.GetValue<long>(), jobs["pull"]!["total"]!.GetValue<long>()));
    }

    [Fact]
    public async Task Windows_asks_about_its_firewall_and_sleep()
    {
        using var dir = new TempDir();
        var opened = new List<int>();
        var (s, c) = await Client(dir, Fakes("Windows", firewall: _ => false, sleep: () => 30, openFirewall: port =>
        {
            opened.Add(port);
            return Task.FromResult(true);
        }));
        await using var __ = c;
        await Post(c, "/api/library", new { name = "L", password = "long enough", folder = dir["n"], port = "8794" });
        string page = await c.Text("/");
        Assert.Contains("Let my laptop in", page);
        Assert.Contains("Keep it awake while plugged in", page);
        await Post(c, "/api/firewall");
        Assert.Contains("can reach port 8794", (await Settle(s, "firewall")).Note);
        Assert.Equal([8794], opened);
        Assert.Contains("stays awake", (await Json(await Post(c, "/api/awake")))["message"].S());
    }

    [Fact]
    public async Task A_failed_install_says_where_to_get_it_and_nothing_installs_by_default()
    {
        using var dir = new TempDir();
        var s = new LibrarySetup(dir["home"], Fakes(listModels: _ => Task.FromResult<List<(string, double)>?>(null), ollamaInstalled: () => false,
            installOllama: (_, _) => Task.FromResult(false), tailscale: () => new TailscaleInfo()));
        await s.FixOllamaAsync();
        Assert.Equal("Ollama didn't install. Get it from https://ollama.com, then come back.", (await Settle(s, "ollama")).Note);

        // A setup that isn't this computer's own page changes nothing on it: each of these fails instead.
        var bare = new LibrarySetup(dir["other"], new SetupHost
        {
            ListModels = _ => Task.FromResult<List<(string, double)>?>(null), OllamaInstalled = () => false,
            Tailscale = () => new TailscaleInfo(), RamGb = () => 8, DiskFree = () => 100, SleepMinutes = () => 0,
        });
        await bare.FixOllamaAsync();
        var ollama = await Settle(bare, "ollama");
        Assert.True(ollama.Error);
        Assert.Equal("Installing Ollama is off for this setup.", ollama.Note);
        await bare.FixTailscaleAsync();
        Assert.Equal("Installing Tailscale is off for this setup.", (await Settle(bare, "tailscale")).Note);
        bare.FixFirewall();
        Assert.Equal("Changing Windows Firewall is off for this setup.", (await Settle(bare, "firewall")).Note);
        Assert.Throws<InvalidOperationException>(() => bare.StayAwake());
    }

    [Fact]
    public async Task Connecting_tailscale_opens_its_sign_in_and_gives_up_when_nobody_signs_in()
    {
        if (OperatingSystem.IsWindows()) return; // a shell script stands in for `tailscale up`
        using var dir = new TempDir();
        string exe = dir["tailscale"];
        File.WriteAllText(exe, "#!/bin/sh\necho 'To authenticate, visit: https://login.tailscale.com/a/abc123'\nsleep 30\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var opened = new List<string>();
        var s = new LibrarySetup(dir["home"], new SetupHost
        {
            ListModels = _ => Task.FromResult<List<(string, double)>?>(Models), OllamaInstalled = () => true, RamGb = () => 8, DiskFree = () => 100,
            Tailscale = () => new TailscaleInfo(true, false, "NeedsLogin", Exe: exe), OpenTailscale = () => false, // no app to open: Linux
            OpenUrl = opened.Add, ConnectTimeout = TimeSpan.FromSeconds(1),
        });
        Assert.Equal("Connecting Tailscale...", await s.FixTailscaleAsync());
        var job = await Settle(s, "tailscale");
        Assert.Equal(("Tailscale didn't connect.", false), (job.Note, job.Running));
        Assert.Equal(["https://login.tailscale.com/a/abc123"], opened);
    }

    [Fact]
    public async Task The_page_matches_the_python_engine_in_every_state()
    {
        if (OperatingSystem.IsWindows()) return; // the page shows the notes folder, and Windows spells it with backslashes
        foreach (var (name, want, html) in await GoldenStates())
            Assert.True(html == want, $"setup page, {name}: differs from Python's at {Diff(want, html)}");
    }

    [Fact]
    public async Task No_setup_state_mentions_granola()
    {
        foreach (var (name, _, html) in await GoldenStates())
        {
            Assert.False(html.Contains("granola", StringComparison.OrdinalIgnoreCase), $"setup page, {name}, mentions Granola");
            Assert.DoesNotContain("install.sh", html); // the laptop gets the app, not a line for a terminal
        }
    }

    /// <summary>The setup page in each state the fixture keeps (fresh, windows, mac-asleep, finished), as this
    /// engine draws it, with the fixture's copy.</summary>
    static async Task<List<(string Name, string Want, string Html)>> GoldenStates()
    {
        var states = new List<(string, string, string)>();
        foreach (var (name, want) in Golden.PageCases()["setup"]!.AsObject())
        {
            using var dir = new TempDir();
            Directory.CreateDirectory(dir["home"]);
            File.WriteAllText(Path.Combine(dir["home"], "setup_draft.json"), want!["draft"]!.ToJsonString());
            // golden.py's fakes: its two models, its tailnet, 200.4 GB free, unless the state says otherwise
            List<(string, double)> pageModels = [("qwen3:1.7b", 1.4), ("gemma4:e4b", 9.6)];
            var tailnet = new TailscaleInfo(true, true, "Running", "mini.tail.ts.net", ["100.64.0.9"]);
            Func<string, Task<List<(string, double)>?>> models = _ => Task.FromResult<List<(string, double)>?>(pageModels);
            var host = name switch
            {
                "fresh" => Fakes(listModels: _ => Task.FromResult<List<(string, double)>?>(null), ollamaInstalled: () => false,
                    tailscale: () => new TailscaleInfo(), ram: () => 15.6, disk: () => 12.5),
                "windows" => Fakes("Windows", listModels: models, firewall: _ => false, sleep: () => 30,
                    tailscale: () => new TailscaleInfo(true, false, "NeedsLogin"), ram: () => null, disk: () => null),
                "mac-asleep" => Fakes(sleep: () => 1, listModels: _ => Task.FromResult<List<(string, double)>?>([]), tailscale: () => tailnet, disk: () => 200.4),
                _ => Fakes(listModels: models, tailscale: () => tailnet, disk: () => 200.4),
            };
            var s = new LibrarySetup(dir["home"], host);
            if (name == "finished") s.Finished = true;
            states.Add((name, want["html"].S(), await SetupWeb.RenderAsync(s)));
        }
        return states;
    }

    static string Diff(string want, string got)
    {
        int i = 0;
        while (i < want.Length && i < got.Length && want[i] == got[i]) i++;
        return $"{i}:\npython: {want[Math.Max(0, i - 60)..Math.Min(want.Length, i + 60)]}\nc#:     {got[Math.Max(0, i - 60)..Math.Min(got.Length, i + 60)]}";
    }

    [Fact]
    public async Task Healthz_says_which_app_this_is()
    {
        using var dir = new TempDir();
        var (_, c) = await Client(dir, Fakes());
        await using var __ = c;
        var health = await Json(await c.Get("/healthz"));
        Assert.Equal(("study-stash-setup", Engine.Version), (health["app"].S(), health["version"].S()));
    }
}
