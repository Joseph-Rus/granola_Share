using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>
/// Native yes/no popups and notifications (dialogs.py). A Mac uses osascript, Windows user32's MessageBox, Linux
/// zenity. AskYesNo answers true (share), false (skip) or null (nobody answered in time).
/// </summary>
public static partial class Dialogs
{
    public const string AccessibilitySettings = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";

    static readonly TimeSpan Forever = TimeSpan.FromDays(1);

    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public static string BuildOsascript(string title, string text, string yes, string no, int timeout) =>
        $"display dialog \"{Esc(text)}\" with title \"{Esc(title)}\" buttons {{\"{Esc(no)}\", \"{Esc(yes)}\"}} "
        + $"default button \"{Esc(yes)}\" with icon note giving up after {timeout}";

    [GeneratedRegex(@"button returned:([^,\n]*)")]
    private static partial Regex ButtonReturned();

    public static bool? ParseOsascript(string output, string yes)
    {
        if (output.Contains("gave up:true", StringComparison.Ordinal)) return null;
        var m = ButtonReturned().Match(output);
        return m.Success && Py.Strip(m.Groups[1].Value) == yes;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxTimeoutW")]
    private static extern int MessageBoxTimeout(IntPtr hwnd, string text, string caption, uint type, ushort language, uint milliseconds);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);

    static bool? AskWindows(string title, string text, int timeout)
    {
        const uint flags = 0x4 | 0x20 | 0x10000 | 0x40000; // MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND | MB_TOPMOST
        int r;
        try
        {
            r = MessageBoxTimeout(IntPtr.Zero, text, title, flags, 0, (uint)(timeout * 1000));
            if (r == 32000) return null; // MB_TIMEDOUT
        }
        catch (EntryPointNotFoundException)
        {
            r = MessageBox(IntPtr.Zero, text, title, flags);
        }
        return r == 6; // IDYES
    }

    public static bool? AskYesNo(string title, string text, string yes = "Share", string no = "Skip", int timeout = 300,
        Runner? run = null, string? system = null)
    {
        run ??= Machine.Run;
        system ??= Machine.Platform;
        if (system == "Darwin")
        {
            var p = run("osascript", ["-e", BuildOsascript(title, text, yes, no, timeout)], Forever);
            if (p is not { ExitCode: 0 }) return false; // closed or cancelled
            return ParseOsascript(p.Stdout, yes);
        }
        if (system == "Windows") return OperatingSystem.IsWindows() ? AskWindows(title, $"{text}\n\nYes = {yes}, No = {no}", timeout) : false;
        if (Machine.Which("zenity") is not null)
        {
            var p = run("zenity", ["--question", $"--title={title}", $"--text={text}", $"--ok-label={yes}", $"--cancel-label={no}", $"--timeout={timeout}"], Forever);
            return p?.ExitCode == 5 ? null : p?.ExitCode == 0;
        }
        return null; // no way to ask on this desktop: no answer, so the lecture waits and is asked about again
    }

    /// <summary>A dialog with up to three buttons: the label clicked, or null if nobody answered in time. Only a Mac draws
    /// three buttons; elsewhere the first and the default button are offered.</summary>
    public static string? AskChoice(string title, string text, IReadOnlyList<string> buttons, string? preferred = null, int timeout = 300,
        Runner? run = null, string? system = null)
    {
        run ??= Machine.Run;
        system ??= Machine.Platform;
        string chosen = preferred ?? buttons[^1];
        if (system == "Darwin")
        {
            string labels = string.Join(", ", buttons.Select(b => $"\"{Esc(b)}\""));
            string script = $"display dialog \"{Esc(text)}\" with title \"{Esc(title)}\" buttons {{{labels}}} "
                + $"default button \"{Esc(chosen)}\" with icon note giving up after {timeout}";
            var p = run("osascript", ["-e", script], Forever);
            if (p is not { ExitCode: 0 } || p.Stdout.Contains("gave up:true", StringComparison.Ordinal)) return null;
            var m = ButtonReturned().Match(p.Stdout);
            return m.Success ? Py.Strip(m.Groups[1].Value) : null;
        }
        string other = buttons[0] != chosen ? buttons[0] : buttons[^1];
        bool? answer = AskYesNo(title, text, chosen, other, timeout, run, system);
        return answer is null ? null : answer.Value ? chosen : other;
    }

