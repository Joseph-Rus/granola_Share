using System.Diagnostics;
using System.Text;

namespace StudyStash.Core;

/// <summary>Where each system keeps the background services. Tests point these at a folder of their own.</summary>
public sealed record ServicePlaces(string LaunchAgents, string Startup, string Systemd)
{
    public static ServicePlaces Default => new(
        Path.Combine(Py.UserHome(), "Library", "LaunchAgents"),
        Path.Combine(Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData ? appData : Py.UserHome(),
            "Microsoft", "Windows", "Start Menu", "Programs", "Startup"),
        Path.Combine(Py.UserHome(), ".config", "systemd", "user"));
}

/// <summary>
/// Keep the library running under its own name: launchd on a Mac, the Startup folder on Windows, systemd --user on
/// Linux. Installing it clears any service left by the app's name before the rename first, so a computer never runs
/// two libraries fighting over one port.
/// </summary>
public static class Autostart
{
    public static readonly IReadOnlyDictionary<string, string[]> Roles = new Dictionary<string, string[]>
    {
        ["server"] = ["run"],
    };

    /// <summary>Set for the background service only: tells the auto-updater that exiting means "restart me".</summary>
    public const string ServiceEnv = "STUDYSTASH_SERVICE";
    /// <summary>Windows: set on the service KeepAlive starts, so it doesn't start another KeepAlive.</summary>
    public const string ChildEnv = "STUDYSTASH_SERVICE_CHILD";
    /// <summary>Windows: set on the windowless copy the Startup file's copy hands over to (see <see cref="Detach"/>).</summary>
    public const string SupervisorEnv = "STUDYSTASH_SERVICE_SUPERVISOR";
    /// <summary>Bytes: the Windows log starts over (keeping one old copy) past this.</summary>
    public const long LogLimit = 5_000_000;

    /// <summary>True while this process runs as the background service: <see cref="ServiceEnv"/>, or the
    /// <see cref="LegacyServiceEnv"/> that a service file written before the app's rename still sets.</summary>
    public static bool UnderService(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        return env(ServiceEnv) == "1" || env(LegacyServiceEnv) == "1";
    }

    /// <summary>How to start this engine: its own program, or `dotnet studystash.dll` for a build run that way.</summary>
    public static string[] EngineCommand()
    {
        string exe = Environment.ProcessPath ?? "studystash";
        return Path.GetFileNameWithoutExtension(exe) == "dotnet" ? [exe, Path.Combine(AppContext.BaseDirectory, "studystash.dll")] : [exe];
    }

    /// <summary>What the service needs beyond the marker: where .NET is, for a build that doesn't carry its own.</summary>
    public static List<(string Name, string Value)> ExtraEnvironment() =>
        Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root ? [("DOTNET_ROOT", root)] : [];

    public static List<string> RoleArgs(string role, string home, IReadOnlyList<string>? engine = null)
    {
        if (!Roles.TryGetValue(role, out var command)) throw new ArgumentException("role must be 'server'");
        return [.. engine ?? EngineCommand(), "--home", home, .. command];
    }

    public static string Label(string role) => $"com.study-stash.{role}";

    public static string ServicePath(string role, ServicePlaces? places = null, string? system = null)
    {
        places ??= ServicePlaces.Default;
        return (system ?? Machine.Platform) switch
        {
            "Darwin" => Path.Combine(places.LaunchAgents, $"{Label(role)}.plist"),
            "Windows" => Path.Combine(places.Startup, $"study-stash-{role}.cmd"),
            _ => Path.Combine(places.Systemd, $"study-stash-{role}.service"),
        };
    }

    // --- the service files ---------------------------------------------------------------------------------------

    /// <summary>xml.sax.saxutils.escape: &amp;, &lt; and &gt;.</summary>
    static string Xml(string s) => s.Replace("&", "&amp;").Replace(">", "&gt;").Replace("<", "&lt;");

