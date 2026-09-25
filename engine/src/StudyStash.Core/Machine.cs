using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>This build's version: granola_share/__init__.py's, so both engines report the same one.</summary>
public static class Engine
{
    public static string Version { get; } =
        typeof(Engine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
}

/// <summary>What a command printed, and how it ended.</summary>
public sealed record ProcResult(int ExitCode, string Stdout);

/// <summary>Runs a command and reads what it prints, with no console window ever (Windows flashed one per call before).</summary>
public delegate ProcResult? Runner(string exe, IReadOnlyList<string> args, TimeSpan timeout);

/// <summary>Facts about this computer, each asked safely: null means it couldn't tell.</summary>
public static partial class Machine
{
    public static ProcResult? Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            // Something it started in the background may still hold its output open: don't wait for that forever.
            return new ProcResult(p.ExitCode, stdout.Wait(timeout) ? stdout.Result : "");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>A command that talks to the person in this terminal (an installer asking for a password): its exit
    /// code, or null when it didn't start.</summary>
    public static int? RunAttached(string exe, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Open a file the way double-clicking it would: on Windows an installer asks to run as administrator.</summary>
    public static void Open(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    /// <summary>os.getuid(): launchd names each person's services by it.</summary>
    public static string Uid()
    {
        if (OperatingSystem.IsWindows()) return "0";
        try
        {
            return GetUid().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return "0";
        }
    }

    /// <summary>os.access(dir, W_OK), by trying: can this account make files here?</summary>
    public static bool Writable(string dir)
    {
        string probe = Path.Combine(dir, $".study-stash-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>shutil.which: the command on PATH (with Windows' .exe and friends), or null.</summary>
    public static string? Which(string name)
    {
        string[] exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in exts.Prepend(""))
            {
                string candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate) && (OperatingSystem.IsWindows() || ext.Length > 0 || IsExecutable(candidate))) return candidate;
            }
        }
        return null;
    }

    static bool IsExecutable(string path) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    /// <summary>The name of this computer, as Python's socket.gethostname() gives it.</summary>
    public static string HostName() => System.Net.Dns.GetHostName();

    /// <summary>Memory, in GiB, as Python's total_ram_gb reports it.</summary>
    public static double? TotalRamGb()
    {
        long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return bytes > 0 ? bytes / (double)(1L << 30) : null;
    }

    /// <summary>Free space where Ollama keeps its models (your home folder), in GB.</summary>
    public static double? DiskFreeGb(string? path = null)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path ?? Py.UserHome()))!).AvailableFreeSpace / 1e9;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // --- sleep and the firewall (Ready changes them) -----------------------------------------------------------

    public static readonly Dictionary<string, string> SleepFix = new()
    {
        ["Darwin"] = "System Settings → Energy: turn on \"Prevent automatic sleeping when the display is off\"",
        ["Windows"] = "Settings → System → Power: when plugged in, put the device to sleep after Never",
    };

    /// <summary>platform.system(): "Darwin", "Windows", or "Linux".</summary>
    public static string Platform => OperatingSystem.IsMacOS() ? "Darwin" : OperatingSystem.IsWindows() ? "Windows" : "Linux";

    [GeneratedRegex(@"^\s*sleep\s+(\d+)", RegexOptions.Multiline)]
    private static partial Regex PmsetSleep();

    [GeneratedRegex(@"0x([0-9a-fA-F]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex HexValue();

    /// <summary>The Mac's sleep timer from `pmset -g` (0 = never sleeps).</summary>
    public static int? MacSleepMinutes(Runner? run = null)
    {
        var p = (run ?? Run)("pmset", ["-g"], TimeSpan.FromSeconds(10));
        var m = PmsetSleep().Match(p?.Stdout ?? "");
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Minutes before this PC sleeps while plugged in (0 = never), from powercfg.</summary>
    public static int? WindowsSleepMinutes(Runner? run = null)
    {
        var p = (run ?? Run)("powercfg", ["/query", "SCHEME_CURRENT", "SUB_SLEEP", "STANDBYIDLE"], TimeSpan.FromSeconds(10));
        // The labels are in the PC's language, but the last two values are always the plugged-in (AC) and battery
        // (DC) settings, in seconds.
        var values = HexValue().Matches(p?.Stdout ?? "").Select(m => m.Groups[1].Value).ToList();
        return values.Count >= 2 ? (int)(Convert.ToInt64(values[^2], 16) / 60) : null;
    }

    public static int? SleepMinutes(string? system = null) => (system ?? Platform) switch
    {
        "Darwin" => MacSleepMinutes(),
        "Windows" => WindowsSleepMinutes(),
        _ => null,
    };

    public const string FirewallRule = "Study Stash library";

    /// <summary>Windows: can your laptop get through Windows Firewall to the library's port? Null when it can't tell.</summary>
    public static bool? FirewallOpen(int port, Runner? run = null)
    {
        string ps = "if (-not @(Get-NetFirewallProfile | Where-Object { $_.Enabled }).Count) { 'off'; exit } "
            + $"$r = @(Get-NetFirewallRule -DisplayName '{FirewallRule}' -ErrorAction SilentlyContinue "
            + "| Where-Object { $_.Enabled -eq 'True' }); "
            + "if (-not $r.Count) { 'none' } else { ($r | Get-NetFirewallPortFilter).LocalPort -join ',' }";
        var p = (run ?? Run)("powershell", ["-NoProfile", "-NonInteractive", "-Command", ps], TimeSpan.FromSeconds(60));
        string output = Py.Strip(p?.Stdout);
        if (p is null || p.ExitCode != 0 || output.Length == 0) return null;
        return output == "off" || output.Split(',').Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
