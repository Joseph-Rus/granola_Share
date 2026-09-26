using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>How the library's own process, run as this app's child, is doing right now.</summary>
public enum LibraryServiceState
{
    Stopped,
    Starting,
    /// <summary>Our child is up and the library answers.</summary>
    Running,
    /// <summary>Something else already answers as a Study Stash library on this port: we didn't start anything.</summary>
    Elsewhere,
    /// <summary>Another program holds the port.</summary>
    PortTaken,
    Failed,
}

/// <summary>
/// Runs `StudyStash --home H serve` as a child of this app: starts it, waits for it to answer, restarts it with a
/// growing pause if it dies (giving up after too many restarts in a row), and stops it cleanly on quit. Never installs
/// a LaunchAgent, Startup entry or systemd unit, and never touches a library that's already answering on the port —
/// it steps aside instead. What actually starts the process is swappable, so a test runs a real, short-lived copy.
/// </summary>
public sealed class LibraryService : IDisposable
{
    const int MaxRestartsIn10Min = 5;
    static readonly int[] RestartDelaysSeconds = [2, 5, 15, 60];

    readonly string home;
    readonly Config cfg;
    readonly IReadOnlyList<string> command;
    readonly Func<ProcessStartInfo, Process> spawn;
    readonly Func<Config, TimeSpan, Task<bool>> waitHealthy;
    readonly Func<int, Task<string>> portStatus;
    readonly Action<string> log;
    readonly Lock gate = new();
    readonly List<DateTime> recentExits = [];
    readonly Queue<string> tail = new();
    Process? proc;
    bool stopping;
    bool disposed;

    public LibraryServiceState State { get; private set; } = LibraryServiceState.Stopped;
    /// <summary>Why it's <see cref="LibraryServiceState.Failed"/> or <see cref="LibraryServiceState.PortTaken"/>; null otherwise.</summary>
    public string? Failure { get; private set; }
    /// <summary>The child's process id while we're running one we started; null otherwise (a test kills it by this).</summary>
    public int? Pid { get { lock (gate) return proc?.Id; } }
    /// <summary>Called (on a worker thread) whenever <see cref="State"/> or <see cref="Failure"/> changes.</summary>
    public event Action? Changed;

    public string LogDir => Path.Combine(home, "logs");
    public string LogPath => Path.Combine(LogDir, "library.log");
    public string PidPath => Path.Combine(home, "library.pid");

    public LibraryService(string home, Config cfg, IReadOnlyList<string>? command = null, Func<ProcessStartInfo, Process>? spawn = null,
        Func<Config, TimeSpan, Task<bool>>? waitHealthy = null, Func<int, Task<string>>? portStatus = null, Action<string>? log = null)
    {
        this.home = home;
        this.cfg = cfg;
        this.command = command ?? [Platform.Desktop.Program];
        this.spawn = spawn ?? (info => Process.Start(info)!);
        this.waitHealthy = waitHealthy ?? ((c, t) => HostInfo.WaitForServerAsync(c, t));
        this.portStatus = portStatus ?? (p => HostInfo.PortStatusAsync(p));
        this.log = log ?? Console.WriteLine;
    }

    void SetState(LibraryServiceState s, string? failure = null)
    {
        State = s;
        Failure = failure;
        Changed?.Invoke();
    }

    /// <summary>Starts the library, unless it's already starting or running. Returns once it answers, steps aside
    /// (<see cref="LibraryServiceState.Elsewhere"/>/<see cref="LibraryServiceState.PortTaken"/>) or fails to.</summary>
    public async Task StartAsync()
    {
        lock (gate)
        {
            if (State is LibraryServiceState.Starting or LibraryServiceState.Running) return;
            stopping = false;
        }
        if (!File.Exists(cfg.ConfigPath))
        {
            SetState(LibraryServiceState.Failed, "Not set up yet");
            return;
        }
        SetState(LibraryServiceState.Starting);
        string status;
        try { status = await portStatus(cfg.WebPort); }
        catch (Exception e) when (e is HttpRequestException or SocketException) { status = "free"; }
        if (status == "busy") { SetState(LibraryServiceState.PortTaken, $"Port {cfg.WebPort} is taken by another program."); return; }
        if (status == "ours") { SetState(LibraryServiceState.Elsewhere); return; }
        await SpawnAsync();
    }

