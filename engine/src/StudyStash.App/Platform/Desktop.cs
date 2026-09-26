using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using Microsoft.Win32;
using StudyStash.Core;

namespace StudyStash.App.Platform;

/// <summary>The desktop around the app: the Dock (a Mac shows the app there only while a window is open), starting at
/// login, the taskbar button's progress (Windows), and one copy of the app at a time.</summary>
public static class Desktop
{
    // --- the Mac's Dock and app activation --------------------------------------------------------------------------

    [SupportedOSPlatform("macos")]
    static class ObjC
    {
        const string Lib = "/usr/lib/libobjc.A.dylib";

        [DllImport(Lib)]
        public static extern IntPtr objc_getClass(string name);

        [DllImport(Lib)]
        public static extern IntPtr sel_registerName(string name);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern bool SendLong(IntPtr receiver, IntPtr selector, long arg);

        [DllImport(Lib, EntryPoint = "objc_msgSend")]
        public static extern void SendBool(IntPtr receiver, IntPtr selector, bool arg);

        public static IntPtr App => Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
    }

    /// <summary>A Mac: show the app in the Dock and the ⌘Tab switcher (a window is open), or keep it to the menu bar.</summary>
    public static void ShowInDock(bool show)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            ObjC.SendLong(ObjC.App, ObjC.sel_registerName("setActivationPolicy:"), show ? 0 : 1); // regular : accessory
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>A Mac: bring the app forward, so the window it opened is in front of the one you were in.</summary>
    public static void Activate()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            ObjC.SendBool(ObjC.App, ObjC.sel_registerName("activateIgnoringOtherApps:"), true);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    // --- starting at login -------------------------------------------------------------------------------------------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Tests and the self-test: nothing here changes this computer. Starting at login refuses, and whether it
    /// starts at login reads "no" without looking. On when STUDYSTASH_SELFTEST is set; the app's tests turn it on as
    /// they load.</summary>
    internal static bool SystemChangesOff { get; set; } = Environment.GetEnvironmentVariable("STUDYSTASH_SELFTEST") is { Length: > 0 };

    /// <summary>The program to start: the app bundle's executable (a Mac), or StudyStash.exe.</summary>
    public static string Program => Environment.ProcessPath ?? "StudyStash";

