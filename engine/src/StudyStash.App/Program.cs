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
    /// <summary>Where settings, recordings and the model live (~/.study-stash, an older ~/.granola-share, or --home).</summary>
    public static string Home { get; private set; } = Configs.DefaultHome;

    /// <summary>Started at login: stay in the menu bar or tray, open no window.</summary>
    public static bool Background { get; private set; }

    static StreamWriter? logFile;

    public static void Log(string line)
    {
        try
        {
            logFile ??= new StreamWriter(new FileStream(Path.Combine(Home, "logs", "app.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            logFile.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}");
        }
        catch (IOException)
        {
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (StudyStash.Library.Cli.IsCommand(args)) return StudyStash.Library.Cli.RunAsync(args).GetAwaiter().GetResult();
        int at = Array.IndexOf(args, "--home");
        if (at >= 0 && at + 1 < args.Length) Home = Path.GetFullPath(Py.ExpandUser(args[at + 1]));
        Background = args.Contains("--background");
        Directory.CreateDirectory(Path.Combine(Home, "logs"));
        // One copy at a time: a second one asks the first to show itself, then goes.
        if (Desktop.HandOff(Home, args.Contains("--record") ? "record" : "show")) return 0;
        Skin.Current = Skin.FromEnvironment();
        Log($"[app] Study Stash {Engine.Version} starting ({Home})");
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new MacOSPlatformOptions { ShowInDock = false })
        .LogToTrace();
}
