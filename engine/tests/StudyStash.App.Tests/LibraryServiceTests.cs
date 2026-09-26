using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using StudyStash.App.Services;
using StudyStash.Core;
using StudyStash.Library;

namespace StudyStash.App.Tests;

/// <summary>LibraryService runs a real, disposable copy of the built engine: no LaunchAgent, no Startup entry, just a
/// child process on a spare port in a temp home, gone again when the test is.</summary>
public sealed class LibraryServiceTests
{
    static string BuiltEngine()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory); // tests/StudyStash.App.Tests/bin/<config>/net10.0/
        string config = bin.Parent!.Name, framework = bin.Name;
        string engineDir = Path.GetFullPath(Path.Combine(bin.FullName, "..", "..", "..", "..", "..", "src", "StudyStash.Engine", "bin", config, framework));
        return Path.Combine(engineDir, OperatingSystem.IsWindows() ? "studystash.exe" : "studystash");
    }

    static string TempHome()
    {
        string dir = Path.Combine(Path.GetTempPath(), "studystash-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A config.toml written and ready to serve, on a spare loopback port nothing else is using.</summary>
    static Config ReadyConfig(string home)
    {
        var cfg = Configs.Load(home);
        cfg.PoolDir = Path.Combine(home, "pool");
        cfg.PoolPassword = "library-service-test";
        // 0.0.0.0 (the default): a wildcard bind is what PortStatusAsync's own free-port probe actually conflicts with.
        cfg.WebPort = AppPage.FreePort(19200, 400);
        cfg.OllamaEnabled = false;
        cfg.AutoUpdate = false;
        Configs.Save(cfg);
        return cfg;
    }

    static async Task<LibraryServiceState> Until(LibraryService svc, Func<LibraryServiceState, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (done(svc.State)) break;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        return svc.State;
    }

    [Fact]
    public async Task Starts_on_a_free_port_and_answers_with_the_password()
    {
        string exe = BuiltEngine();
        Assert.True(File.Exists(exe), $"build the solution first: no {exe}");
        string home = TempHome();
        try
        {
            var cfg = ReadyConfig(home);
            var svc = new LibraryService(home, cfg, [exe]);
            await svc.StartAsync();
            Assert.Equal(LibraryServiceState.Running, svc.State);

            using var http = new HttpClient();
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{cfg.WebPort}/api/health");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.PoolPassword);
            using var resp = await http.SendAsync(req, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            await svc.StopAsync();
        }
        finally { try { Directory.Delete(home, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Comes_back_on_its_own_when_the_child_is_killed()
    {
        string exe = BuiltEngine();
        Assert.True(File.Exists(exe), $"build the solution first: no {exe}");
        string home = TempHome();
        try
        {
            var cfg = ReadyConfig(home);
            var svc = new LibraryService(home, cfg, [exe]);
            await svc.StartAsync();
            Assert.Equal(LibraryServiceState.Running, svc.State);
            int pid = svc.Pid ?? throw new InvalidOperationException("no pid");
            Process.GetProcessById(pid).Kill(entireProcessTree: true);

            var state = await Until(svc, s => s == LibraryServiceState.Running, TimeSpan.FromSeconds(10));
            Assert.Equal(LibraryServiceState.Running, state);

            await svc.StopAsync();
        }
        finally { try { Directory.Delete(home, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Stop_frees_the_port_and_removes_the_pid_file()
    {
        string exe = BuiltEngine();
        Assert.True(File.Exists(exe), $"build the solution first: no {exe}");
        string home = TempHome();
        try
        {
            var cfg = ReadyConfig(home);
            var svc = new LibraryService(home, cfg, [exe]);
            await svc.StartAsync();
            Assert.Equal(LibraryServiceState.Running, svc.State);

            await svc.StopAsync();
            Assert.Equal(LibraryServiceState.Stopped, svc.State);
            Assert.False(File.Exists(svc.PidPath));

            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, cfg.WebPort)); // throws if still taken
        }
        finally { try { Directory.Delete(home, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task A_second_service_on_the_same_home_steps_aside()
    {
        string exe = BuiltEngine();
        Assert.True(File.Exists(exe), $"build the solution first: no {exe}");
        string home = TempHome();
        try
        {
            var cfg = ReadyConfig(home);
            var first = new LibraryService(home, cfg, [exe]);
            await first.StartAsync();
            Assert.Equal(LibraryServiceState.Running, first.State);

            var second = new LibraryService(home, cfg, [exe]);
            await second.StartAsync();
            Assert.Equal(LibraryServiceState.Elsewhere, second.State);

            await first.StopAsync();
        }
        finally { try { Directory.Delete(home, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task No_config_fails_with_not_set_up_yet()
    {
        string home = TempHome();
        try
        {
            var cfg = new Config(home, Path.Combine(home, "pool")) { WebPort = AppPage.FreePort(19600, 400) };
            var svc = new LibraryService(home, cfg, [BuiltEngine()]);
            await svc.StartAsync();
            Assert.Equal(LibraryServiceState.Failed, svc.State);
            Assert.Equal("Not set up yet", svc.Failure);
        }
        finally { try { Directory.Delete(home, true); } catch (IOException) { } }
    }
}
