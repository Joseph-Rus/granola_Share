using System.Diagnostics;
using System.Text.RegularExpressions;

namespace StudyStash.Core.Tests;

/// <summary>tests/test_dialogs_autostart.py and the Windows half of test_ready.py: the background services, under the
/// Python engine's names and files, so either engine's replaces the other's.</summary>
public class AutostartTests
{
    static readonly string[] Engine = ["/opt/studystash"];

    static ServicePlaces Places(TempDir dir) => new(dir["agents"], dir["startup"], dir["systemd"]);

    [Fact]
    public void The_service_files_match_python()
    {
        var s = Golden.Platform()["services"]!;
        var args = s["args"]!.AsArray().Select(a => a.S()).ToList();
        var odd = s["odd"]!.AsArray().Select(a => a.S()).ToList();
        string home = s["user_home"].S(), log = s["log"].S();
        Assert.Equal(s["plist"].S(), Autostart.RenderPlist("com.granola-share.server", args, log, [], home));
        Assert.Equal(s["plist_odd"].S(), Autostart.RenderPlist("com.granola-share.client", odd, log, [], home));
        Assert.Equal(s["systemd"].S(), Autostart.RenderSystemd("granola-share server", args));
        Assert.Equal(s["systemd_odd"].S(), Autostart.RenderSystemd("granola-share client", odd));
    }

    [Fact]
    public void Services_run_this_engine_and_say_they_are_services()
    {
        Assert.Equal(["/opt/studystash", "--home", "/h", "client", "run"], Autostart.RoleArgs("client", "/h", Engine));
        Assert.Throws<ArgumentException>(() => Autostart.RoleArgs("laptop", "/h", Engine));
        var args = Autostart.RoleArgs("server", "/h", Engine);
        Assert.Contains("<key>GRANOLA_SHARE_SERVICE</key><string>1</string>", Autostart.RenderPlist("l", args, "/x"));
        Assert.Contains("Environment=GRANOLA_SHARE_SERVICE=1", Autostart.RenderSystemd("d", args));
        Assert.Equal("@echo off\r\nset GRANOLA_SHARE_SERVICE=1\r\n\"/opt/studystash\" \"--home\" \"/h\" \"run\"\r\n", Autostart.RenderCmd(args));
        // .NET's own folder, for a build that doesn't carry it
        var env = new List<(string, string)> { ("DOTNET_ROOT", "/opt/dotnet") };
        Assert.Contains("        <key>DOTNET_ROOT</key><string>/opt/dotnet</string>\n    </dict>", Autostart.RenderPlist("l", args, "/x", env));
        Assert.Contains("Environment=GRANOLA_SHARE_SERVICE=1\nEnvironment=DOTNET_ROOT=/opt/dotnet\nRestart=always", Autostart.RenderSystemd("d", args, env));
        Assert.Contains("set GRANOLA_SHARE_SERVICE=1\r\nset DOTNET_ROOT=/opt/dotnet\r\n", Autostart.RenderCmd(args, env));
        // cmd reads a batch file in the console's code page: a home folder named José needs UTF-8 first
        Assert.StartsWith("@echo off\r\nchcp 65001 >nul\r\nset GRANOLA_SHARE_SERVICE=1\r\n", Autostart.RenderCmd(Autostart.RoleArgs("server", @"C:\Users\José", Engine)));
    }

    [Fact]
    public void Install_and_uninstall_on_a_mac()
    {
        using var dir = new TempDir();
        var run = new FakeRunner();
        string p = Autostart.Install("server", dir["home"], Places(dir), run.Run, Engine, "Darwin", []);
        Assert.Equal(Path.Combine(dir["agents"], "com.granola-share.server.plist"), p);
        Assert.Equal(Autostart.RenderPlist("com.granola-share.server", Autostart.RoleArgs("server", dir["home"], Engine),
            Path.Combine(dir["home"], "logs", "server.log"), []), File.ReadAllText(p).ReplaceLineEndings("\n"));
        Assert.Equal(["launchctl", "bootstrap", $"gui/{Machine.Uid()}", p], run.Calls[^1]);
        Assert.Equal("bootout", run.Calls[^2][1]); // the old one goes first, whichever engine it ran
        Assert.True(Directory.Exists(Path.Combine(dir["home"], "logs")));
        Assert.True(Autostart.Uninstall("server", Places(dir), run.Run, "Darwin"));
        Assert.False(File.Exists(p));
        Assert.False(Autostart.Uninstall("server", Places(dir), run.Run, "Darwin"));
    }

