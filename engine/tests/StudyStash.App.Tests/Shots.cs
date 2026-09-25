using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using StudyStash.App.ViewModels;
using StudyStash.App.Views;

[assembly: AvaloniaTestApplication(typeof(StudyStash.App.Tests.TestApp))]

namespace StudyStash.App.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<StudyStash.App.App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Every surface drawn as the design shows it (on its ground, 64 px in), light and dark, into STUDYSTASH_SHOTS (or
/// shots/ beside the tests): to compare with the design, and to see the Windows look from CI.
/// </summary>
public static class Shot
{
    public static string Dir
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("STUDYSTASH_SHOTS") is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "shots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static Bitmap Take(string name, SkinKind skin, ThemeVariant theme, Func<Control> build, double width, double height)
    {
        ((StudyStash.App.App)Application.Current!).UseSkin(skin);
        var content = build();
        content.HorizontalAlignment = HorizontalAlignment.Left;
        content.VerticalAlignment = VerticalAlignment.Top;
        // Top-aligned, like the design's canvas: a surface taller than the window runs off the bottom, not up.
        var ground = new Border
        {
            Padding = new Thickness(64), Child = content, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
            MinHeight = height + 400, MinWidth = width + 400,
        };
        ground.Bind(Border.BackgroundProperty, ground.GetResourceObservable("Ground"));
        var window = new Window { Width = width + 400, Height = height + 400, RequestedThemeVariant = theme, Content = ground };
        Views.Look.Apply(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var whole = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("nothing rendered");
        // The design's canvas size: a window with room to spare, cut to it, so nothing is squeezed.
        var frame = new RenderTargetBitmap(new PixelSize((int)width, (int)height));
        using (var ctx = frame.CreateDrawingContext())
            ctx.DrawImage(whole, new Rect(0, 0, width, height), new Rect(0, 0, width, height));
        using (var file = File.Create(Path.Combine(Dir, $"{name}-{(theme == ThemeVariant.Dark ? "dark" : "light")}.png"))) frame.Save(file, PngBitmapEncoderOptions.Default);
        window.Close();
        return frame;
    }

    public static StackPanel Side(params Control[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 56, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var c in items) p.Children.Add(c);
        return p;
    }
}

public class SurfaceShots
{
    static readonly ThemeVariant[] Themes = [ThemeVariant.Light, ThemeVariant.Dark];

    static SurfaceShots() => Environment.SetEnvironmentVariable("STUDYSTASH_STILL", "1");

    [AvaloniaFact]
    public void Win_flyout()
    {
        foreach (var t in Themes)
            Shot.Take("win-flyout", SkinKind.Win, t, () => Shot.Side(
                new WinPanel { DataContext = Demo.Panel(recording: false), VerticalAlignment = VerticalAlignment.Top },
                new WinPanel { DataContext = Demo.Panel(recording: true), VerticalAlignment = VerticalAlignment.Top }), 900, 780);
    }

    static StackPanel Recorders(Func<RecorderModel, Control> view)
    {
        var pills = new StackPanel { Spacing = 32, VerticalAlignment = VerticalAlignment.Top };
        pills.Children.Add(view(Demo.Recorder()));
        pills.Children.Add(view(Demo.Recorder(paused: true)));
        return Shot.Side(pills, view(Demo.Recorder(expanded: true)));
    }

    [AvaloniaFact]
    public void Mac_recorder()
    {
        foreach (var t in Themes) Shot.Take("mac-recorder", SkinKind.Mac, t, () => Recorders(m => new MacRecorder { DataContext = m, VerticalAlignment = VerticalAlignment.Top }), 860, 640);
    }

    [AvaloniaFact]
    public void Win_recorder()
    {
        foreach (var t in Themes) Shot.Take("win-recorder", SkinKind.Win, t, () => Recorders(m => new WinRecorder { DataContext = m, VerticalAlignment = VerticalAlignment.Top }), 860, 648);
    }

    static StackPanel Quick(Func<QuickModel, Control> view)
    {
        var p = new StackPanel { Spacing = 48, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        p.Children.Add(view(Demo.Quick(answer: false)));
        p.Children.Add(view(Demo.Quick(answer: true)));
        return p;
    }

    [AvaloniaFact]
    public void Mac_quick()
    {
        foreach (var t in Themes) Shot.Take("mac-quick", SkinKind.Mac, t, () => Quick(m => new MacQuick { DataContext = m }), 800, 1180);
    }

    [AvaloniaFact]
    public void Win_quick()
    {
        foreach (var t in Themes) Shot.Take("win-quick", SkinKind.Win, t, () => Quick(m => new WinQuick { DataContext = m }), 800, 1220);
    }

    [AvaloniaFact]
    public void Mac_app()
    {
        foreach (var t in Themes) Shot.Take("mac-app", SkinKind.Mac, t, () => new MacLibrary { DataContext = Demo.Library(), Width = 1280, Height = 800 }, 1400, 928);
    }

    [AvaloniaFact]
    public void Win_app()
    {
        foreach (var t in Themes) Shot.Take("win-app", SkinKind.Win, t, () => new WinLibrary { DataContext = Demo.Library(), Width = 1280, Height = 800 }, 1400, 928);
    }

    [AvaloniaFact]
    public void Mac_setup()
    {
        foreach (var t in Themes) Shot.Take("mac-setup", SkinKind.Mac, t, () => new MacSetup { DataContext = Demo.Setup(SkinKind.Mac), DrawChrome = true }, 850, 608);
    }

    [AvaloniaFact]
    public void Win_setup()
    {
        foreach (var t in Themes) Shot.Take("win-setup", SkinKind.Win, t, () => new WinSetup { DataContext = Demo.Setup(SkinKind.Win), DrawChrome = true }, 850, 608);
    }

    /// <summary>The steps the design doesn't show, in both looks.</summary>
    [AvaloniaFact]
    public void Setup_steps()
    {
        foreach (var skin in new[] { SkinKind.Mac, SkinKind.Win })
            foreach (var step in new[] { SetupStep.Microphone, SetupStep.Library, SetupStep.Classes })
                Shot.Take($"{(skin == SkinKind.Mac ? "mac" : "win")}-setup-{step.ToString().ToLowerInvariant()}", skin, ThemeVariant.Light, () =>
                {
                    var m = SetupModel.For(skin);
                    m.Go(step);
                    m.Address = "http://mac-mini:8787";
                    m.Classes.Add(new SetupClass { Name = "CS 101", When = "Tue Thu 10:00–11:15", Dot = Skin.ClassDot(0) });
                    m.Classes.Add(new SetupClass { Name = "BIO 110", When = "Tue 11:00–12:30", Dot = Skin.ClassDot(1) });
                    return skin == SkinKind.Mac ? new MacSetup { DataContext = m, DrawChrome = true } : new WinSetup { DataContext = m, DrawChrome = true };
                }, 850, 608);
    }

    [AvaloniaFact]
    public void Mac_dropdown()
    {
        foreach (var t in Themes)
            Shot.Take("mac-dropdown", SkinKind.Mac, t, () => Shot.Side(
                new MacPanel { DataContext = Demo.Panel(recording: false), VerticalAlignment = VerticalAlignment.Top },
                new MacPanel { DataContext = Demo.Panel(recording: true), VerticalAlignment = VerticalAlignment.Top }), 900, 700);
    }
}
