using StudyStash.App.Services;
using StudyStash.Core;

namespace StudyStash.App.Tests;

/// <summary>AppUpdates.RoundAsync, tried through its injected pieces: no network, no process, no real clock.</summary>
public class AppUpdatesTests
{
    static Release Release(string version = "9.9.9") => new(
        $"v{version}", Updates.ParseVersion(version), $"https://example.com/{version}", "https://example.com");

    static AppUpdates Updates_(
        bool enabled = true,
        bool idle = true,
        string onDisk = "0.4.4",
        Func<Task<Release?>>? latest = null,
        Updates.ApplyFn? apply = null,
        Action? relaunchAndQuit = null,
        List<string>? log = null)
    {
        log ??= [];
        return new AppUpdates
        {
            Latest = latest ?? (() => Task.FromResult<Release?>(null)),
            Idle = () => idle,
            Enabled = () => enabled,
            OnDiskVersion = () => onDisk,
            Apply = apply ?? ((_, _, _, _) => Task.FromResult(true)),
            RelaunchAndQuit = relaunchAndQuit ?? (() => { }),
            Log = s => log.Add(s),
            Home = "unused",
        };
    }

    [Fact]
    public async Task Disabled_never_checks_for_a_release()
    {
        bool asked = false;
        var updates = Updates_(enabled: false, latest: () => { asked = true; return Task.FromResult<Release?>(null); });

        Assert.Equal("off", await updates.RoundAsync());
        Assert.False(asked);
    }

    [Fact]
    public async Task Nothing_newer_leaves_it_alone()
    {
        var updates = Updates_(onDisk: "0.4.4", latest: () => Task.FromResult<Release?>(null));

        Assert.Equal("up to date", await updates.RoundAsync());
    }

    [Fact]
    public async Task A_newer_release_waits_for_the_student_to_be_idle_then_installs()
    {
        bool applied = false;
        bool quit = false;
        var updates = Updates_(
            idle: false,
            latest: () => Task.FromResult<Release?>(Release()),
            apply: (_, _, _, _) => { applied = true; return Task.FromResult(true); },
            relaunchAndQuit: () => quit = true);

        Assert.Equal("waiting until you're done recording", await updates.RoundAsync());
        Assert.False(applied);
        Assert.False(quit);
    }

    [Fact]
    public async Task Once_idle_the_release_installs_and_the_app_relaunches()
    {
        bool quit = false;
        var updates = Updates_(
            idle: true,
            latest: () => Task.FromResult<Release?>(Release()),
            apply: (_, _, _, _) => Task.FromResult(true),
            relaunchAndQuit: () => quit = true);

        Assert.Equal("relaunching", await updates.RoundAsync());
        Assert.True(quit);
    }

    [Fact]
    public async Task A_failed_install_says_so_and_does_not_quit()
    {
        bool quit = false;
        var updates = Updates_(
            idle: true,
            latest: () => Task.FromResult<Release?>(Release()),
            apply: (_, _, _, _) => Task.FromResult(false),
            relaunchAndQuit: () => quit = true);

        Assert.Equal("install failed", await updates.RoundAsync());
        Assert.False(quit);
    }

    [Fact]
    public async Task A_newer_copy_already_on_disk_relaunches_without_checking_for_a_release()
    {
        bool asked = false;
        bool quit = false;
        var updates = Updates_(
            idle: true,
            onDisk: "9.9.9",
            latest: () => { asked = true; return Task.FromResult<Release?>(null); },
            relaunchAndQuit: () => quit = true);

        Assert.Equal("relaunching", await updates.RoundAsync());
        Assert.False(asked);
        Assert.True(quit);
    }

    [Fact]
    public async Task A_newer_copy_on_disk_waits_for_idle_too()
    {
        bool quit = false;
        var updates = Updates_(idle: false, onDisk: "9.9.9", relaunchAndQuit: () => quit = true);

        Assert.Equal("waiting until you're done recording", await updates.RoundAsync());
        Assert.False(quit);
    }

    [Fact]
    public void Enabled_reads_auto_update_from_client_toml()
    {
        using var home = new TempHome();
        Configs.SaveClient(new ClientConfig(home.Path) { AutoUpdate = false });

        Assert.False(Configs.LoadClient(home.Path).AutoUpdate);
    }
}
