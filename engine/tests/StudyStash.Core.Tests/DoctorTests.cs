using System.Net;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Tests;

/// <summary>tests/test_doctor.py, and every scenario golden.py gave the Python engine's doctor: the same checks, the same
/// words, printed the same way.</summary>
public class DoctorTests
{
    static JsonObject P => Golden.Platform();

    static TailscaleInfo Ts(JsonNode? t) => new(t?["installed"]?.GetValue<bool>() ?? false, t?["running"]?.GetValue<bool>() ?? false,
        t?["state"]?.S() ?? "", t?["dns"]?.S() ?? "", t?["ips"]?.AsArray().Select(i => i.S()).ToList());

    static Release? Rel(JsonNode? tag) => tag is null ? null : new Release(tag.S(), Updates.ParseVersion(tag.S()), "u", "p");

    static Func<Task<Release?>> Latest(JsonNode? tag) => () => Task.FromResult(Rel(tag));

    /// <summary>The temporary folder as golden.py wrote it; on Windows, with its forward slashes.</summary>
    static string Scrub(string text, string root)
    {
        string s = text.Replace(root, "{root}");
        return OperatingSystem.IsWindows() ? s.Replace('\\', '/') : s;
    }

    static void Matches(string name, JsonNode want, List<Check> checks, string root)
    {
        var got = new JsonArray(checks.Select(c => (JsonNode?)new JsonArray(c.Name, c.State, Scrub(c.Detail, root), Scrub(c.Fix, root))).ToArray());
        Assert.True(JsonNode.DeepEquals(want["checks"], got), $"{name}:\npython: {want["checks"]!.ToJsonString()}\nc#:     {got.ToJsonString()}");
        Assert.Equal(want["unicode"].S(), Scrub(Doctor.FormatChecks("title", checks), root));
        Assert.Equal(want["ascii"].S(), Scrub(Doctor.FormatChecks("title", checks, unicode: false), root));
    }

    [Fact]
    public async Task The_library_checks_match_python_in_every_scenario()
    {
        foreach (var (name, want) in P["server"]!.AsObject())
        {
            var g = want!["given"]!;
            using var dir = new TempDir();
            var cfg = new Config(dir["home"], dir["pool"])
            {
                PoolPassword = "pw", OllamaModel = "qwen3:1.7b", SummaryModel = "big:35b", Classes = [new ClassDef("CS 101")],
            };
            if (g["cfg"] is JsonObject over)
            {
                if (over["classes"] is JsonArray none) cfg.Classes = none.Select(c => new ClassDef(c!["name"].S())).ToList();
                if (over["pool_password"] is JsonNode pw) cfg.PoolPassword = pw.S();
                if (over["server_sync"] is JsonNode sync) cfg.ServerSync = sync.GetValue<bool>();
                if (over["ollama_enabled"] is JsonNode ai) cfg.OllamaEnabled = ai.GetValue<bool>();
                if (over["summary_enabled"] is JsonNode sum) cfg.SummaryEnabled = sum.GetValue<bool>();
            }
            Configs.Save(cfg);
            if (g["log"] is JsonNode log)
            {
                Directory.CreateDirectory(cfg.LogDir);
                File.WriteAllText(Path.Combine(cfg.LogDir, "server.log"), log.S());
            }
            var health = g["health"];
            var models = g["models"] as JsonArray;
            var host = new DoctorHost
            {
                System = g["system"].S(),
                HealthGet = (_, _) => health is null ? throw new HttpRequestException("refused")
                    : Task.FromResult((health["status"]!.GetValue<int>(), health["body"]!.ToJsonString())),
                ServiceStatus = _ => g["service"].S(),
                ListModels = _ => Task.FromResult(models?.Select(m => (m![0].S(), m[1]!.GetValue<double>())).ToList()),
                OllamaInstalled = () => g["ollama_installed"]?.GetValue<bool>() ?? false,
                Tailscale = () => Ts(g["tailscale"]),
                SleepMinutes = () => g["sleep"]?.GetValue<int>(),
                Firewall = _ => g["firewall"]?.GetValue<bool>(),
                Latest = Latest(g["latest"]),
                HostName = () => "library-pc",
            };
            Matches(name, want, await Doctor.ServerChecksAsync(cfg, host), dir.Path);
        }
    }

