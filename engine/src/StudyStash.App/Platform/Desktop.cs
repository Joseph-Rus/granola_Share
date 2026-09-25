using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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

    const string LoginLabel = "com.study-stash.app";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The program to start: the app bundle's executable (a Mac), or StudyStash.exe.</summary>
    public static string Program => Environment.ProcessPath ?? "StudyStash";

    static string LaunchAgent => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", LoginLabel + ".plist");

    public static bool StartsAtLogin()
    {
        if (OperatingSystem.IsMacOS()) return File.Exists(LaunchAgent);
        if (OperatingSystem.IsWindows()) return Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("Study Stash") is string;
        return false;
    }

    /// <summary>Start (or stop starting) when you log in, quietly: to the menu bar or tray, no window.</summary>
    public static void StartAtLogin(bool on, string home)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (!on)
            {
                File.Delete(LaunchAgent);
                return;
            }
            var args = new List<string> { Program, "--background" };
            if (home != Configs.DefaultHome) args.AddRange(["--home", home]);
            Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgent)!);
            string items = string.Concat(args.Select(a => $"\n    <string>{System.Security.SecurityElement.Escape(a)}</string>"));
            File.WriteAllText(LaunchAgent, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key>
                  <string>{LoginLabel}</string>
                  <key>ProgramArguments</key>
                  <array>{items}
                  </array>
                  <key>RunAtLoad</key>
                  <true/>
                  <key>ProcessType</key>
                  <string>Interactive</string>
                </dict>
                </plist>
                """);
        }
        else if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue("Study Stash", $"\"{Program}\" --background" + (home != Configs.DefaultHome ? $" --home \"{home}\"" : ""));
            else key.DeleteValue("Study Stash", throwOnMissingValue: false);
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

    /// <summary>Windows: the green bar under the app's taskbar button while a lecture transcribes (null clears it).</summary>
    [SupportedOSPlatform("windows")]
    public static void TaskbarProgress(IntPtr hwnd, double? fraction)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (taskbar is null)
            {
                taskbar = (ITaskbarList3)new TaskbarList();
                taskbar.HrInit();
            }
            if (fraction is double f)
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

    static string PipeName(string home) => "StudyStash-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(home)))[..16];

    /// <summary>Tell the copy already running (for this folder) to show itself. True when there was one.</summary>
    public static bool HandOff(string home, string message)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(home), PipeDirection.Out);
            pipe.Connect(300);
            using var w = new StreamWriter(pipe);
            w.WriteLine(message);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Listen for later copies handing off (they say "show", or "record").</summary>
    public static void Listen(string home, Action<string> onMessage, CancellationToken stop)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(PipeName(home), PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync(stop);
                    using var r = new StreamReader(pipe);
                    if (await r.ReadLineAsync(stop) is { } line) Avalonia.Threading.Dispatcher.UIThread.Post(() => onMessage(line.Trim()));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(500, CancellationToken.None);
                }
            }
        }, CancellationToken.None);
    }
}
