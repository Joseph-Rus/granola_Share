using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace StudyStash.Core.Tests;

/// <summary>
/// One library, two engines: the Python engine builds a library, the C# engine reads and changes it, and the
/// Python engine checks every row and file (engine/tests/crosscheck.py). This is what lets a computer switch
/// engines without touching its notes.
/// </summary>
public class CrossEngineTests(ITestOutputHelper output)
{
    static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "granola_share")) && Directory.Exists(Path.Combine(d.FullName, "engine")))
                return d.FullName;
        return null;
    }

    /// <summary>STUDYSTASH_PYTHON, or the repo's own .venv. CI sets STUDYSTASH_REQUIRE_PYTHON so it can't be skipped.</summary>
    static string? Python(string root)
    {
        string? set = Environment.GetEnvironmentVariable("STUDYSTASH_PYTHON");
        if (!string.IsNullOrEmpty(set)) return set;
        string venv = OperatingSystem.IsWindows()
            ? Path.Combine(root, ".venv", "Scripts", "python.exe") : Path.Combine(root, ".venv", "bin", "python");
        return File.Exists(venv) ? venv : null;
    }

    string Run(string python, string script, string step, string dir)
    {
        var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(step);
        psi.ArgumentList.Add(dir);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        output.WriteLine(stdout.Result + stderr);
        Assert.True(p.ExitCode == 0, $"python {step} failed:\n{stderr}");
        return stdout.Result;
    }

    static JsonNode? Column(NoteRow row, string name) => name switch
    {
        "id" => row.Id, "title" => row.Title, "date" => row.Date, "owner" => row.Owner, "attendees" => row.Attendees,
        "folder" => row.Folder, "class_name" => row.ClassName, "confidence" => row.Confidence is double d ? d : null,
        "classified_by" => row.ClassifiedBy, "lecture_title" => row.LectureTitle, "topics" => row.Topics,
        "md_path" => row.MdPath, "has_transcript" => row.HasTranscript is long l ? l : null, "raw_json" => row.RawJson,
        "first_seen" => row.FirstSeen, "updated_at" => row.UpdatedAt, "payload_json" => row.PayloadJson,
        "summary_md" => row.SummaryMd, "summary_model" => row.SummaryModel, "status" => row.Status, "error" => row.Error,
        _ => throw new ArgumentException($"a column the C# engine doesn't know: {name}"),
    };

    [Fact]
    public async Task A_python_library_opens_changes_and_goes_back_to_python()
    {
        string? root = RepoRoot();
        string? python = root is null ? null : Python(root);
        if (python is null)
        {
            Assert.True(Environment.GetEnvironmentVariable("STUDYSTASH_REQUIRE_PYTHON") != "1", "no Python engine to check against");
            output.WriteLine("skipped: no .venv with the Python engine (set STUDYSTASH_PYTHON)");
            return;
        }
        string script = Path.Combine(root!, "engine", "tests", "crosscheck.py");
        using var dir = new TempDir();
        Run(python, script, "make", dir.Path);
        var made = (JsonObject)JsonNode.Parse(File.ReadAllText(dir["python.json"]))!;

        // Everything Python wrote reads back the same: rows, lectures, lists, counts.
        var cfg = Configs.Load(made["home"].S());
        Assert.Equal("maple-otter", cfg.PoolPassword);
        using (var store = new Store(cfg.DbPath, cfg.PoolDir))
        {
            foreach (var (id, want) in made["rows"]!.AsObject())
            {
                var row = store.Get(id)!;
                foreach (var (col, value) in want!.AsObject())
                    Assert.True(JsonNode.DeepEquals(value, Column(row, col)), $"{id}.{col}: {value?.ToJsonString()} vs {Column(row, col)?.ToJsonString()}");
                var meeting = JsonNode.Parse(Granola.MeetingJson(store.Meeting(row)));
                Assert.True(JsonNode.DeepEquals(made["meetings"]![id], meeting), $"{id}: the lecture reads differently");
            }
            Assert.Equal(made["list"]!.AsArray().Select(n => n.S()), store.ListNotes().Select(r => r.Id));
            Assert.Equal(made["search"]!.AsArray().Select(n => n.S()), store.Search("membranes").Select(r => r.Id));
            Assert.Equal(made["classes"]!.AsArray().Select(c => (c![0].S(), c[1]!.GetValue<int>())), store.ClassesSummary());
            Assert.Equal(made["counts"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<int>()), store.StatusCounts());
            Assert.Equal(made["processing"]!.AsArray().Select(n => n.S()), store.Processing().Select(r => r.Id));
            Assert.Equal("2026-09-15T00:00:00+00:00", store.GetState("last_sync"));

            // Now the C# engine does a day's work in it.
            Assert.NotNull(store.SetClass("alpha-111111", "Chem 101"));
            Assert.True(store.Delete("delta-444444"));
            store.Enqueue(new Meeting("csharp-1")
            {
                Title = "Enzymes ☕ (from C#)", Date = "Sep 20, 2026 9:05 AM", Owner = "Bo", Attendees = ["Bo", "Zoë"],
                Folder = "Biology", NotesMarkdown = "# Enzymes\n- catalysts", Transcript = "Enzymes speed reactions up.",
                Raw = (JsonObject)JsonNode.Parse("""{"id": "csharp-1", "n": 2.50, "big": 12345678901234567890, "e": 1e-7}""")!,
            });
            store.Save(new Meeting("csharp-2") { Title = "Loops", Date = "2026-09-21", NotesMarkdown = "## For\n- range" },
                new Classification("CS 101", 0.875, "ollama", "For loops", ["loops", "é"]),
                "## Overview\nLoops.\n```\n# code\n```", "big:35b", keepGranola: true);
            store.SetState("csharp", "yes");
            int filed = await new Pipeline(cfg, store, log: _ => { }).RunPendingAsync();
            Assert.Equal(2, filed); // queued-1 and csharp-1
            File.WriteAllText(dir["csharp.json"], new JsonObject
            {
                ["keep_granola"] = new JsonArray("csharp-2"),
                ["filed"] = new JsonArray("queued-1", "csharp-1", "csharp-2"),
                ["columns"] = new JsonArray(Columns(cfg.DbPath).Select(c => (JsonNode?)c).ToArray()),
            }.ToJsonString());
        }

        Assert.Equal("ok", Run(python, script, "verify", dir.Path).Trim());
    }

    [Fact]
    public async Task The_python_laptop_talks_to_a_csharp_library()
    {
        string? root = RepoRoot();
        string? python = root is null ? null : Python(root);
        if (python is null)
        {
            Assert.True(Environment.GetEnvironmentVariable("STUDYSTASH_REQUIRE_PYTHON") != "1", "no Python engine to check against");
            output.WriteLine("skipped: no .venv with the Python engine (set STUDYSTASH_PYTHON)");
            return;
        }
        using var dir = new TempDir();
        var cfg = new Config(dir["home"], dir["pool"])
        {
            PoolName = "Cross — engine", PoolPassword = "maple otter", OllamaEnabled = false, WebHost = "127.0.0.1",
            Classes = [new ClassDef("CS 101", ["cs101"]), new ClassDef("Bio 110")],
        };
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        cfg.WebPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var pipeline = new Pipeline(cfg, store, log: _ => { });
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        Microsoft.AspNetCore.Hosting.WebHostBuilderKestrelExtensions.ConfigureKestrel(builder.WebHost,
            k => k.Listen(System.Net.IPAddress.Loopback, cfg.WebPort));
        await using var app = StudyStash.Library.LibraryWeb.Build(builder, cfg, store, pipeline);
        await app.StartAsync();

        var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { Path.Combine(root!, "engine", "tests", "laptop_check.py"), $"http://127.0.0.1:{cfg.WebPort}", cfg.PoolPassword, $"{cfg.WebPort}" })
            psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        string stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        output.WriteLine(await stdout + stderr);
        Assert.True(p.ExitCode == 0, $"the laptop's checks failed:\n{stderr}");
        Assert.Equal("ok", (await stdout).Trim());

        // What the laptop sent is in the library, and files like any lecture.
        Assert.Equal("queued", store.Get("laptop-1")!.Status);
        Assert.Equal(1, await pipeline.RunPendingAsync());
        var row = store.Get("laptop-1")!;
        Assert.Equal(("done", "CS 101", "Sam"), (row.Status, row.ClassName, row.Owner));
        Assert.Contains("We start with the base case.", Py.ReadText(row.MdPath!));
    }

    /// <summary>A C# library on a spare port on this computer, with its pipeline filing what arrives.</summary>
    sealed class CsLibrary : IAsyncDisposable
    {
        public Config Cfg { get; }
        public Store Store { get; }
        public string Url => $"http://127.0.0.1:{Cfg.WebPort}";
        readonly Microsoft.AspNetCore.Builder.WebApplication app;
        readonly CancellationTokenSource stop;
        readonly Task filing;

        CsLibrary(Config cfg, Store store, Microsoft.AspNetCore.Builder.WebApplication app, CancellationTokenSource stop, Task filing) =>
            (Cfg, Store, this.app, this.stop, this.filing) = (cfg, store, app, stop, filing);

        public static async Task<CsLibrary> StartAsync(TempDir dir)
        {
            var cfg = new Config(dir["library"], dir["notes"])
            {
                PoolName = "Cross — engine", PoolPassword = "maple otter", OllamaEnabled = false, WebHost = "127.0.0.1", WebPort = FreePort(),
                Classes = [new ClassDef("CS 101", ["cs101"]), new ClassDef("Bio 110", ["bio"])],
            };
            var store = new Store(cfg.DbPath, cfg.PoolDir);
            var pipeline = new Pipeline(cfg, store, log: _ => { });
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            Microsoft.AspNetCore.Hosting.WebHostBuilderKestrelExtensions.ConfigureKestrel(builder.WebHost,
                k => k.Listen(System.Net.IPAddress.Loopback, cfg.WebPort));
            var app = StudyStash.Library.LibraryWeb.Build(builder, cfg, store, pipeline);
            await app.StartAsync();
            var stop = new CancellationTokenSource();
            return new CsLibrary(cfg, store, app, stop, pipeline.Start(stop.Token));
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await app.StopAsync();
            await app.DisposeAsync();
            await filing.WaitAsync(TimeSpan.FromSeconds(10));
            Store.Dispose();
        }
    }

    static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    static readonly List<Meeting> Lectures =
    [
        new("cs-1") { Title = "CS101 lecture 6 — recursion", Date = "2026-09-17T10:00:00", Folder = "CS 101", NotesMarkdown = "# Recursion\n- base case",
            Raw = new JsonObject { ["id"] = "cs-1" } },
        new("cs-2") { Title = "Bio lab: diffusion", Date = "2026-09-18T14:00:00", NotesMarkdown = "Membranes and diffusion.", Raw = new JsonObject { ["id"] = "cs-2" } },
    ];

    /// <summary>Sends the lectures as the real watcher does, then checks on them until the library has filed both. What
    /// it told the person.</summary>
    static async Task<List<string>> SendAndWaitForFiling(ClientConfig cc)
    {
        var told = new List<string>();
        var host = new LaptopHost { Notify = (_, text) => told.Add(text) }; // the library's HTTP API for real; no popups
        var client = new ShareClient(cc, new FakeGranola(Lectures, Lectures, id => id == "cs-1" ? "We start with the base case." : ""), host, log: _ => { });
        var rep = await client.PollOnceAsync();
        Assert.Empty(rep.Errors);
        Assert.Equal(2, rep.Shared.Count);
        for (int i = 0; i < 60 && told.Count < 2; i++)
        {
            await Task.Delay(500);
            await client.PollOnceAsync();
        }
        return told;
    }

    [Fact]
    public async Task The_python_laptops_watcher_sends_to_a_csharp_library_until_its_filed()
    {
        string? root = RepoRoot();
        string? python = root is null ? null : Python(root);
        if (python is null)
        {
            Assert.True(Environment.GetEnvironmentVariable("STUDYSTASH_REQUIRE_PYTHON") != "1", "no Python engine to check against");
            return;
        }
        using var dir = new TempDir();
        await using var library = await CsLibrary.StartAsync(dir);
        var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { Path.Combine(root!, "engine", "tests", "laptop_flow.py"), library.Url, library.Cfg.PoolPassword, dir["laptop"] })
            psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        string stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        output.WriteLine(await stdout + stderr);
        Assert.True(p.ExitCode == 0, $"the laptop failed:\n{stderr}");
        var got = JsonNode.Parse(await stdout)!;
        Assert.Empty(got["errors"]!.AsArray());
        Assert.Equal(["CS101 lecture 5 — loops", "Bio lab: osmosis"], got["shared"]!.AsArray().Select(x => x![0].S()));
        Assert.Equal(2, got["filed"]!.AsArray().Count);
        Assert.Contains("“CS101 lecture 5 — loops” is in Cross — engine under CS 101.", got["told"]!.AsArray().Select(x => x.S()));
        var row = library.Store.Get("py-1")!;
        Assert.Equal(("done", "CS 101", "Sam"), (row.Status, row.ClassName, row.Owner));
        Assert.Contains("Today: for loops, then while loops.", Py.ReadText(row.MdPath!));
        Assert.Equal("done", library.Store.Get("py-2")!.Status);
    }

    [Fact]
    public async Task The_csharp_laptops_watcher_sends_to_a_python_library_until_its_filed()
    {
        string? root = RepoRoot();
        string? python = root is null ? null : Python(root);
        if (python is null)
        {
            Assert.True(Environment.GetEnvironmentVariable("STUDYSTASH_REQUIRE_PYTHON") != "1", "no Python engine to check against");
            return;
        }
        using var dir = new TempDir();
        var cfg = new Config(dir["library"], dir["notes"])
        {
            PoolName = "Cross — engine", PoolPassword = "maple otter", AdminPassword = "admin", OllamaEnabled = false, WebHost = "127.0.0.1",
            WebPort = FreePort(), AutoUpdate = false, Classes = [new ClassDef("CS 101", ["cs101"]), new ClassDef("Bio 110", ["bio"])],
        };
        Configs.Save(cfg); // this engine's config.toml, as the Python library reads it
        var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        foreach (string a in new[] { "-m", "granola_share.cli", "--home", cfg.Home, "run", "--no-ollama" }) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi)!;
        var log = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        string url = $"http://127.0.0.1:{cfg.WebPort}";
        try
        {
            JsonObject? health = null;
            for (int i = 0; i < 120 && health is null && !p.HasExited; i++)
            {
                try
                {
                    health = await LibraryApi.CheckServerAsync(url, cfg.PoolPassword);
                }
                catch (InvalidOperationException)
                {
                    await Task.Delay(250);
                }
            }
            Assert.True(health is not null, "the Python library didn't start:\n" + log);
            Assert.Equal(("Cross — engine", Engine.Version), (health!["pool_name"].S(), health["version"].S()));
            var cc = new ClientConfig(dir["laptop"]) { ServerUrl = url, PoolKey = cfg.PoolPassword, PoolName = "Cross — engine", DisplayName = "Ada" };
            var told = await SendAndWaitForFiling(cc);
            Assert.Contains("“CS101 lecture 6 — recursion” is in Cross — engine under CS 101.", told);
            Assert.Equal(2, told.Count);
            var status = await LibraryHttp.GetAsync($"{url}/api/notes/cs-1/status", cfg.PoolPassword);
            Assert.Equal(("done", "CS 101"), (status!["status"].S(), status["class_name"].S()));
            string note = Directory.EnumerateFiles(cfg.PoolDir, "*.md", SearchOption.AllDirectories).Single(f => f.Contains("recursion"));
            Assert.Contains("We start with the base case.", Py.ReadText(note));
        }
        finally
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await p.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task The_csharp_laptop_connects_on_its_page_and_sends_to_a_csharp_library()
    {
        using var dir = new TempDir();
        await using var library = await CsLibrary.StartAsync(dir);
        var told = new List<string>();
        string home = dir["laptop"];
        using var stop = new CancellationTokenSource();
        var granola = new FakeGranola(Lectures, Lectures, id => id == "cs-1" ? "We start with the base case." : "");
        var host = new LaptopHost { System = "Linux", Granola = _ => granola, Notify = (_, text) => told.Add(text), UserName = () => "ada" };
        var rt = new LaptopRuntime(home, host, stop, _ => { });
        await using var site = await TestSite.StartAsync(b => StudyStash.Library.LaptopWeb.Build(b, rt, 8765, ["localhost"]));
        string token = StudyStash.Library.AppPage.Token(home);
        await site.Get($"/?t={token}");
        async Task<JsonNode> Post(string path, object body)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, path) { Content = System.Net.Http.Json.JsonContent.Create(body) };
            r.Headers.TryAddWithoutValidation("X-Granola-Share", "1");
            var answer = await site.Client.SendAsync(r);
            string text = await answer.Content.ReadAsStringAsync();
            Assert.True(answer.IsSuccessStatusCode, text);
            return JsonNode.Parse(text)!;
        }
        // The page checks the address and password with the real library, as it does on a laptop.
        Assert.Equal("Connected to Cross — engine. Anything waiting is being sent now.",
            (await Post("/api/pool", new { server = library.Url.Replace("http://", ""), key = library.Cfg.PoolPassword }))["message"].S());
        File.WriteAllText(Configs.LoadClient(home).TokensPath, "{}"); // signed in to Granola (a stand-in one)
        Assert.Equal("All set. Checking Granola now…", (await Post("/api/finish", new { }))["message"].S());
        for (int i = 0; i < 80 && told.Count < 2; i++)
        {
            await Task.Delay(500);
            if (i % 4 == 3) await Post("/api/check", new { }); // "Check for new lectures now"
        }
        Assert.Contains("“CS101 lecture 6 — recursion” is in Cross — engine under CS 101.", told);
        Assert.Equal(2, told.Count);
        string page = await site.Text("/");
        Assert.Contains("Sending your lectures to Cross — engine.", page);
        Assert.Contains("Filed in CS 101 with its transcript", page);
        var row = library.Store.Get("cs-1")!;
        Assert.Equal(("done", "CS 101", "ada"), (row.Status, row.ClassName, row.Owner));
        await stop.CancelAsync();
    }

    static List<string> Columns(string db)
    {
        using var conn = new SqliteConnection($"Data Source={db};Pooling=False;Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(notes)";
        using var r = cmd.ExecuteReader();
        var cols = new List<string>();
        while (r.Read()) cols.Add(r.GetString(1));
        return cols;
    }

    [Fact]
    public void Json_columns_parse_in_both_directions()
    {
        // Numbers Python keeps exactly (a 20-digit int) and floats it rewrites (2.50 → 2.5) stay valid JSON here too.
        var raw = (JsonObject)JsonNode.Parse("""{"big": 12345678901234567890, "f": 2.50}""")!;
        Assert.Equal("{\"big\": 12345678901234567890, \"f\": 2.5}", PyJson.Dumps(raw));
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(PyJson.Dumps(raw)).RootElement.ValueKind);
    }
}