    /// <summary>Open a web page or a System Settings pane with the system's default handler.</summary>
    public static void OpenUrl(string url, Runner? run = null, string? system = null)
    {
        run ??= Machine.Run;
        system ??= Machine.Platform;
        try
        {
            if (system == "Darwin") run("open", [url], TimeSpan.FromSeconds(30));
            else if (system == "Windows") Machine.Open(url);
            else run("xdg-open", [url], TimeSpan.FromSeconds(30));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    /// <summary>Browsers that can show a page as its own app window (no tabs or address bar), best first. Every Windows 10
    /// and 11 has Edge.</summary>
    public static List<string> AppBrowsers(string? system = null)
    {
        system ??= Machine.Platform;
        if (system == "Windows")
        {
            var roots = new[] { "ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA" }.Select(Environment.GetEnvironmentVariable).OfType<string>().Where(r => r.Length > 0);
            string[][] subs = [["Microsoft", "Edge", "Application", "msedge.exe"], ["Google", "Chrome", "Application", "chrome.exe"]];
            return subs.SelectMany(sub => roots.Select(r => Path.Combine([r, .. sub]))).Distinct().Where(File.Exists).ToList();
        }
        if (system == "Linux")
            return new[] { "google-chrome", "chromium", "chromium-browser", "microsoft-edge", "brave-browser" }.Select(Machine.Which).OfType<string>().ToList();
        return [];
    }

    /// <summary>Show a page in its own window, like an app (Windows and Linux; the Mac has the real Study Stash app).
    /// Falls back to a browser tab. True when it opened as a window.</summary>
    public static bool OpenWindow(string url, Func<IReadOnlyList<string>, bool>? spawn = null, string? system = null)
    {
        system ??= Machine.Platform;
        spawn ??= Spawn;
        foreach (string exe in AppBrowsers(system))
            if (spawn([exe, $"--app={url}", "--window-size=1180,820"])) return true;
        OpenUrl(url, system: system);
        return false;
    }

    static bool Spawn(IReadOnlyList<string> args)
    {
        try
        {
            // Through the shell: no pipes a browser could fill and stall on, and nothing of this service's inherited.
            var psi = new ProcessStartInfo(args[0]) { UseShellExecute = true };
            foreach (string a in args.Skip(1)) psi.ArgumentList.Add(a);
            using var _ = Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Bring an app to the front (a Mac), or start one from its .exe (Windows: the app brings its own window
    /// forward when it's already open).</summary>
    public static bool OpenApp(string name, Runner? run = null, string? system = null, Action<string>? start = null)
    {
        system ??= Machine.Platform;
        if (system == "Windows")
        {
            try
            {
                (start ?? Machine.Open)(name);
                return true;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }
        if (system != "Darwin") return false;
        return (run ?? Machine.Run)("open", ["-a", name], TimeSpan.FromSeconds(30)) is { ExitCode: 0 };
    }

    /// <summary>A notification, best effort and not waited on for long.</summary>
    public static void Notify(string title, string text, Runner? run = null, string? system = null)
    {
        run ??= Machine.Run;
        system ??= Machine.Platform;
        if (system == "Darwin") run("osascript", ["-e", $"display notification \"{Esc(text)}\" with title \"{Esc(title)}\""], TimeSpan.FromSeconds(30));
        else if (system == "Linux" && Machine.Which("notify-send") is not null) run("notify-send", [title, text], TimeSpan.FromSeconds(30));
    }
}
