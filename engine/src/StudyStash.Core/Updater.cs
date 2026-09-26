using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StudyStash.Core;

/// <summary>
/// What installing an update touches, so tests can point it somewhere else. What only looks (the release, the
/// checksums) looks at this computer; what changes it (this copy's files, commands, a relaunch) is off unless
/// <see cref="ThisComputer"/> turns it on.
/// </summary>
public sealed class UpdateHost
{
    static Exception Off(string what) => new InvalidOperationException($"{what} is off for this update.");

    public string System { get; init; } = Machine.Platform;
    /// <summary>This copy's own folder: the Mac bundle (Study Stash.app) or the Windows install folder. Empty when
    /// this copy doesn't update itself - see <see cref="Updates.WhyNotUpdatable"/> and <see cref="NotInstalledReason"/>.</summary>
    public string AppDir { get; init; } = "";
    /// <summary>Why AppDir is empty, for the student. Ignored once AppDir is set.</summary>
    public string NotInstalledReason { get; init; } = "This copy runs from a build folder, so it doesn't update itself.";
    public HttpClient? Http { get; init; }
    public Runner Run { get; init; } = (_, _, _) => throw Off("Running commands");
    public ServicePlaces Places { get; init; } = ServicePlaces.Default;
    /// <summary>What running this exact program with `version` prints, or null when it doesn't run here.</summary>
    public Func<string, string?> VersionOf { get; init; } = _ => throw Off("Running the new app");
    /// <summary>Start a program detached from this one, so it outlives it: Windows' Setup.exe, or the Mac relaunch
    /// waiter below.</summary>
    public Action<IReadOnlyList<string>> SpawnDetached { get; init; } = _ => throw Off("Starting the installer");
    /// <summary>A pid to wait for before reopening the app after a Mac swap; 0 means don't.</summary>
    public int RelaunchPid { get; init; }
    public IReadOnlyList<string> RelaunchArgs { get; init; } = [];
    /// <summary>Which installer an update fetches: this copy's own role preset, until Settings knows better (D3).</summary>
    public string? Role { get; init; } = Apps.RolePreset();
    /// <summary>Where a download stages: a real temp folder unless a test points it elsewhere.</summary>
    public string TempDir { get; init; } = Path.GetTempPath();

    /// <summary>This computer, for real: wherever this copy runs from, a Mac bundle or a Windows install (or neither).</summary>
    public static UpdateHost ThisComputer(string? baseDir = null, string? system = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        system ??= Machine.Platform;
        string appDir = "";
        string reason = "This copy runs from a build folder, so it doesn't update itself.";
        if (system == "Darwin")
        {
            appDir = Apps.MacBundleOf(baseDir, out string problem) ?? "";
            reason = problem switch
            {
                "translocated" => "Move Study Stash into Applications, then open it again, so it can update itself.",
                "unwritable" => "Study Stash's folder isn't writable, so it can't update itself here.",
                _ => reason,
            };
        }
        else if (system == "Windows")
        {
            appDir = Apps.WindowsInstallOf(baseDir) ?? "";
        }
        return new UpdateHost
        {
            System = system, AppDir = appDir, NotInstalledReason = reason, Role = Apps.RolePreset(baseDir, system),
            Run = Machine.Run, Places = ServicePlaces.Default, VersionOf = Updates.VersionOf, SpawnDetached = Updates.SpawnDetached,
        };
    }
}

/// <summary>
/// Installing a release (D4). Only an installed copy updates: a Mac bundle swaps in the new one from its DMG, a
/// Windows install runs the role's Setup.exe quietly and lets it relaunch the app. Both check the download's
/// SHA-256 against the release's SHA256SUMS.txt first.
/// </summary>
public static partial class Updates
{
    public const double FirstCheckAfter = 10 * 60;
    public const double CheckEvery = 6 * 3600;
    public const double LockStaleAfter = 20 * 60;

