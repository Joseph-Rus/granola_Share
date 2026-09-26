using Avalonia;
using StudyStash.App.Platform;
using StudyStash.Core;

namespace StudyStash.App;

/// <summary>
/// Study Stash. With no command it's the app (in the menu bar or the tray); given one (serve, mcp, doctor, ...) it's
/// the engine, so the library's service and Claude's MCP server run from the same program.
/// </summary>
static class Program
{
    /// <summary>Where settings, recordings and the model live (~/.granola-share, or --home).</summary>
    public static string Home { get; private set; } = Configs.DefaultHome;

    /// <summary>Started at login: stay in the menu bar or tray, open no window.</summary>
    public static bool Background { get; private set; }

    static RollingLog? log;

    /// <summary>A line in logs/app.log (from any thread).</summary>
    public static void Log(string line) =>
        LazyInitializer.EnsureInitialized(ref log, () => new RollingLog(Path.Combine(Home, "logs", "app.log"))).Write(line);

    [STAThread]
    public static int Main(string[] args)
    {
        if (StudyStash.Library.Cli.IsCommand(args)) return StudyStash.Library.Cli.RunAsync(args).GetAwaiter().GetResult();
        int at = Array.IndexOf(args, "--home");
        if (at >= 0 && at + 1 < args.Length) Home = Path.GetFullPath(Py.ExpandUser(args[at + 1]));
        Background = args.Contains("--background");
        Directory.CreateDirectory(Path.Combine(Home, "logs"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"[crash] {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log($"[error] a background task failed: {e.Exception}");
            e.SetObserved();
        };
        // One copy at a time: a second one asks the first to show itself (or record), then goes. One started at login
        // has nothing to show, so it just goes.
        if (!Desktop.Claim(Home))
        {
            string? word = args.Contains("--record") ? "record" : Background ? null : "show";
            Log(word is null ? "[app] Study Stash is already running for this folder; this copy, started at login, goes"
                : Desktop.HandOff(Home, word) ? $"[app] Study Stash is already running for this folder: handed it \"{word}\""
                : "[app] Study Stash is already running for this folder but didn't answer in 5 seconds; this copy goes");
            return 0;
        }
        Skin.Current = Skin.FromEnvironment();
        Log($"[app] Study Stash {Engine.Version} starting ({Home})");
        int code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        Log($"[app] quit ({code})");
        return code;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new MacOSPlatformOptions { ShowInDock = false })
        .LogToTrace();
}

/// <summary>
/// A log file that several threads (and a second copy handing off) write to, one whole line at a time. Past
/// <see cref="Limit"/> bytes it becomes app.log.1 and a new one starts, so it never fills the disk.
/// </summary>
sealed class RollingLog(string path, long limit = RollingLog.Limit)
{
    public const long Limit = 5 * 1024 * 1024;

    readonly Lock gate = new();
    StreamWriter? file;

    public void Write(string line)
    {
        lock (gate)
        {
            try
            {
                if (file is null || file.BaseStream.Length > limit)
                {
                    file?.Dispose();
                    file = null;
                    Roll();
                    // Delete lets another copy roll the file while this one has it open (Windows).
                    file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1)) { AutoFlush = true };
                }
                // To the end as it is now: another copy (handing off) may have written since, and a stream only knows
                // where it last wrote.
                file.BaseStream.Seek(0, SeekOrigin.End);
                file.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // Nowhere to say it: a full disk or a folder gone. The app carries on, and tries a new file next time.
                try
                {
                    file?.Dispose();
                }
                catch (IOException)
                {
                }
                file = null;
            }
        }
    }

    void Roll()
    {
        try
        {
            if (new FileInfo(path) is { Exists: true } f && f.Length > limit) File.Move(path, path + ".1", overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another copy has it open without letting it go (an older version); roll next time.
        }
    }
}
