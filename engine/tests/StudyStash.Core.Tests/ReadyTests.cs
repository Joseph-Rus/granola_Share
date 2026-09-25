using System.Text;

namespace StudyStash.Core.Tests;

/// <summary>tests/test_ready.py: getting the library's computer ready (Tailscale, Ollama, Windows' firewall and sleep),
/// and finding Granola on the laptop. Every install here is a fake: nothing is downloaded or installed.</summary>
public class ReadyTests
{
    const string Powercfg = """
        Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
          Subgroup GUID: 238c9fa8-0aad-41ed-83f4-97be242c8f20  (Sleep)
            Power Setting GUID: 29f6c1db-86da-48c5-9fdb-f2b67b1f44da  (Sleep after)
              Minimum Possible Setting: 0x00000000
              Maximum Possible Setting: 0xffffffff
              Possible Settings increment: 0x00000001
              Possible Settings units: Seconds
            Current AC Power Setting Index: 0x00000708
            Current DC Power Setting Index: 0x00000384

        """;

    static Fetch Saved(List<string>? fetched = null) => (url, dest, progress) =>
    {
        fetched?.Add(url);
        progress?.Invoke(5, 10);
        File.WriteAllText(dest, "installer");
        return Task.FromResult(dest);
    };

    static Fetch Nothing => (_, _, _) => throw new InvalidOperationException("no download here");

    [Fact]
    public void Windows_sleep_is_read_whatever_the_language()
    {
        Assert.Equal(30, Machine.WindowsSleepMinutes((_, _, _) => new ProcResult(0, Powercfg)));
        string german = Powercfg.Replace("Current AC Power Setting Index", "Index der aktuellen Wechselstromeinstellung");
        Assert.Equal(30, Machine.WindowsSleepMinutes((_, _, _) => new ProcResult(0, german)));
        Assert.Equal(0, Machine.WindowsSleepMinutes((_, _, _) => new ProcResult(0, Powercfg.Replace("0x00000708", "0x00000000"))));
        Assert.Null(Machine.WindowsSleepMinutes((_, _, _) => new ProcResult(0, "")));
    }

    [Fact]
    public void Keep_awake_only_on_windows_and_checks_it_took()
    {
        var run = new FakeRunner((_, _) => new ProcResult(0, Powercfg.Replace("0x00000708", "0x00000000")));
        Assert.True(Ready.KeepAwake("Windows", run.Run));
        Assert.Equal(["powercfg", "/change", "standby-timeout-ac", "0"], run.Calls[0]);
        Assert.False(Ready.KeepAwake("Windows", (_, _, _) => new ProcResult(0, Powercfg))); // still 30 min: it didn't take
        Assert.False(Ready.KeepAwake("Windows", (_, _, _) => null));
        Assert.False(Ready.KeepAwake("Darwin", run.Run));
        Assert.Equal(2, run.Calls.Count);
    }

    [Theory]
    [InlineData("off", 0, true)]
    [InlineData("none", 0, false)]
    [InlineData("8787", 0, true)]
    [InlineData("8000,8787", 0, true)]
    [InlineData("8000", 0, false)]
    [InlineData("", 1, null)]
    public void Firewall_status(string output, int code, bool? want) =>
        Assert.Equal(want, Machine.FirewallOpen(8787, (_, _, _) => new ProcResult(code, output)));

    [Fact]
    public async Task The_firewall_rule_matches_python_and_asks_windows_for_permission()
    {
        var f = Golden.Platform()["firewall"]!;
        var programs = f["programs"]!.AsArray().Select(p => p.S()).ToList();
        string script = Ready.FirewallScript(8790, programs);
        Assert.Equal(f["script"].S(), script);
        Assert.Contains("-LocalPort 8790", script);
        Assert.Contains("100.64.0.0/10,LocalSubnet", script);
        Assert.Contains("Action -eq 'Block'", script); // the block rules a dismissed "allow access?" prompt leaves
        var run = new FakeRunner((_, _) => new ProcResult(0, "8790"));
        Assert.True(await Ready.OpenFirewallAsync(8790, run.Run, programs));
        string elevate = run.Calls[0][^1];
        Assert.Equal(f["elevate"].S(), elevate);
        string encoded = elevate.Split("'-EncodedCommand','")[1].Split('\'')[0];
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
        Assert.False(await Ready.OpenFirewallAsync(8790, (_, _, _) => new ProcResult(1, ""), programs)); // "No" to Windows' prompt
        Assert.Equal(Autostart.EngineCommand()[0], Ready.Programs().Single()); // this engine's program is the one unblocked
    }