    /// <summary>The version on disk right now: another process (the CLI, the library page's Update now) may have
    /// installed a newer one than this process runs.</summary>
    public static string InstalledVersion(string appDir, string? system = null)
    {
        try
        {
            string? v = (system ?? Machine.Platform) switch
            {
                "Darwin" => Apps.PlistString(Path.Combine(appDir, "Contents", "Info.plist"), "CFBundleShortVersionString"),
                "Windows" => Apps.WindowsIniVersion(appDir),
                _ => null,
            };
            return v is { Length: > 0 } ? v : Engine.Version;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Engine.Version;
        }
    }

    /// <summary>Why this copy doesn't update itself, or null when it does.</summary>
    public static string? WhyNotUpdatable(UpdateHost host) =>
        host.System switch
        {
            "Linux" => "Study Stash updates itself on a Mac or a Windows PC.",
            _ => host.AppDir.Length > 0 ? null : host.NotInstalledReason,
        };

    /// <summary>What running this exact program with `version` prints, or null when it doesn't run here.</summary>
    public static string? VersionOf(string exe)
    {
        var p = Machine.Run(exe, ["version"], TimeSpan.FromSeconds(30));
        return p is { ExitCode: 0 } ? Py.Strip(p.Stdout) : null;
    }

    /// <summary>Start a program with no console and no wait, so it outlives this one.</summary>
    public static void SpawnDetached(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(args[0]) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args.Skip(1)) psi.ArgumentList.Add(a);
        using var _ = Process.Start(psi);
    }

    static bool ChecksumOk(string file, string asset, IReadOnlyDictionary<string, string>? checksums)
    {
        if (checksums is null || !checksums.TryGetValue(asset, out string? want)) return false;
        using var stream = File.OpenRead(file);
        return string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The services that run this copy's own program: an update restarts those, and leaves anything else's alone.</summary>
    static List<string> ServicesRunning(string exe, UpdateHost host) =>
        Autostart.InstalledRoles(host.Places, host.System).Where(role =>
        {
            try
            {
                string text = File.ReadAllText(Autostart.ServicePath(role, host.Places, host.System));
                return text.Contains(exe, StringComparison.Ordinal) || text.Contains(exe.Replace("&", "&amp;"), StringComparison.Ordinal);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }).ToList();

    static string MacExe(string appDir) => Path.Combine(appDir, "Contents", "MacOS", "StudyStash");

    /// <summary>Mac path of D4: mount the DMG, copy its app next to this one, check it's really the release before
    /// trusting it, then swap it in and restart what was running from the old one.</summary>
    static async Task<bool> ApplyMacAsync(Release release, string home, UpdateHost host, string asset, string url,
        IReadOnlyDictionary<string, string>? checksums, bool restartServices, Action<string> log)
    {
        string scratch = Path.Combine(host.TempDir, "study-stash-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string dmg = Path.Combine(scratch, asset);
            try
            {
                await Ready.DownloadAsync(url, dmg, http: host.Http);
            }
            catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException or TaskCanceledException)
            {
                log($"Download failed: {e.Message}");
                return false;
            }
            if (!ChecksumOk(dmg, asset, checksums))
            {
                log("The download didn't match its checksum, so this version stays.");
                return false;
            }
            string mount = Path.Combine(scratch, "mount");
            Directory.CreateDirectory(mount);
            string appName = Path.GetFileName(host.AppDir);
            string parent = Path.GetDirectoryName(host.AppDir)!;
            string fresh = Path.Combine(parent, "." + appName + ".new");
            Apps.TryDeleteFolder(fresh);
            bool attached = false;
            try
            {
                if (host.Run("hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, dmg], TimeSpan.FromMinutes(5))
                    is not { ExitCode: 0 })
                {
                    log($"The {release.Tag} download didn't run on this Mac, so this version stays.");
                    return false;
                }
                attached = true;
                string onVolume = Path.Combine(mount, appName);
                if (!Directory.Exists(onVolume) || host.Run("ditto", [onVolume, fresh], TimeSpan.FromMinutes(5)) is not { ExitCode: 0 })
                {
                    log($"The {release.Tag} download didn't run on this Mac, so this version stays.");
                    return false;
                }
            }
            finally
            {
                if (attached) host.Run("hdiutil", ["detach", mount, "-quiet"], TimeSpan.FromMinutes(1));
            }
            string freshVersion = Apps.PlistString(Path.Combine(fresh, "Contents", "Info.plist"), "CFBundleShortVersionString") ?? "";
            bool signedOk = host.Run("codesign", ["--verify", "--deep", "--strict", fresh], TimeSpan.FromMinutes(2)) is { ExitCode: 0 };
            bool runsOk = host.VersionOf(MacExe(fresh)) is string v && Compare(ParseVersion(v), release.Version) == 0;
            if (Compare(ParseVersion(freshVersion), release.Version) != 0 || !signedOk || !runsOk)
            {
                Apps.TryDeleteFolder(fresh);
                log($"The {release.Tag} download didn't run on this Mac, so this version stays.");
                return false;
            }
            List<string> roles = restartServices ? ServicesRunning(MacExe(host.AppDir), host) : [];
            try
            {
                Apps.SwapMacBundle(host.AppDir, fresh, host.Run);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Apps.TryDeleteFolder(fresh);
                log($"Install failed: {e.Message}");
                return false;
            }
            log($"Installed {release.Tag}.");
            foreach (string role in roles)
            {
                Autostart.Restart(role, host.Places, host.Run, host.System);
                log($"Restarted the {role} service.");
            }
            if (host.RelaunchPid != 0)
            {
                const string script = "app=\"$1\"; shift; while kill -0 \"$0\" 2>/dev/null; do sleep 1; done; exec /usr/bin/open \"$app\" --args \"$@\"";
                host.SpawnDetached(["/bin/sh", "-c", script, host.RelaunchPid.ToString(CultureInfo.InvariantCulture), host.AppDir, .. host.RelaunchArgs]);
            }
            return true;
        }
        finally
        {
            Apps.TryDeleteFolder(scratch);
        }
    }

    /// <summary>Windows path of D4: hand the role's Setup.exe the quiet, self-relaunching arguments and return - Setup
    /// closes this copy, installs over it, and starts it again.</summary>
    static async Task<bool> ApplyWindowsAsync(Release release, string home, UpdateHost host, string asset, string url,
        IReadOnlyDictionary<string, string>? checksums, Action<string> log)
    {
        string dir = Path.Combine(host.TempDir, "Study Stash update");
        Directory.CreateDirectory(dir);
        string setup = Path.Combine(dir, asset);
        try
        {
            await Ready.DownloadAsync(url, setup, http: host.Http);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException or TaskCanceledException)
        {
            log($"Download failed: {e.Message}");
            return false;
        }
        if (!ChecksumOk(setup, asset, checksums))
        {
            try
            {
                File.Delete(setup);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            log("The download didn't match its checksum, so this version stays.");
            return false;
        }
        string logPath = Path.Combine(home, "logs", "update.log");
        host.SpawnDetached([setup, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/relaunch=1", $"/LOG={logPath}"]);
        log($"Installing {release.Tag} in the background (log: {logPath}).");
        return true;
    }

    /// <summary>Install `release` for this host's role. Downloads its installer, checks it against SHA256SUMS.txt,
    /// then swaps it in (Mac) or hands off to it (Windows).</summary>
    public static async Task<bool> ApplyAsync(Release release, string home, UpdateHost host, Action<string>? log = null, bool restartServices = true)
    {
        log ??= Console.WriteLine;
        if (WhyNotUpdatable(host) is string problem)
        {
            log(problem);
            return false;
        }
        string? asset = Installer(host.System, host.Role);
        if (asset is null)
        {
            log("Study Stash updates itself on a Mac or a Windows PC.");
            return false;
        }
        string url = release.Assets?.GetValueOrDefault(asset) ?? "";
        if (url.Length == 0)
        {
            log($"{release.Tag} has no {asset} yet; the next check tries again.");
            return false;
        }
        Directory.CreateDirectory(Path.Combine(home, "logs"));
        IReadOnlyDictionary<string, string>? checksums = null;
        if (release.Assets?.GetValueOrDefault(ChecksumsAsset) is { Length: > 0 } checksumsUrl)
        {
            try
            {
                using var r = await (host.Http ?? Http).GetAsync(checksumsUrl);
                r.EnsureSuccessStatusCode();
                checksums = Checksums.Parse(await r.Content.ReadAsStringAsync());
            }
            catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException)
            {
            }
        }
        log($"Installing {release.Tag}...");
        return host.System == "Darwin"
            ? await ApplyMacAsync(release, home, host, asset, url, checksums, restartServices, log)
            : await ApplyWindowsAsync(release, home, host, asset, url, checksums, log);
    }

    // --- the background checker ------------------------------------------------------------------------------------

    /// <summary>One updater at a time when the library and the laptop's watcher run on the same computer.</summary>
    sealed class UpdateLock(string path)
    {
        public bool Acquire()
        {
            try
            {
                if (File.Exists(path) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds > LockStaleAfter) File.Delete(path);
                using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                f.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        public void Release()
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public delegate Task<bool> ApplyFn(Release release, string home, Action<string> log, bool restartServices);

    /// <summary>One auto-update round. Returns what happened (for logs and tests). `exit` stops this process so the
    /// service manager, or the app itself once idle, starts the new version.</summary>
    public static async Task<string> CheckAndUpdateAsync(string home, UpdateHost host, Action<string>? log = null, bool? supervised = null,
        Func<Task<Release?>>? latest = null, ApplyFn? apply = null, Action<int>? exit = null)
    {
        log ??= Console.WriteLine;
        bool underService = supervised ?? Environment.GetEnvironmentVariable(Autostart.ServiceEnv) == "1";
        apply ??= (r, h, l, restart) => ApplyAsync(r, h, host, l, restart);
        exit ??= Environment.Exit;
        Release? rel;
        try
        {
            rel = await (latest ?? (() => LatestAsync()))();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return $"check failed: {e.Message}";
        }
        if (rel is null || !IsNewer(rel)) return "up to date";
        if (!underService || WhyNotUpdatable(host) is not null)
        {
            log($"[update] {rel.Tag} is available: run `studystash update`.");
            return "available";
        }
        var gate = new UpdateLock(Path.Combine(home, "update.lock"));
        if (!gate.Acquire()) return "another update is running";
        try
        {
            bool windows = host.System == "Windows";
            // A Mac's launchd or Linux's systemd restarts this process after it exits; on Windows Setup.exe does it.
            if (windows || Compare(ParseVersion(InstalledVersion(host.AppDir, host.System)), rel.Version) < 0)
                if (!await apply(rel, home, s => log($"[update] {s}"), windows))
                    return "install failed";
            if (windows) return "handed off"; // Setup.exe closes this process, installs, and relaunches the app itself
        }
        finally
        {
            gate.Release();
        }
        log($"[update] now on {rel.Tag}; restarting");
        exit(0);
        return "restarting";
    }

    /// <summary>`enabled` is read before each check, so turning auto_update off in config takes effect.</summary>
    public static Task StartAutoUpdate(string home, Func<bool> enabled, Action<int> exit, Action<string> log, CancellationToken stop) => Task.Run(async () =>
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(FirstCheckAfter), stop);
            while (true)
            {
                bool on;
                try
                {
                    on = enabled();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or Tomlyn.TomlException)
                {
                    on = false;
                }
                if (on)
                {
                    string result = await CheckAndUpdateAsync(home, UpdateHost.ThisComputer(), log, exit: exit);
                    if (result is not ("up to date" or "available")) log($"[update] {result}");
                }
                await Task.Delay(TimeSpan.FromSeconds(CheckEvery), stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
    });
}
