using System.Globalization;
using Avalonia.Threading;
using StudyStash.App.Platform;
using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>
/// The running app checking for a new release on its own (D4): 10 minutes after it starts, then every 6 hours,
/// installing it once nothing is recording, paused or transcribing, then quitting so the new copy starts in its
/// place. It also notices when a newer copy has already been swapped onto disk by someone else - the CLI's
/// `studystash update`, or the library page's "Update now" - and relaunches into it once idle, with no download of
/// its own. Every dependency is injectable, so <see cref="RoundAsync"/> is tried with fakes: no network, no process,
/// no real clock.
/// </summary>
public sealed class AppUpdates
{
    /// <summary>How often the idle-wait loop looks again while an update is ready but the student is mid-lecture,
    /// instead of waiting the full six hours for the next round.</summary>
    public static readonly TimeSpan IdleRecheck = TimeSpan.FromMinutes(1);

    public required Func<Task<Release?>> Latest { get; init; }
    /// <summary>Nothing recording, paused or transcribing right now.</summary>
    public required Func<bool> Idle { get; init; }
    /// <summary>An installed copy, `auto_update` on, and not the self-test.</summary>
    public required Func<bool> Enabled { get; init; }
    /// <summary>The version actually on disk right now: someone else (the CLI, the library page) may have installed
    /// a newer one than the code this process is running.</summary>
    public required Func<string> OnDiskVersion { get; init; }
    public required Updates.ApplyFn Apply { get; init; }
    /// <summary>Get the app running again on the version now on disk, then quit this process. Called instead of a
    /// plain quit because nothing else is watching to reopen the app: unlike a service under launchd or Setup.exe's
    /// own `/relaunch`, an ordinary quit here would just leave the student without a menu bar icon.</summary>
    public required Action RelaunchAndQuit { get; init; }
    public required Action<string> Log { get; init; }
    /// <summary>Where lectures and settings live, for <see cref="Apply"/>.</summary>
    public required string Home { get; init; }
    /// <summary>How a round waits between checks; a test finishes at once instead of really sleeping.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// One check, never throwing: "off" (disabled), "up to date", "waiting until you're done recording" (an update
    /// is ready but the student isn't idle), "install failed", or "relaunching" (a fresh install, or a newer copy
    /// already on disk, just needed the student to be idle). A failed network check is logged and counted as "up to
    /// date", so the next round simply tries again.
    /// </summary>
    public async Task<string> RoundAsync()
    {
        if (!Enabled()) return "off";

        int[] running = Updates.ParseVersion(Engine.Version);
        if (Updates.Compare(Updates.ParseVersion(OnDiskVersion()), running) > 0)
        {
            if (!Idle()) return "waiting until you're done recording";
            Log("[update] a newer copy is already installed here; relaunching into it");
            RelaunchAndQuit();
            return "relaunching";
        }

        Release? release;
        try
        {
            release = await Latest();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log($"[update] check failed: {e.Message}");
            return "up to date";
        }
        if (!Updates.IsNewer(release)) return "up to date";
        if (!Idle()) return "waiting until you're done recording";

        Log($"[update] installing {release!.Tag}...");
        bool ok = await Apply(release, Home, s => Log($"[update] {s}"), restartServices: false);
        if (!ok)
        {
            Log("[update] install failed");
            return "install failed";
        }
        RelaunchAndQuit();
        return "relaunching";
    }

    /// <summary>Rounds forever: the first one 10 minutes after the app starts, then every 6 hours; while a round
    /// keeps saying to wait for the student, the next one comes a minute later instead.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        try
        {
            await Delay(TimeSpan.FromSeconds(Updates.FirstCheckAfter), stop);
            while (!stop.IsCancellationRequested)
            {
                string result;
                try
                {
                    result = await RoundAsync();
                }
                catch (Exception e)
                {
                    Log($"[update] {e.Message}");
                    result = "up to date";
                }
                await Delay(result == "waiting until you're done recording" ? IdleRecheck : TimeSpan.FromSeconds(Updates.CheckEvery), stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A program that outlives this one, waiting for it to quit before opening the app again: the same
    /// idea as the Mac swap's own waiter (Updater.cs), needed here too because nothing else relaunches an already
    /// on-disk copy, and because relaunching after a fresh install this way (once, from here) avoids racing a
    /// second waiter spawned by <see cref="Updates.ApplyAsync"/> if that were also asked to relaunch.</summary>
    static void SpawnWaiter(UpdateHost host, IReadOnlyList<string> args)
    {
        string pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        if (host.System == "Darwin")
        {
            const string script = "app=\"$1\"; shift; while kill -0 \"$0\" 2>/dev/null; do sleep 1; done; exec /usr/bin/open \"$app\" --args \"$@\"";
            host.SpawnDetached(["/bin/sh", "-c", script, pid, host.AppDir, .. args]);
        }
        else if (host.System == "Windows")
        {
            string exe = Path.Combine(host.AppDir, "StudyStash.exe");
            string command = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Process -FilePath '{exe}' -ArgumentList '{string.Join(' ', args)}'";
            host.SpawnDetached(["powershell.exe", "-NoProfile", "-WindowStyle", "Hidden", "-Command", command]);
        }
    }

    /// <summary>Wires the real pieces to an app that's actually running, and starts checking in the background.
    /// Disabled for a build folder, the self-test, or `auto_update = false`; enabled is read fresh every round, so
    /// turning it off in Settings (or client.toml) takes effect on the next check.</summary>
    public static void Start(AppHost host, CancellationToken stop)
    {
        var updateHost = UpdateHost.ThisComputer();
        List<string> relaunchArgs = ["--background"];
        if (!string.Equals(host.Home, Configs.DefaultHome, StringComparison.Ordinal)) relaunchArgs.AddRange(["--home", host.Home]);
        var updates = new AppUpdates
        {
            Latest = () => Updates.CachedLatestAsync(),
            Idle = () => !host.Lectures.All().Any(l => l.State is LectureState.Recording or LectureState.Paused or LectureState.Transcribing),
            Enabled = () => updateHost.AppDir.Length > 0 && !Desktop.SystemChangesOff && Configs.LoadClient(host.Home).AutoUpdate,
            OnDiskVersion = () => Updates.InstalledVersion(updateHost.AppDir, updateHost.System),
            Apply = (release, home, log, restart) => Updates.ApplyAsync(release, home, updateHost, log, restart),
            RelaunchAndQuit = () =>
            {
                SpawnWaiter(updateHost, relaunchArgs);
                Dispatcher.UIThread.Post(() => Shell.Quit(0));
            },
            Log = Program.Log,
            Home = host.Home,
        };
        _ = updates.RunAsync(stop);
    }
}
