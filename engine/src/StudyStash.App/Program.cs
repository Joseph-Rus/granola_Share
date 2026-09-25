using Avalonia;

namespace StudyStash.App;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Skin.Current = Skin.FromEnvironment();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
