using System.Net;
using System.Net.Sockets;
using StudyStash.App.Services;
using StudyStash.Core;

namespace StudyStash.App.Tests;

/// <summary>Making this computer the library, end to end: a real child on a free port, client.toml pointed at it, and
/// nothing installed.</summary>
public sealed class LibraryHereTests
{
    static string BuiltEngine()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory);
        string config = bin.Parent!.Name, framework = bin.Name;
        string engineDir = Path.GetFullPath(Path.Combine(bin.FullName, "..", "..", "..", "..", "..", "src", "StudyStash.Engine", "bin", config, framework));
        return Path.Combine(engineDir, OperatingSystem.IsWindows() ? "studystash.exe" : "studystash");
    }

    static AppHost Host(string home) => new(home, log: _ => { }, loginItems: new CountingLoginItems());

    [Fact]
    public async Task Creating_a_library_here_starts_it_and_points_the_client_at_it()
    {
        string exe = BuiltEngine();
        Assert.True(File.Exists(exe), $"build the solution first: no {exe}");
        using var home = new TempHome();
        using var host = Host(home.Path);
        var here = new LibraryHere { Command = [exe] };
        try
        {
            string said = await here.CreateAsync(host, "Ada's library", "at-least-4", "Ada");
            Assert.Contains("ready", said, StringComparison.Ordinal);
            Assert.NotNull(host.LocalLibrary);
            Assert.Equal(LibraryServiceState.Running, host.LocalLibrary!.State);

            var cfg = Configs.Load(home.Path);
            var cc = Configs.LoadClient(home.Path);
            Assert.Equal($"http://127.0.0.1:{cfg.WebPort}", cc.ServerUrl);
            Assert.Equal(cfg.PoolPassword, cc.PoolKey);
            Assert.Equal(AppRole.Both, AppSettings.Load(home.Path).Role);
        }
        finally { if (host.LocalLibrary is { } svc) await svc.StopAsync(); }
    }

    [Fact]
    public async Task A_busy_8787_gets_the_next_free_pair()
    {
        string exe = BuiltEngine();
        Assert.True(File.Exists(exe), $"build the solution first: no {exe}");
        using var home = new TempHome();
        using var host = Host(home.Path);
        var here = new LibraryHere { Command = [exe] };
        using var squatter = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        squatter.Bind(new IPEndPoint(IPAddress.Loopback, 8787));
        squatter.Listen();
        try
        {
            await here.CreateAsync(host, "Ada's library", "at-least-4", "Ada");
            var cfg = Configs.Load(home.Path);
            Assert.NotEqual(8787, cfg.WebPort);
            var cc = Configs.LoadClient(home.Path);
            Assert.Equal($"http://127.0.0.1:{cfg.WebPort}", cc.ServerUrl);
        }
        finally { if (host.LocalLibrary is { } svc) await svc.StopAsync(); }
    }

    [Fact]
    public async Task Never_installs_a_launch_agent_or_startup_entry()
    {
        string exe = BuiltEngine();
        Assert.True(File.Exists(exe), $"build the solution first: no {exe}");
        using var home = new TempHome();
        using var host = Host(home.Path);
        var here = new LibraryHere { Command = [exe] };
        try
        {
            await here.CreateAsync(host, "Ada's library", "at-least-4", "Ada");
            var places = ServicePlaces.Default;
            Assert.False(File.Exists(Autostart.ServicePath("server", places)));
            Assert.DoesNotContain("server", Autostart.InstalledRoles(places));
        }
        finally { if (host.LocalLibrary is { } svc) await svc.StopAsync(); }
    }

    [Fact]
    public async Task A_short_password_is_refused_before_anything_starts()
    {
        using var home = new TempHome();
        using var host = Host(home.Path);
        var here = new LibraryHere();
        await Assert.ThrowsAsync<ArgumentException>(() => here.CreateAsync(host, "Ada's library", "abc", "Ada"));
        Assert.Null(host.LocalLibrary);
    }
}