    /// <summary>The login item's name for a settings folder: the usual folder is "com.study-stash.app" (a LaunchAgent)
    /// and "Study Stash" (the Run key); any other folder gets its own, so a second profile never replaces the real one.</summary>
    internal static (string Label, string RunValue) LoginName(string home)
    {
        if (SameFolder(home, Configs.DefaultHome)) return ("com.study-stash.app", "Study Stash");
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(home))))[..8];
        return ($"com.study-stash.app.{hash}", $"Study Stash ({hash})");
    }

    static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>What starts the app at login: quietly (--background), and with its folder when it isn't the usual one.</summary>
    internal static List<string> LoginArgs(string program, string home)
    {
        var args = new List<string> { program, "--background" };
        if (!SameFolder(home, Configs.DefaultHome)) args.AddRange(["--home", home]);
        return args;
    }

    /// <summary>The Run key's command that starts the app when you log in to Windows.</summary>
    internal static string RunCommand(string program, string home) =>
        $"\"{program}\" --background" + (SameFolder(home, Configs.DefaultHome) ? "" : $" --home \"{home}\"");

    /// <summary>The LaunchAgent that starts the app when you log in to a Mac.</summary>
    internal static string LaunchAgentPlist(string label, IEnumerable<string> args)
    {
        string items = string.Concat(args.Select(a => $"\n    <string>{SecurityElement.Escape(a)}</string>"));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{SecurityElement.Escape(label)}</string>
              <key>ProgramArguments</key>
              <array>{items}
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>ProcessType</key>
              <string>Interactive</string>
            </dict>
            </plist>
            """;
    }

    static string LaunchAgent(string label) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", label + ".plist");

    /// <summary>Whether the app starts when you log in, for this settings folder.</summary>
    public static bool StartsAtLogin(string home)
    {
        if (SystemChangesOff) return false;
        var (label, value) = LoginName(home);
        if (OperatingSystem.IsMacOS()) return File.Exists(LaunchAgent(label));
        if (OperatingSystem.IsWindows()) return Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(value) is string;
        return false;
    }

    /// <summary>Start (or stop starting) when you log in, quietly: to the menu bar or tray, no window. Only when the
    /// student turns it on.</summary>
    public static void StartAtLogin(bool on, string home)
    {
        if (SystemChangesOff) throw new InvalidOperationException("Start at login is off here.");
        var (label, value) = LoginName(home);
        if (OperatingSystem.IsMacOS())
        {
            string plist = LaunchAgent(label);
            if (!on)
            {
                File.Delete(plist);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
            File.WriteAllText(plist, LaunchAgentPlist(label, LoginArgs(Program, home)));
        }
        else if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(value, RunCommand(Program, home));
            else key.DeleteValue(value, throwOnMissingValue: false);
        }
    }

    // --- the taskbar button's progress (Windows) ------------------------------------------------------------------

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong done, ulong total);
        void SetProgressState(IntPtr hwnd, int state);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    class TaskbarList;

    static ITaskbarList3? taskbar;

    /// <summary>Windows: the bar under the app's taskbar button while the model downloads or a lecture transcribes
    /// (null clears it), red while <paramref name="blocked"/> says a problem is stopping a lecture being recorded.</summary>
    [SupportedOSPlatform("windows")]
    public static void TaskbarProgress(IntPtr hwnd, double? fraction, bool blocked = false)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (taskbar is null)
            {
                taskbar = (ITaskbarList3)new TaskbarList();
                taskbar.HrInit();
            }
            if (blocked)
            {
                taskbar.SetProgressState(hwnd, 4); // TBPF_ERROR
            }
            else if (fraction is double f)
            {
                taskbar.SetProgressState(hwnd, 2); // TBPF_NORMAL
                taskbar.SetProgressValue(hwnd, (ulong)Math.Round(Math.Clamp(f, 0, 1) * 1000), 1000);
            }
            else
            {
                taskbar.SetProgressState(hwnd, 0); // TBPF_NOPROGRESS
            }
        }
        catch (COMException)
        {
        }
    }

    // --- one copy at a time ------------------------------------------------------------------------------------------

    /// <summary>The lock each settings folder's copy holds while it runs. Kept here for the life of the process: a
    /// stream nobody refers to is collected, and that lets the lock go.</summary>
    static readonly Dictionary<string, FileStream> claims = [];

    internal static string PipeName(string home) => "StudyStash-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(home)))[..16];

    /// <summary>Be the one copy of the app for this settings folder: hold its app.lock until the app ends. False when
    /// another copy holds it.</summary>
    public static bool Claim(string home)
    {
        string path = Path.Combine(home, "app.lock");
        try
        {
            Directory.CreateDirectory(home);
            var lockFile = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            lock (claims)
            {
                if (claims.Remove(Path.GetFullPath(home), out var old)) old.Dispose();
                claims[Path.GetFullPath(home)] = lockFile;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Let the folder go (tests; the app keeps its claim until it ends).</summary>
    internal static void Release(string home)
    {
        lock (claims)
            if (claims.Remove(Path.GetFullPath(home), out var f)) f.Dispose();
    }

    /// <summary>Tell the copy already running (for this folder) to show itself, or to record. It may still be starting,
    /// so this keeps trying for a while. True when it heard.</summary>
    public static bool HandOff(string home, string message, TimeSpan? wait = null)
    {
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName(home), PipeDirection.Out);
                pipe.Connect((int)Math.Clamp((until - DateTime.UtcNow).TotalMilliseconds, 1, 1000));
                using var w = new StreamWriter(pipe);
                w.WriteLine(message);
                return true;
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= until) return false;
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>The words a later copy can hand off.</summary>
    static readonly HashSet<string> Words = ["show", "record"];

    /// <summary>Listen for later copies handing off (they say "show", or "record"); what they say is passed to
    /// <paramref name="onMessage"/> through <paramref name="post"/> (the UI thread, unless a test says otherwise).
    /// Anything else is ignored, and a copy that connects and says nothing is let go after 2 seconds.</summary>
    public static void Listen(string home, Action<string> onMessage, CancellationToken stop, Action<Action>? post = null)
    {
        post ??= a => Dispatcher.UIThread.Post(a);
        string name = PipeName(home);
        _ = Task.Run(async () =>
        {
            NamedPipeServerStream? next = null;
            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = next ?? Server(name);
                    next = null;
                    await pipe.WaitForConnectionAsync(stop);
                    // Listen again before reading this one: a copy that comes meanwhile waits its turn, not turned away.
                    next = Server(name);
                    if (await ReadWordAsync(pipe, stop) is { } word && Words.Contains(word)) post(() => onMessage(word));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    try
                    {
                        await Task.Delay(500, stop);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                finally
                {
                    pipe?.Dispose();
                }
            }
            next?.Dispose();
        }, CancellationToken.None);
    }

    static NamedPipeServerStream Server(string name) =>
        new(name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    /// <summary>The line a copy sent, or null when it said nothing within 2 seconds.</summary>
    static async Task<string?> ReadWordAsync(Stream pipe, CancellationToken stop)
    {
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(stop);
        quiet.CancelAfter(TimeSpan.FromSeconds(2));
        using var r = new StreamReader(pipe, leaveOpen: true);
        try
        {
            return (await r.ReadLineAsync(quiet.Token))?.Trim();
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>Starting the app when you log in, as the app sees it: <see cref="LoginItems.System"/> is the real thing
/// (<see cref="Desktop"/>); a test gives its own and counts the calls.</summary>
public interface ILoginItems
{
    bool StartsAtLogin(string home);
    void StartAtLogin(bool on, string home);
}

/// <summary>This computer's login items: a LaunchAgent on a Mac, the Run key on Windows.</summary>
public sealed class LoginItems : ILoginItems
{
    public static readonly LoginItems System = new();

    public bool StartsAtLogin(string home) => Desktop.StartsAtLogin(home);

    public void StartAtLogin(bool on, string home) => Desktop.StartAtLogin(on, home);
}
