using System.Diagnostics;
using StudyStash.App.Services;
using StudyStash.Audio;
using StudyStash.Core;

namespace StudyStash.App.Tests;

[Collection(nameof(EnvironmentTests))]
public class AppHostTests
{
    static AppHost Host(string home, Func<IAudioSource>? mic = null) => new(home, mic, log: _ => { }, loginItems: new CountingLoginItems());

    static string Wav(TempHome home)
    {
        string path = home["speech.wav"];
        using var w = new WavWriter(path);
        w.Write(Enumerable.Range(0, Sound.Rate / 2).Select(i => (float)Math.Sin(i / 10.0) * 0.3f).ToArray());
        return path;
    }

    [Fact]
    public void A_sound_file_stands_in_for_the_microphone()
    {
        using var home = new TempHome();
        string before = Environment.GetEnvironmentVariable("STUDYSTASH_MIC_FILE") ?? "";
        Environment.SetEnvironmentVariable("STUDYSTASH_MIC_FILE", Wav(home));
        try
        {
            using var host = Host(home.Path);
            Assert.True(host.PretendMic);
            Assert.Equal(MicAccess.Allowed, host.MicAccess());
            host.AskMic();
            using var mic = host.OpenMic();
            Assert.IsType<FileMicrophone>(mic);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STUDYSTASH_MIC_FILE", before.Length > 0 ? before : null);
        }

        // Without one (and no file named), it's the real microphone: not opened here, only told apart.
        if (before.Length == 0)
        {
            using var real = Host(home.Path);
            Assert.False(real.PretendMic);
        }
    }

    [Fact]
    public void A_file_that_isnt_there_is_not_a_microphone()
    {
        using var home = new TempHome();
        string before = Environment.GetEnvironmentVariable("STUDYSTASH_MIC_FILE") ?? "";
        Environment.SetEnvironmentVariable("STUDYSTASH_MIC_FILE", home["nothing.wav"]);
        try
        {
            Assert.Null(AppHost.MicFromEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable("STUDYSTASH_MIC_FILE", before.Length > 0 ? before : null);
        }
    }

    [Fact]
    public void A_microphone_given_is_the_one_recorded_from()
    {
        using var home = new TempHome();
        var given = new FileMicrophone(Wav(home));
        using var host = Host(home.Path, () => given);
        Assert.True(host.PretendMic);
        Assert.Same(given, host.OpenMic());
        Assert.Equal(MicAccess.Allowed, host.MicAccess());
    }

    [Fact]
    public void Settings_that_cant_be_saved_are_said_not_thrown()
    {
        using var home = new TempHome();
        using var host = Host(home.Path);
        // app.json's temporary file can't be written where a folder is.
        Directory.CreateDirectory(home["app.json.tmp"]);
        Directory.CreateDirectory(home["timetable.json"]);
        List<string> said = [];
        host.Problem += (title, _) => said.Add(title);

        host.Save(s => s.Language = "fr");
        host.SaveTimetable(new Timetable());

        Assert.Equal("fr", host.Settings.Language);
        Assert.Equal(["Your settings couldn't be saved", "Your timetable couldn't be saved"], said);
    }

    [Fact]
    public void Stopping_waits_for_the_work_and_happens_once()
    {
        using var home = new TempHome();
        var host = Host(home.Path);
        host.Start();
        var took = Stopwatch.StartNew();
        host.Dispose();
        Assert.True(took.Elapsed < TimeSpan.FromSeconds(3.5), $"took {took.Elapsed}");
        host.Dispose();
    }
}