    [Fact]
    public async Task Installing_ollama_on_a_mac_unpacks_the_app_into_applications()
    {
        using var dir = new TempDir();
        var fetched = new List<string>();
        var run = new FakeRunner((exe, args) =>
        {
            Assert.Equal(["-x", "-k"], args.Take(2));
            Directory.CreateDirectory(Path.Combine(args[^1], "Ollama.app", "Contents"));
            return new ProcResult(0, "");
        });
        var bars = new List<(long, long)>();
        var said = new List<string>();
        Assert.True(await Ready.InstallOllamaAsync(said.Add, (d, t) => bars.Add((d, t)), "Darwin", run.Run, Saved(fetched), dir["Applications"], _ => true));
        Assert.Equal([Ready.OllamaMac], fetched);
        Assert.Equal([(5L, 10L)], bars);
        Assert.True(Directory.Exists(Path.Combine(dir["Applications"], "Ollama.app", "Contents")));
        Assert.Equal($"    Installed in {dir["Applications"]}.", said[^1]);
        // an older copy is replaced, not merged into
        Directory.CreateDirectory(Path.Combine(dir["Applications"], "Ollama.app", "stale"));
        Assert.True(await Ready.InstallOllamaAsync(said.Add, null, "Darwin", run.Run, Saved(), dir["Applications"], _ => true));
        Assert.False(Directory.Exists(Path.Combine(dir["Applications"], "Ollama.app", "stale")));
    }

    [Fact]
    public async Task A_mac_download_that_doesnt_unpack_says_where_to_get_ollama()
    {
        using var dir = new TempDir();
        var said = new List<string>();
        Assert.False(await Ready.InstallOllamaAsync(said.Add, null, "Darwin", (_, _, _) => new ProcResult(1, ""), Saved(), dir["Applications"], _ => true));
        Assert.Equal("    The download didn't unpack. Get Ollama from https://ollama.com/download instead.", said.Single());
        Assert.False(await Ready.InstallOllamaAsync(said.Add, null, "Darwin", (_, _, _) => new ProcResult(0, ""),
            (_, _, _) => throw new HttpRequestException("offline"), dir["Applications"], _ => true));
        Assert.Equal("    Couldn't install Ollama: offline", said[^1]);
    }

    [Fact]
    public async Task Installing_ollama_on_windows_runs_its_installer_quietly()
    {
        var run = new FakeRunner();
        Assert.True(await Ready.InstallOllamaAsync(_ => { }, null, "Windows", run.Run, Saved(), installed: _ => true));
        Assert.EndsWith("OllamaSetup.exe", run.Calls[0][0]);
        Assert.Equal(["/SILENT", "/NORESTART", "/SUPPRESSMSGBOXES"], run.Calls[0].Skip(1));
        var said = new List<string>();
        Assert.False(await Ready.InstallOllamaAsync(said.Add, null, "Windows", (_, _, _) => new ProcResult(2, ""), Saved(), installed: _ => false));
        Assert.Equal("    Ollama's installer stopped (code 2).", said[^1]);
    }

    [Fact]
    public async Task Installing_on_linux_runs_the_official_script_in_the_terminal()
    {
        var ran = new List<List<string>>();
        int? Attached(string exe, IReadOnlyList<string> args)
        {
            ran.Add([exe, .. args]);
            return 0;
        }
        Assert.True(await Ready.InstallOllamaAsync(_ => { }, null, "Linux", (_, _, _) => null, Nothing, installed: _ => true, attached: Attached));
        Assert.True(await Ready.InstallTailscaleAsync(_ => { }, null, "Linux", (_, _, _) => null, Nothing, exe: () => "/usr/bin/tailscale", attached: Attached));
        Assert.Equal([["sh", "-c", Ready.OllamaLinux], ["sh", "-c", Ready.TailscaleLinux]], ran);
    }