    [Fact]
    public void Install_on_windows_and_linux()
    {
        using var dir = new TempDir();
        var run = new FakeRunner();
        string cmd = Autostart.Install("client", dir["home"], Places(dir), run.Run, Engine, "Windows", []);
        Assert.Equal(Path.Combine(dir["startup"], "granola-share-client.cmd"), cmd);
        Assert.Contains("\"client\" \"run\"\r\n", File.ReadAllText(cmd));
        // an older copy stops first (Python's or this engine's), then the new one starts right away
        Assert.Equal("powershell", run.Calls[0][0]);
        Assert.Contains("Stop-Process", run.Calls[0][^1]);
        Assert.Equal(["cmd", "/c", cmd], run.Calls[1]);
        Assert.True(Autostart.Uninstall("client", Places(dir), run.Run, "Windows"));
        Assert.False(File.Exists(cmd));

        run.Calls.Clear();
        string unit = Autostart.Install("client", dir["home"], Places(dir), run.Run, Engine, "Linux", []);
        Assert.EndsWith("granola-share-client.service", unit);
        Assert.Contains("Restart=always", File.ReadAllText(unit));
        Assert.Equal([["systemctl", "--user", "daemon-reload"], ["systemctl", "--user", "enable", "granola-share-client.service"],
            ["systemctl", "--user", "restart", "granola-share-client.service"]], run.Calls);
        Assert.True(Autostart.Uninstall("client", Places(dir), run.Run, "Linux"));
        Assert.Equal(["systemctl", "--user", "disable", "--now", "granola-share-client.service"], run.Calls[^1]);
    }

    [Fact]
    public void Status_and_restart()
    {
        using var dir = new TempDir();
        var places = Places(dir);
        ProcResult Said(string text) => new(0, text);
        Assert.Equal("missing", Autostart.Status("client", places, (_, _, _) => Said("state = running"), "Darwin"));
        Directory.CreateDirectory(dir["agents"]);
        File.WriteAllText(Path.Combine(dir["agents"], "com.granola-share.client.plist"), "");
        Assert.Equal("running", Autostart.Status("client", places, (_, _, _) => Said("state = running"), "Darwin"));
        Assert.Equal("stopped", Autostart.Status("client", places, (_, _, _) => Said("state = waiting"), "Darwin"));
        Assert.Equal("stopped", Autostart.Status("client", places, (_, _, _) => null, "Darwin"));
        Assert.Equal(["client"], Autostart.InstalledRoles(places, "Darwin"));
        var run = new FakeRunner();
        Autostart.Restart("client", places, run.Run, "Darwin");
        Assert.Equal(["launchctl", "kickstart", "-k", $"gui/{Machine.Uid()}/com.granola-share.client"], run.Calls.Single());
        var notLoaded = new FakeRunner((exe, args) => new ProcResult(args[0] == "kickstart" ? 113 : 0, ""));
        Autostart.Restart("client", places, notLoaded.Run, "Darwin");
        Assert.Equal("bootstrap", notLoaded.Calls[^1][1]);

        Directory.CreateDirectory(dir["systemd"]);
        File.WriteAllText(Path.Combine(dir["systemd"], "granola-share-server.service"), "");
        Assert.Equal("running", Autostart.Status("server", places, (_, _, _) => Said("active\n"), "Linux"));
        Assert.Equal("stopped", Autostart.Status("server", places, (_, _, _) => Said("inactive\n"), "Linux"));
        run.Calls.Clear();
        Autostart.Restart("server", places, run.Run, "Linux");
        Assert.Equal(["systemctl", "--user", "restart", "granola-share-server.service"], run.Calls.Single());
        Autostart.Restart("client", places, run.Run, "Linux"); // not installed there: nothing to do
        Assert.Single(run.Calls);

        Directory.CreateDirectory(dir["startup"]);
        File.WriteAllText(Path.Combine(dir["startup"], "granola-share-server.cmd"), "");
        Assert.Equal("running", Autostart.Status("server", places, (_, _, _) => Said("2\r\n"), "Windows"));
        Assert.Equal("stopped", Autostart.Status("server", places, (_, _, _) => Said("0\r\n"), "Windows"));
        Assert.Equal("stopped", Autostart.Status("server", places, (_, _, _) => Said("oops"), "Windows"));
    }

