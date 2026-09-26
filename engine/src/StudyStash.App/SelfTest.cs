using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using StudyStash.Core;

namespace StudyStash.App;

/// <summary>
/// STUDYSTASH_SELFTEST=&lt;folder&gt;: the app tries itself, in its real windows. It opens each surface and saves a picture
/// of it there, records a lecture with the pretend microphone (STUDYSTASH_MIC_FILE), follows it through Whisper to
/// the library, and writes what happened to selftest.txt; then it quits. For CI, and for trying a build.
/// </summary>
public static class SelfTest
{
    public static string? Dir => Environment.GetEnvironmentVariable("STUDYSTASH_SELFTEST") is { Length: > 0 } d ? d : null;

    static readonly List<string> said = [];

    static void Say(string line)
    {
        said.Add(line);
        Program.Log("[selftest] " + line);
    }

    static void Shot(Window? w, string name)
    {
        if (w is null || !w.IsVisible)
        {
            Say($"{name}: not showing");
            return;
        }
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(w.Bounds.Width)), Math.Max(1, (int)Math.Ceiling(w.Bounds.Height)));
        using var bmp = new RenderTargetBitmap(new PixelSize(size.Width * 2, size.Height * 2), new Vector(192, 192));
        bmp.Render(w);
        using var f = File.Create(Path.Combine(Dir!, name + ".png"));
        bmp.Save(f, PngBitmapEncoderOptions.Default);
        Say($"{name}: {w.Bounds.Width:0}×{w.Bounds.Height:0} at {w.Position}");
    }

    static async Task Wait(double seconds) => await Task.Delay(TimeSpan.FromSeconds(seconds));

    static async Task<bool> Until(Func<bool> done, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until) await Task.Delay(250);
        return done();
    }

    public static void Run() => Dispatcher.UIThread.Post(async () =>
    {
        Directory.CreateDirectory(Dir!);
        try
        {
            await Script();
            Say("ok");
        }
        catch (Exception e)
        {
            Say($"failed: {e}");
        }
        await File.WriteAllLinesAsync(Path.Combine(Dir!, "selftest.txt"), said);
        Shell.Quit();
    });

    static async Task Script()
    {
        var host = Shell.Host;
        Say($"skin {Skin.Current}, model ready {host.ModelReady}, library {host.Library}");
        await Wait(1.5);
        Shot(Shell.Windows.Setup, "setup");
        host.Save(s => s.SetupDone = true);
        Shell.Windows.Setup?.Close();

        Shell.ShowLibrary();
        await Until(() => host.Library != Services.LibraryState.NotSetUp || host.Client().ServerUrl.Length == 0, 10);
        await Wait(2);
        Shot(Shell.Windows.Main, "library");
        if (Shell.HasDue)
        {
            await Shell.ShowDuePublic();
            await Wait(2);
            Shot(Shell.Windows.Main, "library-due");
        }

        // STUDYSTASH_SELFTEST_ASK: ask the library's AI in the full app, and picture the answer.
        if (Environment.GetEnvironmentVariable("STUDYSTASH_SELFTEST_ASK") is { Length: > 0 } question)
        {
            var asking = Shell.AskForSelfTest(question);
            bool answered = await Until(() => asking.IsCompleted, 120);
            await Wait(1);
            Say(answered ? $"answered: {Py.Head(Shell.AnswerForSelfTest ?? "", 120)}" : "no answer in 120 s");
            Shot(Shell.Windows.Main, "library-answer");
        }

        Shell.Windows.TogglePanel();
        await Wait(1);
        Shot(Shell.Windows.Panel, "panel-idle");
        Shell.Windows.Panel?.Hide();

        Shell.Windows.ToggleQuick();
        await Wait(1);
        Shot(Shell.Windows.Quick, "quick");
        Shell.Windows.Quick?.Hide();

        Shell.ShowSettings();
        await Wait(1);
        Shot(Shell.Windows.Settings, "settings");
        foreach (string section in new[] { "AI", "Canvas" })
        {
            if ((Shell.Windows.Settings?.Content as Control)?.DataContext is not Services.SettingsModel sm) break;
            sm.Section = section;
            await Wait(2);
            Shot(Shell.Windows.Settings, "settings-" + section.ToLowerInvariant());
        }
        Shell.Windows.Settings?.Close();

        if (!host.ModelReady)
        {
            Say("no model: recording skipped");
            return;
        }
        Shell.Windows.Record();
        var live = host.Recorder.Current;
        Say(live is null ? "recording didn't start" : $"recording {live.Id}");
        if (live is null) return;
        await Wait(2);
        Shot(Shell.Windows.Recorder, "recorder-pill");
        Shell.Windows.Panel?.Hide();
        Shell.Windows.TogglePanel();
        await Wait(1);
        Shot(Shell.Windows.Panel, "panel-recording");
        Shell.Windows.Panel?.Hide();
        // Whisper takes a piece every 20 to 30 seconds while it records.
        bool heard = await Until(() => host.Lectures.Get(live.Id)?.Segments.Count > 0, 60);
        Say(heard ? $"heard: {host.Lectures.Get(live.Id)!.Segments[0].Text}" : "heard nothing in 60 s");
        Shell.Windows.ShowRecorder(expanded: true);
        await Wait(1);
        Shot(Shell.Windows.Recorder, "recorder-expanded");
        Shell.Windows.StopRecording();
        bool done = await Until(() => host.Lectures.Get(live.Id)?.State is LectureState.Sending or LectureState.Writing or LectureState.Filed or LectureState.Failed, 120);
        var l = host.Lectures.Get(live.Id);
        Say($"after stopping: {l?.State} ({l?.Segments.Count} lines, {TimedText.Clock(l?.Seconds ?? 0)})");
        if (done && host.Client().ServerUrl.Length > 0)
        {
            bool filed = await Until(() => host.Lectures.Get(live.Id)?.State is LectureState.Filed or LectureState.Failed, 90);
            Say($"library: {host.Lectures.Get(live.Id)?.State} in '{host.Lectures.Get(live.Id)?.FiledClass}'");
        }
        Shell.Windows.TogglePanel();
        await Wait(1);
        Shot(Shell.Windows.Panel, "panel-after");
    }
}
