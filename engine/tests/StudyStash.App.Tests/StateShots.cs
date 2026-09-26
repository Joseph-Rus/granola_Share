using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using StudyStash.App.ViewModels;
using StudyStash.App.Views;

namespace StudyStash.App.Tests;

/// <summary>Setup states the static design doesn't show: hearing the microphone, and a library "Find it" turned up.</summary>
public class StateShots
{
    static readonly ThemeVariant[] Themes = [ThemeVariant.Light, ThemeVariant.Dark];
    static readonly double[] Wave =
    [
        0.2, 0.5, 0.8, 0.3, 0.6, 0.9, 0.4, 0.7, 0.5, 0.2, 0.6, 0.8, 0.3, 0.5, 0.7, 0.4, 0.6, 0.9, 0.2, 0.5, 0.7, 0.3, 0.6, 0.4,
    ];

    static Control View(SkinKind skin, SetupModel m) =>
        skin == SkinKind.Mac ? new MacSetup { DataContext = m, DrawChrome = true } : new WinSetup { DataContext = m, DrawChrome = true };

    [AvaloniaFact]
    public void Setup_microphone_check()
    {
        foreach (var skin in new[] { SkinKind.Mac, SkinKind.Win })
            foreach (var t in Themes)
                Shot.Take($"{(skin == SkinKind.Mac ? "mac" : "win")}-setup-microphone-check", skin, t, () =>
                {
                    var m = SetupModel.For(skin);
                    m.MicAllowed = true;
                    m.MicLevels = Wave;
                    m.MicHeard = true;
                    return View(skin, m);
                }, 850, 608);
    }

    [AvaloniaFact]
    public void Setup_library_found()
    {
        foreach (var skin in new[] { SkinKind.Mac, SkinKind.Win })
            foreach (var t in Themes)
                Shot.Take($"{(skin == SkinKind.Mac ? "mac" : "win")}-setup-library-found", skin, t, () =>
                {
                    var m = SetupModel.For(skin);
                    m.Go(SetupStep.Library);
                    m.LibraryResult = "Found mac-mini on your Tailscale network. Type its password.";
                    return View(skin, m);
                }, 850, 608);
    }

    [AvaloniaFact]
    public void Setup_library_this_computer()
    {
        foreach (var skin in new[] { SkinKind.Mac, SkinKind.Win })
            foreach (var t in Themes)
                Shot.Take($"{(skin == SkinKind.Mac ? "mac" : "win")}-setup-library-this-computer", skin, t, () =>
                {
                    var m = SetupModel.For(skin);
                    m.Go(SetupStep.Library);
                    m.PickThisCommand.Execute(null);
                    m.LibraryName = "Ada's library";
                    return View(skin, m);
                }, 850, 608);
    }
}