    /// <summary>PowerShell's -match is .NET's regex, case-insensitive: this is the test Windows itself runs.</summary>
    static string Service(string commandLine)
    {
        bool client = Regex.IsMatch(commandLine, Autostart.ClientRun, RegexOptions.IgnoreCase);
        bool server = Regex.IsMatch(commandLine, Autostart.ServerRun, RegexOptions.IgnoreCase) && !client;
        return client ? "client" : server ? "server" : "";
    }

    [Fact]
    public void Windows_tells_either_engines_services_apart_from_commands()
    {
        // `autostart install` stops the service it replaces, and `status` counts services: only a command line ending
        // in the service's own command counts, so neither ever stops or counts itself.
        const string py = "\"C:\\Users\\x\\AppData\\Local\\Programs\\granola-share\\python\\pythonw.exe\"";
        Assert.Equal("server", Service($"{py} \"-u\" \"-m\" \"granola_share.cli\" \"--home\" \"C:\\Users\\x\\.granola-share\" \"run\""));
        Assert.Equal("client", Service($"{py} -u -m granola_share.cli --home C:\\x client run"));
        Assert.Equal("client", Service($"{py} \"-u\" \"-m\" \"granola_share.cli\" \"--home\" \"C:\\x\" \"client\" \"run\""));
        const string cs = "\"C:\\Users\\x\\AppData\\Local\\Programs\\Study Stash Engine\\studystash.exe\"";
        Assert.Equal("server", Service($"{cs} \"--home\" \"C:\\Users\\x\\.granola-share\" \"run\""));
        Assert.Equal("server", Service("C:\\x\\StudyStash.exe --home C:\\x run"));
        Assert.Equal("client", Service($"{cs} \"--home\" \"C:\\x\" \"client\" \"run\""));
        // a build run as `dotnet studystash.dll`
        Assert.Equal("server", Service("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\src\\bin\\studystash.dll\" --home C:\\x run"));
        foreach (string command in new[] { "autostart install --role server", "autostart status --role client", "doctor --role server",
                     "client open --install --no-browser", "update" })
        {
            Assert.Equal("", Service($"{py} -m granola_share.cli --home C:\\x {command}"));
            Assert.Equal("", Service($"{cs} --home C:\\x {command}"));
        }
        Assert.Equal("", Service("\"C:\\Program Files\\Something\\run.exe\" run")); // not ours
        Assert.Contains(Autostart.ClientRun, Autostart.PsFilter("client"));
        Assert.Contains($"-notmatch '{Autostart.ClientRun}'", Autostart.PsFilter("server"));
        Assert.Contains(Autostart.PsFilter("client"), Autostart.PsFilter(null));
    }

    [Fact]
    public void Keep_alive_restarts_the_service_and_keeps_its_log()
    {
        using var dir = new TempDir();
        var runs = new List<(List<string> Args, string? Child)>();
        var sleeps = new List<int>();
        Autostart.KeepAlive(dir.Path, "server", ["--home", dir.Path, "run"], (args, env, output) =>
        {
            runs.Add((args.ToList(), env.GetValueOrDefault(Autostart.ChildEnv)));
            output("studystash: pool 'P' on port 8787");
            return 1;
        }, sleeps.Add, rounds: 2, engine: Engine);
        Assert.Equal(2, runs.Count);
        Assert.Equal(["/opt/studystash", "--home", dir.Path, "run"], runs[0].Args);
        Assert.Equal("1", runs[0].Child);
        Assert.Equal([60, 60], sleeps); // it failed straight away both times: wait longer before trying again
        string log = File.ReadAllText(Path.Combine(dir.Path, "logs", "server.log"));
        Assert.Equal(2, Regex.Count(log, "pool 'P'"));
        Assert.Contains("[service] stopped (exit 1); starting it again in 60 s\n", log);
    }