    public static string RenderPlist(string label, IReadOnlyList<string> args, string logPath, IReadOnlyList<(string Name, string Value)>? env = null,
        string? userHome = null)
    {
        string items = string.Concat(args.Select(a => $"\n        <string>{Xml(a)}</string>"));
        string extra = string.Concat((env ?? []).Select(e => $"        <key>{Xml(e.Name)}</key><string>{Xml(e.Value)}</string>\n"));
        string localBin = (userHome ?? Py.UserHome()) + "/.local/bin";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key><string>{Xml(label)}</string>
                <key>ProgramArguments</key>
                <array>{items}
                </array>
                <key>RunAtLoad</key><true/>
                <key>KeepAlive</key><true/>
                <key>StandardOutPath</key><string>{Xml(logPath)}</string>
                <key>StandardErrorPath</key><string>{Xml(logPath)}</string>
                <key>EnvironmentVariables</key>
                <dict>
                    <key>PATH</key><string>/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:{Xml(localBin)}</string>
                    <key>{ServiceEnv}</key><string>1</string>

            """.ReplaceLineEndings("\n") + extra + "    </dict>\n</dict>\n</plist>\n";
    }

    public static string RenderSystemd(string description, IReadOnlyList<string> args, IReadOnlyList<(string Name, string Value)>? env = null)
    {
        string cmd = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        string extra = string.Concat((env ?? []).Select(e => $"Environment={e.Name}={e.Value}\n"));
        return $"[Unit]\nDescription={description}\n\n[Service]\nExecStart={cmd}\nEnvironment={ServiceEnv}=1\n{extra}"
            + "Restart=always\nRestartSec=10\n\n[Install]\nWantedBy=default.target\n";
    }

    /// <summary>
    /// The Startup folder's file. It runs the engine straight away, and the engine hands over to a copy of itself with
    /// no window (<see cref="Detach"/>): Python's pythonw had no console, but this engine is a console program, and
    /// `start /min` would leave its window in the taskbar for as long as it runs.
    /// </summary>
    public static string RenderCmd(IReadOnlyList<string> args, IReadOnlyList<(string Name, string Value)>? env = null) =>
        Batch(["@echo off", $"set {ServiceEnv}=1", .. (env ?? []).Select(e => $"set {e.Name}={e.Value}"),
            string.Join(" ", args.Select(a => $"\"{a}\""))]);

    /// <summary>A batch file's text. cmd reads one in the console's code page, so a path with "é" in it needs UTF-8 first.</summary>
    public static string Batch(IEnumerable<string> lines)
    {
        var all = lines.ToList();
        if (all.Any(l => l.Any(c => c > 127))) all.Insert(1, "chcp 65001 >nul");
        return string.Join("\r\n", all) + "\r\n";
    }

    // --- installing and removing ---------------------------------------------------------------------------------

    static readonly TimeSpan Quick = TimeSpan.FromSeconds(60);

    /// <summary>Install and start the service. What changes the computer takes its places and runner explicitly (a
    /// test that forgot one would install a real service): ServicePlaces.Default and Machine.Run for the real thing.</summary>
    public static string Install(string role, string home, ServicePlaces places, Runner run,
        IReadOnlyList<string>? engine = null, string? system = null, IReadOnlyList<(string Name, string Value)>? env = null)
    {
        system ??= Machine.Platform;
        env ??= ExtraEnvironment();
        var args = RoleArgs(role, home, engine);
        string logs = Path.Combine(home, "logs");
        Directory.CreateDirectory(logs);
        string log = Path.Combine(logs, $"{role}.log");
        string path = ServicePath(role, places, system);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RemoveLegacy(places, run, system); // never two libraries fighting over one port: the old watcher goes too
        if (system == "Darwin")
        {
            Py.WriteText(path, RenderPlist(Label(role), args, log, env));
            string target = $"gui/{Machine.Uid()}";
            run("launchctl", ["bootout", target, path], Quick);
            run("launchctl", ["bootstrap", target, path], Quick);
        }
        else if (system == "Windows")
        {
            StopWindows(run, role); // an older copy of this service may still be running
            File.WriteAllText(path, RenderCmd(args, env));
            StartWindows(run, path); // start it right away too
        }
        else
        {
            Py.WriteText(path, RenderSystemd("Study Stash library", args, env));
            run("systemctl", ["--user", "daemon-reload"], Quick);
            run("systemctl", ["--user", "enable", Path.GetFileName(path)], Quick);
            run("systemctl", ["--user", "restart", Path.GetFileName(path)], Quick);
        }
        return path;
    }

    public static bool Uninstall(string role, ServicePlaces places, Runner run, string? system = null)
    {
        system ??= Machine.Platform;
        string path = ServicePath(role, places, system);
        bool existed = File.Exists(path);
        if (existed)
        {
            if (system == "Darwin") run("launchctl", ["bootout", $"gui/{Machine.Uid()}", path], Quick);
            else if (system == "Windows") StopWindows(run, role);
            else run("systemctl", ["--user", "disable", "--now", Path.GetFileName(path)], Quick);
            File.Delete(path);
        }
        RemoveLegacyOne(role, places, run, system); // a service from before the rename doesn't linger once this one's gone
        return existed;
    }

    public static List<string> InstalledRoles(ServicePlaces? places = null, string? system = null) =>
        Roles.Keys.Where(r => File.Exists(ServicePath(r, places, system))).ToList();

    /// <summary>"missing" (not installed), "running", or "stopped".</summary>
    public static string Status(string role, ServicePlaces? places = null, Runner? run = null, string? system = null)
    {
        run ??= Machine.Run;
        system ??= Machine.Platform;
        string path = ServicePath(role, places, system);
        if (!File.Exists(path)) return "missing";
        if (system == "Darwin")
        {
            var p = run("launchctl", ["print", $"gui/{Machine.Uid()}/{Label(role)}"], Quick);
            return p is { ExitCode: 0 } && p.Stdout.Contains("state = running", StringComparison.Ordinal) ? "running" : "stopped";
        }
        if (system == "Windows")
        {
            var p = run("powershell", ["-NoProfile", "-Command", $"@(Get-CimInstance Win32_Process | Where-Object {{ {PsFilter(role)} }}).Count"], Quick);
            return p is not null && int.TryParse(Py.Strip(p.Stdout) is { Length: > 0 } n ? n : "0", out int count) && count > 0 ? "running" : "stopped";
        }
        var s = run("systemctl", ["--user", "is-active", Path.GetFileName(path)], Quick);
        return s is not null && Py.Strip(s.Stdout) == "active" ? "running" : "stopped";
    }

    public static void Restart(string role, ServicePlaces places, Runner run, string? system = null)
    {
        system ??= Machine.Platform;
        string path = ServicePath(role, places, system);
        if (!File.Exists(path)) return;
        if (system == "Darwin")
        {
            string target = $"gui/{Machine.Uid()}";
            var p = run("launchctl", ["kickstart", "-k", $"{target}/{Label(role)}"], Quick);
            if (p is not { ExitCode: 0 }) run("launchctl", ["bootstrap", target, path], Quick); // not loaded yet
        }
        else if (system == "Windows")
        {
            StopWindows(run, role);
            StartWindows(run, path);
        }
        else
        {
            run("systemctl", ["--user", "restart", Path.GetFileName(path)], Quick);
        }
    }

    // --- legacy: the service files from before the app's rename ----------------------------------------------------

    /// <summary>What a service file written before the app's rename set instead of <see cref="ServiceEnv"/>; still
    /// recognised so a service installed then still counts as running under this until it's restarted onto today's.</summary>
    public const string LegacyServiceEnv = "GRANOLA_SHARE_SERVICE";

    static readonly string[] LegacyRoles = ["server", "client"];

    static string LegacyServicePath(string role, ServicePlaces places, string system) => system switch
    {
        "Darwin" => Path.Combine(places.LaunchAgents, $"com.granola-share.{role}.plist"),
        "Windows" => Path.Combine(places.Startup, $"granola-share-{role}.cmd"),
        _ => Path.Combine(places.Systemd, $"granola-share-{role}.service"),
    };

    static bool RemoveLegacyOne(string role, ServicePlaces places, Runner run, string system)
    {
        string path = LegacyServicePath(role, places, system);
        if (!File.Exists(path)) return false;
        if (system == "Darwin") run("launchctl", ["bootout", $"gui/{Machine.Uid()}", path], Quick);
        else if (system == "Windows") StopWindows(run, role);
        else run("systemctl", ["--user", "disable", "--now", Path.GetFileName(path)], Quick);
        File.Delete(path);
        return true;
    }

    /// <summary>Stops and deletes any service file left by the app's name before the rename (server, and the old
    /// laptop watcher's client), so a computer never runs two libraries fighting over one port. Only touches a file
    /// that's actually under `places`, so a scratch-place test (or the live test) can never reach a real service.</summary>
    public static List<string> RemoveLegacy(ServicePlaces places, Runner run, string? system = null)
    {
        system ??= Machine.Platform;
        return [.. LegacyRoles.Where(role => RemoveLegacyOne(role, places, run, system)).Select(role => LegacyServicePath(role, places, system))];
    }

    // --- Windows: finding, stopping and starting the service -------------------------------------------------------

    // A background service's command line ends in its command: `... granola_share.cli --home X run` (the Python
    // engine, or `serve`), `...studystash.exe" --home X run` (this engine's, or `dotnet ...studystash.dll` for a build
    // run that way), or `... client run` (either engine's laptop watcher, from before the rename dropped that role).
    // Nothing else counts, so an `autostart install` or `status` running right now never stops or counts itself.
    // RemoveLegacy uses these on Windows to stop a service from before the rename before deleting its file.
    const string Engines = @"(granola_share\.cli|studystash(\.exe|\.dll)?\x22?\s)";
    public const string ClientRun = Engines + @".*\bclient\x22?\s+\x22?run\x22?\s*$";
    public const string ServerRun = Engines + @".*\b(run|serve)\x22?\s*$";

    /// <summary>PowerShell's test for "this process is the service for this role" (-match is .NET's own regex).</summary>
    public static string PsFilter(string? role)
    {
        string client = $"$_.CommandLine -match '{ClientRun}'";
        string server = $"($_.CommandLine -match '{ServerRun}' -and $_.CommandLine -notmatch '{ClientRun}')";
        return role switch
        {
            "client" => client,
            "server" => server,
            _ => $"({client} -or {server})",
        };
    }

    static void StopWindows(Runner run, string? role = null) =>
        run("powershell", ["-NoProfile", "-Command",
            $"Get-CimInstance Win32_Process | Where-Object {{ {PsFilter(role)} }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force }}"], Quick);

    /// <summary>Run a Startup file now. The engine it starts hands over to a windowless copy that holds none of this
    /// command's pipes (see <see cref="Detach"/>), so this returns as soon as the handover is done.</summary>
    static void StartWindows(Runner run, string cmd) => run("cmd", ["/c", cmd], Quick);

    /// <summary>
    /// Windows, from the Startup file: start this same command again as a windowless copy, and return. Started through
    /// the shell, the copy inherits no handles, so nothing waiting on this program's output waits for the service.
    /// </summary>
    public static void Detach(IReadOnlyList<string> argv)
    {
        var command = EngineCommand();
        Environment.SetEnvironmentVariable(SupervisorEnv, "1");
        var psi = new ProcessStartInfo(command[0]) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string a in command.Skip(1).Concat(argv)) psi.ArgumentList.Add(a);
        using var _ = Process.Start(psi);
    }

    /// <summary>Runs the engine with these arguments until it stops, sending what it prints to `output`; its exit code.</summary>
    public delegate int Spawn(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, Action<string> output);

    static int SpawnHidden(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, Action<string> output)
    {
        var psi = new ProcessStartInfo(args[0])
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args.Skip(1)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{args[0]} didn't start");
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) output(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) output(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>
    /// Windows has no launchd KeepAlive or systemd Restart=, so there the service runs under this small loop: it
    /// writes the log that launchd and systemd keep elsewhere, and starts the service again whenever it stops.
    /// </summary>
    public static void KeepAlive(string home, string role, IReadOnlyList<string> argv, Spawn? spawn = null,
        Action<int>? sleep = null, int? rounds = null, IReadOnlyList<string>? engine = null)
    {
        spawn ??= SpawnHidden;
        sleep ??= seconds => Thread.Sleep(TimeSpan.FromSeconds(seconds));
        string log = Path.Combine(home, "logs", $"{role}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        var env = new Dictionary<string, string> { [ChildEnv] = "1" };
        for (int done = 0; rounds is null || done < rounds; done++)
        {
            try
            {
                if (File.Exists(log) && new FileInfo(log).Length > LogLimit) File.Move(log, log + ".1", overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            var began = Stopwatch.StartNew();
            int pause;
            using (var output = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true })
            {
                var gate = new Lock();
                int code = spawn([.. engine ?? EngineCommand(), .. argv], env, line =>
                {
                    lock (gate) output.Write(line + "\n");
                });
                pause = began.Elapsed.TotalSeconds > 60 ? 10 : 60; // failing as it starts: don't spin
                lock (gate) output.Write($"[service] stopped (exit {code}); starting it again in {pause} s\n");
            }
            sleep(pause);
        }
    }
}
