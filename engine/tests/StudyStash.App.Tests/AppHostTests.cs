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
    public void The_sound_file_plays_as_fast_as_asked()
    {
        Assert.Equal(1, AppHost.MicSpeed(null));
        Assert.Equal(1, AppHost.MicSpeed(""));
        Assert.Equal(4, AppHost.MicSpeed("4"));
        Assert.Equal(8, AppHost.MicSpeed("20"));
        Assert.Equal(1, AppHost.MicSpeed("0"));
        Assert.Equal(1, AppHost.MicSpeed("fast"));

        using var home = new TempHome();
        string file = Environment.GetEnvironmentVariable("STUDYSTASH_MIC_FILE") ?? "", speed = Environment.GetEnvironmentVariable("STUDYSTASH_MIC_SPEED") ?? "";
        Environment.SetEnvironmentVariable("STUDYSTASH_MIC_FILE", Wav(home));
        Environment.SetEnvironmentVariable("STUDYSTASH_MIC_SPEED", "4");
        try
        {
            using var host = Host(home.Path);
            using var mic = host.OpenMic();
            Assert.Equal(4, Assert.IsType<FileMicrophone>(mic).Speed);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STUDYSTASH_MIC_FILE", file.Length > 0 ? file : null);
            Environment.SetEnvironmentVariable("STUDYSTASH_MIC_SPEED", speed.Length > 0 ? speed : null);
        }
    }

    /// <summary>Whisper that writes one line per piece.</summary>
    sealed class OneLine : ITranscriber
    {
        public Task<Transcription> TranscribeAsync(float[] samples, string prompt, string language, CancellationToken stop) =>
            Task.FromResult(new Transcription([new Spoken(0, 1, "The midterm is on recursion.")], "en"));

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Whisper_that_wont_start_is_said_and_a_failed_lecture_goes_again()
    {
        using var home = new TempHome();
        bool broken = true;
        using var host = new AppHost(home.Path, () => new FileMicrophone(Wav(home)),
            () => broken ? throw new InvalidOperationException("the model file is damaged") : new OneLine(), log: _ => { },
            loginItems: new CountingLoginItems());
        int changes = 0;
        host.Changed += () => Interlocked.Increment(ref changes);
        File.Copy(Wav(home), host.Lectures.AudioPath("rec-1"));
        host.Lectures.Add(new Lecture { Id = "rec-1", Started = "2026-09-22T10:00:00-07:00", State = LectureState.Transcribing, Seconds = 0.5 });

        Assert.False(await host.Whisper.StepAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Whisper couldn't start: the model file is damaged", host.WhisperProblem);
        Assert.True(changes > 0); // the windows hear of it
        Assert.Equal(LectureState.Transcribing, host.Lectures.Get("rec-1")!.State);

        broken = false;
        host.Whisper.Wake();
        Assert.True(await host.Whisper.StepAsync(TestContext.Current.CancellationToken));
        Assert.Null(host.WhisperProblem);
        Assert.Equal(LectureState.Sending, host.Lectures.Get("rec-1")!.State);

        // A lecture that failed goes again from the dropdown.
        host.Lectures.Update("rec-1", x =>
        {
            x.State = LectureState.Failed;
            x.Error = "Whisper couldn't write part of it down: out of memory";
        });
        host.Retry("rec-1");
        Assert.Equal(LectureState.Sending, host.Lectures.Get("rec-1")!.State);
        Assert.Equal("", host.Lectures.Get("rec-1")!.Error);
    }

    [Fact]
    public async Task The_watchdog_runs_every_second_and_pauses_a_microphone_that_gives_only_silence()
    {
        using var home = new TempHome();
        string silence = home["silence.wav"];
        using (var w = new WavWriter(silence)) w.Write(new float[Sound.Rate]);
        using var host = new AppHost(home.Path, () => new FileMicrophone(silence, 8), () => throw new InvalidOperationException("no model here"),
            log: _ => { }, loginItems: new CountingLoginItems());
        var said = new List<(string, string)>();
        host.Problem += (title, why) =>
        {
            lock (said) said.Add((title, why));
        };
        host.Start();
        host.Recorder.Start("CS 101");
        var took = Stopwatch.StartNew();
        while (host.Recorder.Current?.State != LectureState.Paused && took.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(LectureState.Paused, host.Recorder.Current?.State);
        Assert.InRange(took.Elapsed.TotalSeconds, 4, 15);
        lock (said) Assert.Contains(("Recording paused", RecordingWords.CantHear), said);
        Assert.Equal(RecordingWords.CantHear, host.RecorderProblem);
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