    [Fact]
    public void Keep_alive_starts_the_log_over_past_its_limit()
    {
        using var dir = new TempDir();
        string log = Path.Combine(dir.Path, "logs", "server.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllBytes(log, new byte[Autostart.LogLimit + 1]);
        Autostart.KeepAlive(dir.Path, "server", ["run"], (_, _, output) => { output("fresh"); return 0; }, _ => { }, rounds: 1, engine: Engine);
        Assert.Equal(Autostart.LogLimit + 1, new FileInfo(log + ".1").Length);
        Assert.StartsWith("fresh\n", File.ReadAllText(log));
    }

    /// <summary>The engine this solution built, to run as a real service.</summary>
    internal static string BuiltEngine()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory); // tests/StudyStash.Core.Tests/bin/<config>/net10.0/
        string config = bin.Parent!.Name, framework = bin.Name;
        string engineDir = Path.GetFullPath(Path.Combine(bin.FullName, "..", "..", "..", "..", "..", "src", "StudyStash.Engine", "bin", config, framework));
        return Path.Combine(engineDir, OperatingSystem.IsWindows() ? "studystash.exe" : "studystash");
    }

    /// <summary>
    /// Only when asked (STUDYSTASH_LIVE_SERVICE=1), on a Mac or Windows: installs this engine as the library's real
    /// background service, from a scratch folder on a spare port; checks it answers, restarts, and goes away again.
    /// It refuses to run where a library service is already installed or running: its label is the real one.
    /// </summary>
    [Fact]
    public async Task Live_the_library_runs_as_a_background_service()
    {
        if (Environment.GetEnvironmentVariable("STUDYSTASH_LIVE_SERVICE") != "1" || !(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())) return;
        Assert.Equal("missing", Autostart.Status("server", ServicePlaces.Default, Machine.Run));
        if (OperatingSystem.IsMacOS())
            Assert.False(Machine.Run("launchctl", ["print", $"gui/{Machine.Uid()}/{Autostart.Label("server")}"], TimeSpan.FromSeconds(10)) is { ExitCode: 0 },
                "a library service is loaded on this computer: not touching it");
        else
        {
            var count = Machine.Run("powershell", ["-NoProfile", "-Command",
                $"@(Get-CimInstance Win32_Process | Where-Object {{ {Autostart.PsFilter("server")} }}).Count"], TimeSpan.FromSeconds(60));
            Assert.True(count is not null && Py.Strip(count.Stdout) == "0", "a library is running on this computer: not touching it");
        }
        string exe = BuiltEngine();
        Assert.True(File.Exists(exe), $"build the solution first: no {exe}");
        using var dir = new TempDir();
        var cfg = new Config(dir["home"], dir["pool"]) { PoolPassword = "live-test", WebPort = AppPage_FreePort(), OllamaEnabled = false, AutoUpdate = false };
        Configs.Save(cfg);
        var places = Places(dir);
        var started = Stopwatch.StartNew();
        string path = Autostart.Install("server", cfg.Home, places, Machine.Run, [exe]);
        try
        {
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(45), $"installing waited {started.Elapsed} for the service"); // no pipe held open
            Assert.True(await HostInfo.WaitForServerAsync(cfg, TimeSpan.FromSeconds(60)), "the service never answered: " + ReadLog(cfg));
            Assert.Equal("running", Autostart.Status("server", places, Machine.Run));
            Autostart.Restart("server", places, Machine.Run);
            Assert.True(await HostInfo.WaitForServerAsync(cfg, TimeSpan.FromSeconds(60)), "it didn't come back after a restart: " + ReadLog(cfg));
        }
        finally
        {
            Assert.True(Autostart.Uninstall("server", places, Machine.Run));
        }
        Assert.False(File.Exists(path));
        for (int i = 0; i < 50 && await HostInfo.PortStatusAsync(cfg.WebPort) != "free"; i++) await Task.Delay(200);
        Assert.Equal("free", await HostInfo.PortStatusAsync(cfg.WebPort));
        Assert.Equal("missing", Autostart.Status("server", places, Machine.Run));
    }

    static int AppPage_FreePort() => StudyStash.Library.AppPage.FreePort(18700, 200);

    static string ReadLog(Config cfg)
    {
        try
        {
            return File.ReadAllText(Path.Combine(cfg.LogDir, "server.log"));
        }
        catch (IOException)
        {
            return "(no log)";
        }
    }
}
