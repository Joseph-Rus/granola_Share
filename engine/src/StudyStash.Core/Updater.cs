using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace StudyStash.Core;

/// <summary>
/// What installing an update touches, so tests can point it somewhere else. What only looks (the service files, the
/// release) looks at this computer; what changes it (the engine's folder, commands, the apps) is off unless
/// <see cref="ThisComputer"/> turns it on.
/// </summary>
public sealed class UpdateHost
{
    static Exception Off(string what) => new InvalidOperationException($"{what} is off for this update.");

    public string System { get; init; } = Machine.Platform;
    /// <summary>The engine's own folder: the whole folder is what an update replaces. None unless given.</summary>
    public string Dir { get; init; } = "";
    public HttpClient? Http { get; init; }
    public Runner Run { get; init; } = (_, _, _) => throw Off("Running commands");
    public ServicePlaces Places { get; init; } = ServicePlaces.Default;
    /// <summary>Where the Study Stash apps are, to update them too; none unless given.</summary>
    public AppPlaces? AppsAt { get; init; }
    /// <summary>What a copy of the engine says `version` is, or null when it doesn't run here.</summary>
    public Func<string, string?> VersionOf { get; init; } = _ => throw Off("Running the new engine");
    /// <summary>Windows: start the helper that swaps the folder once this engine has stopped.</summary>
    public Action<IReadOnlyList<string>> SpawnDetached { get; init; } = _ => throw Off("Starting the update helper");

    /// <summary>The real thing: this engine's own folder, and the apps in their usual places.</summary>
    public static UpdateHost ThisComputer() => new()
    {
        Dir = Updates.InstallDir, Run = Machine.Run, AppsAt = AppPlaces.Default, VersionOf = Updates.VersionOf,
        SpawnDetached = Updates.SpawnDetached,
    };
}

/// <summary>
/// Installing a release (update.py). The engine is one folder; a release has a zip of it for each system. Installing
/// unpacks the new folder next to this one, checks it runs here, swaps the two, and restarts the services that run
/// from it. Windows won't replace a program that's running, so there a helper does the swap once this engine stops.
/// The Study Stash apps update along with it, as with the Python engine.
/// </summary>
public static partial class Updates
{
    /// <summary>In the engine's folder when it was installed from a release: its version. A build folder has none,
    /// and an update never replaces one.</summary>
    public const string EngineMarker = "study-stash-engine.txt";
    public const double FirstCheckAfter = 10 * 60;
    public const double CheckEvery = 6 * 3600;
    public const double LockStaleAfter = 20 * 60;