    [Fact]
    public async Task Tailscale_over_ssh_on_a_mac_says_what_to_do_instead()
    {
        var said = new List<string>();
        Assert.False(await Ready.InstallTailscaleAsync(said.Add, null, "Darwin", (_, _, _) => null, Nothing, remote: true));
        Assert.Contains(said, s => s.Contains("tailscale.com/download/mac"));
    }

    [Fact]
    public async Task Tailscale_opens_its_installer_and_waits()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir["Downloads"]);
        var started = new List<string>();
        var asked = new List<string>();
        Assert.True(await Ready.InstallTailscaleAsync(_ => { }, null, "Windows", (_, _, _) => null, Saved(), asked.Add, remote: false,
            start: started.Add, exe: () => @"C:\Program Files\Tailscale\tailscale.exe", downloads: dir["Downloads"]));
        Assert.Equal([Path.Combine(dir["Downloads"], "tailscale-setup.exe")], started);
        Assert.Contains("Enter", asked.Single());
        var run = new FakeRunner();
        Assert.False(await Ready.InstallTailscaleAsync(_ => { }, null, "Darwin", run.Run, Saved(), remote: false, exe: () => null, downloads: dir["Downloads"]));
        Assert.Equal(["open", Path.Combine(dir["Downloads"], "Tailscale.pkg")], run.Calls.Single());
    }

    [Fact]
    public void Opening_tailscale()
    {
        var run = new FakeRunner();
        Assert.True(Ready.OpenTailscale("Darwin", run.Run));
        Assert.Equal(["open", "-a", "Tailscale"], run.Calls.Single());
        Assert.False(Ready.OpenTailscale("Darwin", (_, _, _) => new ProcResult(1, "")));
        Assert.False(Ready.OpenTailscale("Linux", run.Run));
    }

    [Fact]
    public void Granola_is_found_where_its_installers_put_it()
    {
        using var dir = new TempDir();
        var at = new AppPlaces(dir["Applications"], dir["mine"], dir["Local"]);
        var spotlight = new FakeRunner((_, _) => new ProcResult(0, "/Volumes/Apps/Granola.app\n"));
        Assert.Equal("/Volumes/Apps/Granola.app", Ready.GranolaApp("Darwin", spotlight.Run, at));
        Assert.Contains("com.granola.app", spotlight.Calls.Single()[^1]);
        Directory.CreateDirectory(Path.Combine(dir["mine"], "Granola.app"));
        Assert.Equal(Path.Combine(dir["mine"], "Granola.app"), Ready.GranolaApp("Darwin", spotlight.Run, at));
        Assert.Null(Ready.GranolaApp("Darwin", (_, _, _) => null, new AppPlaces(dir["none"], dir["none"], dir["none"])));
        string exe = Path.Combine(dir["Local"], "Programs", "@granolaelectron", "Granola.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        Assert.Equal(exe, Ready.GranolaApp("Windows", null, at));
        Assert.Null(Ready.GranolaApp("Linux"));
        var linux = Ready.LaptopChecks("Linux", _ => throw new InvalidOperationException("Granola has no Linux app"), () => new TailscaleInfo(true, true));
        Assert.Equal((null, false, true), (linux.Granola, linux.GranolaHere, linux.Tailscale.Running));
        Assert.Equal("/G.app", Ready.LaptopChecks("Darwin", _ => "/G.app", () => new TailscaleInfo()).Granola);
    }

    [Fact]
    public async Task Downloads_report_their_progress()
    {
        using var dir = new TempDir();
        var data = new byte[3 << 20];
        new Random(1).NextBytes(data);
        var server = new FakeDownloads(new() { ["https://dl/x.zip"] = data });
        var seen = new List<(long, long)>();
        await Ready.DownloadAsync("https://dl/x.zip", dir["x.zip"], (d, t) => seen.Add((d, t)), server.Client());
        Assert.Equal(data, File.ReadAllBytes(dir["x.zip"]));
        Assert.Equal((data.Length, data.Length), seen[^1]);
        Assert.True(seen.Count >= 3);
        await Assert.ThrowsAsync<HttpRequestException>(() => Ready.DownloadAsync("https://dl/missing", dir["y"], null, server.Client()));
    }
}