    async Task SpawnAsync()
    {
        Directory.CreateDirectory(LogDir);
        RotateLogIfBig();
        var info = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = home,
        };
        foreach (var a in command.Skip(1)) info.ArgumentList.Add(a);
        info.ArgumentList.Add("--home");
        info.ArgumentList.Add(home);
        info.ArgumentList.Add("serve");
        info.Environment.Remove("STUDYSTASH_SELFTEST");

        Process p;
        try { p = spawn(info); }
        catch (Exception e) when (e is IOException or Win32Exception)
        {
            SetState(LibraryServiceState.Failed, e.Message);
            return;
        }
        p.EnableRaisingEvents = true;
        p.OutputDataReceived += (_, e) => LogLine(e.Data);
        p.ErrorDataReceived += (_, e) => LogLine(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        lock (gate) proc = p;
        try { File.WriteAllText(PidPath, p.Id.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        p.Exited += (_, _) => OnExited();

        bool healthy;
        try { healthy = await waitHealthy(cfg, TimeSpan.FromSeconds(30)); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { healthy = false; }
        if (p.HasExited) return; // OnExited already moved us past Starting
        if (!healthy)
        {
            SetState(LibraryServiceState.Failed, "It didn't answer in time." + LastLines());
            return;
        }
        SetState(LibraryServiceState.Running);
    }

    void LogLine(string? line)
    {
        if (line is null) return;
        lock (gate)
        {
            tail.Enqueue(line);
            while (tail.Count > 20) tail.Dequeue();
        }
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string LastLines()
    {
        lock (gate) return tail.Count == 0 ? "" : " " + string.Join(" / ", tail);
    }

    void RotateLogIfBig()
    {
        try
        {
            var f = new FileInfo(LogPath);
            if (f.Exists && f.Length >= Autostart.LogLimit) File.Move(LogPath, LogPath + ".1", overwrite: true);
        }
        catch (IOException) { }
    }

    void OnExited()
    {
        lock (gate)
        {
            proc = null;
            recentExits.Add(DateTime.UtcNow);
            recentExits.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(10));
        }
        try { File.Delete(PidPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (stopping || disposed) { SetState(LibraryServiceState.Stopped); return; }
        int count;
        lock (gate) count = recentExits.Count;
        if (count > MaxRestartsIn10Min)
        {
            SetState(LibraryServiceState.Failed, "It kept stopping." + LastLines());
            return;
        }
        int delay = RestartDelaysSeconds[Math.Min(count - 1, RestartDelaysSeconds.Length - 1)];
        log($"[library] stopped unexpectedly; trying again in {delay}s");
        SetState(LibraryServiceState.Starting);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(delay));
            if (!stopping && !disposed) await SpawnAsync();
        });
    }

    /// <summary>Stops the child we started or adopted; does nothing when the library is <see cref="LibraryServiceState.Elsewhere"/>
    /// or not running — we only ever stop a process we hold, never one just answering on the port.</summary>
    public async Task StopAsync()
    {
        stopping = true;
        Process? p;
        lock (gate) p = proc;
        if (p is null)
        {
            SetState(LibraryServiceState.Stopped);
            return;
        }
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Native.Kill(p.Id, Native.SigTerm);
                using var soft = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await p.WaitForExitAsync(soft.Token); }
                catch (OperationCanceledException) { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            }
            else p.Kill(entireProcessTree: true);
            await p.WaitForExitAsync();
        }
        catch (InvalidOperationException) { } // already gone
        lock (gate) proc = null;
        try { File.Delete(PidPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        SetState(LibraryServiceState.Stopped);
    }

    public void Dispose()
    {
        disposed = true;
        stopping = true;
        Process? p;
        lock (gate) p = proc;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}

static class Native
{
    public const int SigTerm = 15;

    [DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int sig);

    /// <summary>Best-effort: a process that's already gone just means there's nothing left to ask.</summary>
    public static void Kill(int pid, int sig)
    {
        try { kill(pid, sig); } catch (DllNotFoundException) { }
    }
}