    /// <summary>This engine's download for this computer, e.g. Study-Stash-engine-mac-arm64.zip.</summary>
    public static string EngineAsset(string? system = null, Architecture? arch = null)
    {
        string os = (system ?? Machine.Platform) switch { "Darwin" => "mac", "Windows" => "windows", _ => "linux" };
        string cpu = (arch ?? RuntimeInformation.OSArchitecture) switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            var other => other.ToString().ToLowerInvariant(),
        };
        return $"Study-Stash-engine-{os}-{cpu}.zip";
    }

    public static string InstallDir => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string EngineExe(string dir, string? system = null) =>
        Path.Combine(dir, (system ?? Machine.Platform) == "Windows" ? "studystash.exe" : "studystash");

    /// <summary>The version on disk right now: another process may have installed a newer one than this one runs.</summary>
    public static string InstalledVersion(string? dir = null)
    {
        try
        {
            return Py.Strip(Py.ReadText(Path.Combine(dir ?? InstallDir, EngineMarker))) is { Length: > 0 } v ? v : Engine.Version;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Engine.Version;
        }
    }

    public static string? WhyNotUpdatable(string? dir = null)
    {
        dir ??= InstallDir;
        if (dir.Length == 0) return "No install folder was given, so there's nothing to update.";
        return File.Exists(Path.Combine(dir, EngineMarker)) ? null
            : $"This copy runs from a build folder ({dir}), not an install. Update it with `git pull` and build it again.";
    }

    public static string? VersionOf(string exe)
    {
        var p = Machine.Run(exe, ["version"], TimeSpan.FromSeconds(30));
        return p is { ExitCode: 0 } ? Py.Strip(p.Stdout) : null;
    }

    /// <summary>A hidden console its commands share (no windows flash), started through the shell so it inherits no
    /// handles and outlives this engine.</summary>
    public static void SpawnDetached(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(args[0]) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string a in args.Skip(1)) psi.ArgumentList.Add(a);
        using var _ = Process.Start(psi);
    }

    static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Download the engine and unpack it beside the one in use.</summary>
    static async Task StageAsync(string url, string staged, UpdateHost host)
    {
        TryDelete(staged);
        using var tmp = new Ready.TempFolder();
        string zip = await Ready.DownloadAsync(url, Path.Combine(tmp.Path, "engine.zip"), http: host.Http);
        if (host.System == "Darwin")
        {
            if (host.Run("ditto", ["-x", "-k", zip, staged], TimeSpan.FromMinutes(10)) is not { ExitCode: 0 })
                throw new IOException("the download didn't unpack");
        }
        else
        {
            ZipFile.ExtractToDirectory(zip, staged);
        }
        string exe = EngineExe(staged, host.System);
        if (!File.Exists(exe) || !File.Exists(Path.Combine(staged, EngineMarker)))
        {
            TryDelete(staged);
            throw new IOException($"the download has no {Path.GetFileName(exe)}");
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    /// <summary>Replace `dir` with `staged`, putting it back if that fails. A program running from the old folder
    /// keeps running: its files stay open until it stops.</summary>
    static void Swap(string dir, string staged)
    {
        string old = dir + ".old";
        TryDelete(old);
        Directory.Move(dir, old);
        try
        {
            Directory.Move(staged, dir);
        }
        catch
        {
            Directory.Move(old, dir);
            throw;
        }
        TryDelete(old);
    }

    /// <summary>The services this engine's folder runs: an update restarts those, and leaves the Python engine's.</summary>
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

    /// <summary>
    /// Windows won't replace files in use, so a detached helper waits for this engine to stop, stops what's left of
    /// it, swaps the new folder in, and starts the services again from it.
    /// </summary>
    public static string WindowsScript(string home, string target, IReadOnlyList<string> roles)
    {
        string log = Path.Combine(home, "logs", "update.log");
        string d = target, fresh = target + ".new", old = target + ".old";
        string inside = target.TrimEnd('\\', '/').Replace("'", "''") + "\\";
        string stop = "Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and "
            + $"$_.ExecutablePath.StartsWith('{inside}', 'OrdinalIgnoreCase') }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force }}";
        var lines = new List<string>
        {
            "@echo off", "timeout /t 5 /nobreak >nul",
            $"powershell -NoProfile -Command \"{stop}\" >> \"{log}\" 2>&1",
            "timeout /t 2 /nobreak >nul",
            $"if exist \"{old}\" rmdir /s /q \"{old}\"",
            $"if exist \"{d}\" move \"{d}\" \"{old}\" >> \"{log}\" 2>&1",
            $"if exist \"{d}\" goto kept",
            $"move \"{fresh}\" \"{d}\" >> \"{log}\" 2>&1",
            $"if not exist \"{d}\" move \"{old}\" \"{d}\" >> \"{log}\" 2>&1",
            "goto ready",
            ":kept",
            $"echo Something still had {d} open, so this version stays. >> \"{log}\"",
            $"rmdir /s /q \"{fresh}\"",
            ":ready",
        };
        foreach (string role in roles)
            lines.Add($"\"{EngineExe(target, "Windows")}\" --home \"{home}\" autostart install --role {role} >> \"{log}\" 2>&1");
        lines.Add($"if exist \"{old}\" rmdir /s /q \"{old}\"");
        string script = Path.Combine(home, "update.cmd");
        File.WriteAllText(script, Autostart.Batch(lines));
        return script;
    }

    /// <summary>Install `release`. The services that run this engine are restarted onto it. On Windows this hands off
    /// to a helper and returns before the install happens.</summary>
    public static async Task<bool> ApplyAsync(Release release, string home, UpdateHost host, Action<string>? log = null, bool restartServices = true)
    {
        log ??= Console.WriteLine;
        if (WhyNotUpdatable(host.Dir) is string problem)
        {
            log(problem);
            return false;
        }
        string asset = EngineAsset(host.System);
        string url = release.Assets?.GetValueOrDefault(asset) ?? "";
        if (url.Length == 0)
        {
            log($"{release.Tag} has no {asset} download yet; the next check tries again.");
            return false;
        }
        var roles = ServicesRunning(EngineExe(host.Dir, host.System), host);
        Directory.CreateDirectory(Path.Combine(home, "logs"));
        string staged = host.Dir + ".new";
        log($"Installing {release.Tag}...");
        try
        {
            await StageAsync(url, staged, host);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException
                                      or TaskCanceledException)
        {
            log($"Download failed: {e.Message}");
            return false;
        }
        if (host.VersionOf(EngineExe(staged, host.System)) is not string runs || Compare(ParseVersion(runs), release.Version) != 0)
        {
            TryDelete(staged);
            log($"The {release.Tag} download didn't run on this computer, so this version stays.");
            return false;
        }
        if (host.System == "Windows")
        {
            foreach (string folder in release.WindowsApp.Length > 0 && host.AppsAt is { } at ? Apps.WindowsAppsInstalled(at) : []) // the apps update too
                await Apps.InstallWindowsAppAsync(release.WindowsApp, log, folder, host.Http);
            string script = WindowsScript(home, host.Dir, restartServices ? roles : []);
            host.SpawnDetached(["cmd", "/c", script]);
            log($"Installing {release.Tag} in the background (log: {Path.Combine(home, "logs", "update.log")}).");
            return true;
        }
        try
        {
            Swap(host.Dir, staged);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(staged);
            log($"Install failed: {e.Message}");
            return false;
        }
        log($"Installed {release.Tag}.");
        if (host.System == "Darwin" && host.AppsAt is { } apps)
        {
            if (release.MacApp.Length > 0 && Apps.NativeInstalled(apps) is not null) // the Study Stash app updates with it
                await Apps.InstallNativeAsync(release.MacApp, log, host.Http, host.Run, at: apps);
            if (release.MacLibraryApp.Length > 0 && Apps.LibraryAppInstalled(apps) is not null)
                await Apps.InstallNativeAsync(release.MacLibraryApp, log, host.Http, host.Run, Apps.LibraryAppName, apps);
        }
        if (restartServices)
        {
            foreach (string role in roles)
            {
                Autostart.Restart(role, host.Places, host.Run, host.System);
                log($"Restarted the {role} service.");
            }
        }
        return true;
    }

    // --- the background checker ------------------------------------------------------------------------------------

    /// <summary>One updater at a time when the library and the laptop's watcher run on the same computer, whichever
    /// engine each runs (the Python engine takes the same file).</summary>
    sealed class UpdateLock(string path)
    {
        public bool Acquire()
        {
            try
            {
                if (File.Exists(path) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds > LockStaleAfter) File.Delete(path);
                using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                f.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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

    /// <summary>One auto-update round. Returns what happened (for logs and tests). `exit` stops this engine so the
    /// service manager starts the new one.</summary>
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
        if (!underService || WhyNotUpdatable(host.Dir) is not null)
        {
            log($"[update] {rel.Tag} is available: run `studystash update`.");
            return "available";
        }
        var gate = new UpdateLock(Path.Combine(home, "update.lock"));
        if (!gate.Acquire()) return "another update is running";
        try
        {
            bool windows = host.System == "Windows";
            // Elsewhere launchd and systemd restart this engine after it exits; on Windows the helper must do it.
            if (windows || Compare(ParseVersion(InstalledVersion(host.Dir)), rel.Version) < 0)
                if (!await apply(rel, home, s => log($"[update] {s}"), windows))
                    return "install failed";
            if (windows) return "handed off"; // the helper stops this engine, installs, and starts the services again
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