    [Fact]
    public async Task The_laptop_checks_match_python_in_every_scenario()
    {
        foreach (var (name, want) in P["client"]!.AsObject())
        {
            var g = want!["given"]!;
            using var dir = new TempDir();
            var cc = new ClientConfig(dir["home"]) { ServerUrl = "http://mini:8787", PoolKey = "pw", PoolName = "Fall" };
            if (g["cfg"] is JsonObject over)
            {
                if (over["copy_transcripts"] is JsonNode copy) cc.CopyTranscripts = copy.GetValue<bool>();
                if (over["mode"] is JsonNode mode) cc.Mode = mode.S();
                if (over["server_url"] is JsonNode url) cc.ServerUrl = url.S();
            }
            Configs.SaveClient(cc);
            if (g["tokens"]?.GetValue<bool>() ?? true) File.WriteAllText(cc.TokensPath, "{}");
            if (g["status"] is JsonNode status)
            {
                Directory.CreateDirectory(Path.Combine(cc.Home, "transcripts"));
                File.WriteAllText(Path.Combine(cc.Home, "transcripts", "status.json"), status.ToJsonString());
            }
            if (g["log"] is JsonNode log)
            {
                Directory.CreateDirectory(cc.LogDir);
                File.WriteAllText(Path.Combine(cc.LogDir, "client.log"), log.S());
            }
            var server = g["server"]!;
            var probe = g["probe"] ?? new JsonObject { ["ok"] = new JsonArray(0, null) };
            var laptop = g["laptop"] ?? new JsonObject { ["granola"] = null, ["granola_here"] = false, ["tailscale"] = new JsonObject { ["running"] = true } };
            var host = new DoctorHost
            {
                System = g["system"].S(),
                CheckServer = (_, _) => server["error"] is JsonNode e ? throw new InvalidOperationException(e.S())
                    : Task.FromResult(server["ok"]!.DeepClone().AsObject()),
                Probe = _ => probe["error"] is JsonNode e ? throw new InvalidOperationException(e.S())
                    : Task.FromResult((probe["ok"]![0]!.GetValue<int>(), probe["ok"]![1]?.GetValue<bool>())),
                ServiceStatus = _ => g["service"].S(),
                Latest = Latest(g["latest"]),
                Laptop = () => new LaptopInfo(laptop["granola"]?.S(), laptop["granola_here"]!.GetValue<bool>(), Ts(laptop["tailscale"])),
            };
            Matches(name, want, await Doctor.ClientChecksAsync(cc, host), dir.Path);
        }
    }

    [Fact]
    public async Task Nothing_set_up_says_what_to_run()
    {
        using var dir = new TempDir();
        var said = new List<string>();
        Assert.Equal(1, await Doctor.RunAsync(dir.Path, null, new DoctorHost(), said.Add));
        Assert.Equal(P["nothing"]!.AsArray().Select(s => s.S()), said.Select(s => Scrub(s, dir.Path)));
    }

    [Fact]
    public async Task Doctor_prints_each_role_and_fails_when_anything_does()
    {
        using var dir = new TempDir();
        Configs.Save(new Config(dir.Path, dir["pool"]) { PoolPassword = "pw", OllamaEnabled = false });
        var host = new DoctorHost
        {
            System = "Linux", HealthGet = (_, _) => Task.FromResult((200, $$"""{"version": "{{Engine.Version}}"}""")), ServiceStatus = _ => "running",
            Tailscale = () => new TailscaleInfo(true, true), Latest = () => Task.FromResult<Release?>(null), HostName = () => "pc", Unicode = false,
        };
        var said = new List<string>();
        Assert.Equal(0, await Doctor.RunAsync(dir.Path, null, host, said.Add));
        Assert.StartsWith($"granola-share {Engine.Version}: library ({dir.Path})\n", said[0]);
        Assert.Equal("All good.", said[^1]);
        var down = new DoctorHost
        {
            System = "Linux", HealthGet = (_, _) => throw new HttpRequestException("refused"), ServiceStatus = _ => "missing",
            Tailscale = () => new TailscaleInfo(true, true), Latest = () => Task.FromResult<Release?>(null), HostName = () => "pc", Unicode = false,
        };
        said.Clear();
        Assert.Equal(1, await Doctor.RunAsync(dir.Path, "server", down, said.Add));
        Assert.Equal("Fix the X items above, then run `granola-share doctor` again.", said[^1]);
    }

    [Fact]
    public async Task Asking_granola_is_off_unless_this_computer_asks()
    {
        using var dir = new TempDir();
        var cc = new ClientConfig(dir.Path) { ServerUrl = "http://mini:8787", PoolKey = "pw" };
        Configs.SaveClient(cc);
        File.WriteAllText(cc.TokensPath, "{}");
        var checks = await Doctor.ClientChecksAsync(cc, new DoctorHost
        {
            System = "Linux", CheckServer = (_, _) => Task.FromResult(new JsonObject()), ServiceStatus = _ => "running",
            Latest = () => Task.FromResult<Release?>(null), Laptop = () => new LaptopInfo(null, false, new TailscaleInfo(true, true)),
        });
        Assert.Equal("signed in, but Granola refused: Asking Granola is off for this check.", checks.Single(c => c.Name == "Granola").Detail);
    }

    [Fact]
    public async Task The_laptop_reads_the_librarys_health_check()
    {
        var library = new FakeOllama((path, _) => path == "/api/health" ? JsonNode.Parse("""{"pool_name": "Fall", "version": "0.4.4"}""")! : (HttpStatusCode.NotFound, "{}"));
        Assert.Equal("Fall", (await LibraryApi.CheckServerAsync("http://mini:8787/", "pw", library.Client()))["pool_name"].S());
        var wrong = new FakeOllama((_, _) => (HttpStatusCode.Unauthorized, """{"detail": "bad pool password"}"""));
        Assert.Equal("wrong password", (await Assert.ThrowsAsync<InvalidOperationException>(() => LibraryApi.CheckServerAsync("http://mini:8787", "x", wrong.Client()))).Message);
        var odd = new FakeOllama((_, _) => (HttpStatusCode.BadGateway, "{}"));
        Assert.Equal("unexpected response 502 from http://mini:8787/api/health",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => LibraryApi.CheckServerAsync("http://mini:8787", "x", odd.Client()))).Message);
        var nobody = new HttpClient(new Refuses());
        Assert.StartsWith("could not reach http://mini:8787/api/health: ",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => LibraryApi.CheckServerAsync("http://mini:8787", "x", nobody))).Message);
    }

    sealed class Refuses : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");
    }
}
