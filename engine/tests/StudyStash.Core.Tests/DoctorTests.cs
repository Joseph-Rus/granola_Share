using System.Net;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Tests;

/// <summary>Every scenario the retired Python engine's doctor answered: the same checks, the same words, printed
/// the same way.</summary>
public class DoctorTests
{
    static JsonObject P => Golden.Platform();

    static TailscaleInfo Ts(JsonNode? t) => new(t?["installed"]?.GetValue<bool>() ?? false, t?["running"]?.GetValue<bool>() ?? false,
        t?["state"]?.S() ?? "", t?["dns"]?.S() ?? "", t?["ips"]?.AsArray().Select(i => i.S()).ToList());

    static Release? Rel(JsonNode? tag) => tag is null ? null : new Release(tag.S(), Updates.ParseVersion(tag.S()), "u", "p");

    static Func<Task<Release?>> Latest(JsonNode? tag) => () => Task.FromResult(Rel(tag));

    /// <summary>The temporary folder as the fixed fixture wrote it; on Windows, with its forward slashes.</summary>
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
        Assert.StartsWith($"studystash {Engine.Version}: library ({dir.Path})\n", said[0]);
        Assert.Equal("All good.", said[^1]);
        var down = new DoctorHost
        {
            System = "Linux", HealthGet = (_, _) => throw new HttpRequestException("refused"), ServiceStatus = _ => "missing",
            Tailscale = () => new TailscaleInfo(true, true), Latest = () => Task.FromResult<Release?>(null), HostName = () => "pc", Unicode = false,
        };
        said.Clear();
        Assert.Equal(1, await Doctor.RunAsync(dir.Path, "server", down, said.Add));
        Assert.Equal("Fix the X items above, then run `studystash doctor` again.", said[^1]);
    }

    [Fact]
    public async Task The_laptop_checks_say_whats_connected_and_whats_waiting()
    {
        using var dir = new TempDir();
        var cc = new ClientConfig(dir.Path) { ServerUrl = "http://mini:8787", PoolKey = "pw", PoolName = "Fall" };
        DoctorHost Host(Func<Task<JsonObject>>? library = null, TailscaleInfo? ts = null) => new()
        {
            System = "Darwin", CheckServer = (_, _) => (library ?? (() => Task.FromResult(new JsonObject { ["pool_name"] = "Fall" })))(),
            Tailscale = () => ts ?? new TailscaleInfo(true, true, "Running", "sams-laptop.example.ts.net"), Latest = () => Task.FromResult<Release?>(null),
            ServiceStatus = _ => throw new InvalidOperationException("a laptop has no background service to check"), Unicode = false,
        };
        const string connect = "open Study Stash and connect it to your library";

        // Not connected to a library yet: that's the one thing to do.
        Assert.Equal([new Check("Config", Doctor.Fail, $"no {cc.ConfigPath}", connect)], await Doctor.ClientChecksAsync(cc, Host()));
        var blank = new ClientConfig(dir.Path);
        Configs.SaveClient(blank);
        Assert.Equal([new Check("Config", Doctor.Fail, $"{cc.ConfigPath} names no library", connect)], await Doctor.ClientChecksAsync(blank, Host()));

        // Connected, nothing recorded yet: all good, and looking made no recordings folder.
        Configs.SaveClient(cc);
        var fine = await Doctor.ClientChecksAsync(cc, Host());
        Assert.Equal(["Config", "Library", "Tailscale", "Recordings", "Version"], fine.Select(c => c.Name));
        Assert.All(fine, c => Assert.Equal(Doctor.Ok, c.State));
        Assert.Equal(("'Fall' at http://mini:8787", "connected as sams-laptop.example.ts.net", "nothing recorded yet"),
            (fine[1].Detail, fine[2].Detail, fine[3].Detail));
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "recordings")));

        // The library doesn't answer, and Tailscale is signed out.
        var down = await Doctor.ClientChecksAsync(cc, Host(() => throw new InvalidOperationException("could not reach http://mini:8787/api/health: refused"),
            new TailscaleInfo(true, false, "NeedsLogin")));
        var library = down.Single(c => c.Name == "Library");
        Assert.Equal((Doctor.Fail, "http://mini:8787: could not reach http://mini:8787/api/health: refused"), (library.State, library.Detail));
        Assert.Equal("is Tailscale on on both computers, and is the library's computer awake? Wrong address or password: " + connect + " again", library.Fix);
        Assert.Equal(new Check("Tailscale", Doctor.Warn, "installed, but signed out; this computer reaches the library only on the same Wi-Fi",
            "open Tailscale and sign in with the same account as your library's computer"), down.Single(c => c.Name == "Tailscale"));

        // Two lectures on their way (one tried and couldn't get there), one already filed.
        var store = new LectureStore(dir.Path);
        store.Add(new Lecture { Id = "rec-1", Started = "2026-09-21T10:00:00-07:00", State = LectureState.Filed, FiledClass = "CS 101" });
        store.Add(new Lecture { Id = "rec-2", Started = "2026-09-22T10:00:00-07:00", State = LectureState.Sending, Error = "Can't reach your library. Lectures wait here until it's back." });
        store.Add(new Lecture { Id = "rec-3", Started = "2026-09-23T10:00:00-07:00", State = LectureState.Transcribing });
        async Task<Check> Recordings() => (await Doctor.ClientChecksAsync(cc, Host())).Single(c => c.Name == "Recordings");
        Assert.Equal(new Check("Recordings", Doctor.Warn, "2 lectures waiting to reach Fall",
            "last try: Can't reach your library. Lectures wait here until it's back."), await Recordings());
        store.Update("rec-2", l => l.Error = "");
        Assert.Equal("they go on their own while Study Stash is open and the library answers", (await Recordings()).Fix);

        // Sent, but Whisper couldn't write one down.
        store.Update("rec-2", l => l.State = LectureState.Filed);
        store.Update("rec-3", l => (l.State, l.Error) = (LectureState.Failed, "its recording is missing"));
        Assert.Equal(new Check("Recordings", Doctor.Warn, "1 lecture couldn't be written down: its recording is missing", "open Study Stash to see which"), await Recordings());
        store.Delete("rec-3");
        Assert.Equal(new Check("Recordings", Doctor.Ok, "2 lectures filed in Fall"), await Recordings());

        // Doctor prints them as the laptop's, and warnings alone don't fail it.
        var said = new List<string>();
        Assert.Equal(0, await Doctor.RunAsync(dir.Path, null, Host(ts: new TailscaleInfo()), said.Add));
        Assert.Contains($": laptop ({dir.Path})\n", said[0]);
        Assert.Contains("! Tailscale   not installed; this computer reaches the library only on the same Wi-Fi", said[0]);
        Assert.Equal("All good.", said[^1]);
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
