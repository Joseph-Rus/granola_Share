using StudyStash.Audio;
using StudyStash.Library;

namespace StudyStash.Core.Tests;

/// <summary>
/// Real Whisper on a recording of speech (Fixtures/speech.wav, made with macOS's say). Runs when
/// STUDYSTASH_WHISPER_MODEL names a model file (CI downloads ggml-tiny.bin); otherwise it passes without running.
/// </summary>
public class LiveWhisperTests
{
    static string? Model => Environment.GetEnvironmentVariable("STUDYSTASH_WHISPER_MODEL") is { Length: > 0 } p && File.Exists(p) ? p : null;

    static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "speech.wav");

    [Fact]
    public async Task Whisper_hears_the_words()
    {
        if (Model is not { } model) return;
        using var whisper = new WhisperTranscriber(model);
        var t = await whisper.TranscribeAsync(Sound.ReadWav(Fixture), "", "", default);
        string said = string.Join(" ", t.Segments.Select(s => s.Text)).ToLowerInvariant();
        Assert.Contains("midterm", said);
        Assert.Contains("recursion", said);
        Assert.Equal("en", t.Language);
        Assert.All(t.Segments, s => Assert.True(s.End >= s.Start));
    }

    /// <summary>The whole way: a recording on the laptop, Whisper, the library's API, filed in its class.</summary>
    [Fact]
    public async Task A_recording_reaches_the_library_and_is_filed()
    {
        if (Model is not { } model) return;
        using var dir = new TempDir();
        var cfg = new Config(dir["library"], dir["pool"])
        {
            PoolPassword = "pw", OllamaEnabled = false, Classes = [new ClassDef("CS 101", ["cs101"])],
        };
        Directory.CreateDirectory(cfg.Home);
        using var db = new Store(cfg.DbPath, cfg.PoolDir);
        var pipeline = new Pipeline(cfg, db, log: _ => { });
        await using var site = await TestSite.StartAsync(b => LibraryWeb.Build(b, cfg, db, pipeline, new LibraryWebOptions
        {
            ListModels = _ => Task.FromResult<List<(string, double)>?>(null), Tailscale = () => new TailscaleInfo(false, false, "", "", []),
            Latest = _ => Task.FromResult<Release?>(null),
        }));

        var store = new LectureStore(dir["laptop"]);
        var lecture = store.Add(new Lecture { Id = "rec-live-1", Started = "2026-09-22T10:02:12-07:00", State = LectureState.Transcribing, ClassName = "CS 101" });
        File.Copy(Fixture, store.AudioPath(lecture.Id));
        store.Update(lecture.Id, l => l.Seconds = Sound.WavSeconds(store.AudioPath(l.Id)));

        var worker = new TranscriptionWorker(store, () => new WhisperTranscriber(model), () => null);
        while (await worker.StepAsync(default)) { }
        Assert.Equal(LectureState.Sending, store.Get(lecture.Id)!.State);
        Assert.Contains("midterm", store.Get(lecture.Id)!.Transcript().ToLowerInvariant());

        var cc = new ClientConfig(dir["laptop"]) { ServerUrl = "http://localhost", PoolKey = "pw", DisplayName = "Sam" };
        var host = new LaptopHost
        {
            Post = (url, body, key) => LibraryHttp.PostAsync(url, body, key, site.Client),
            Get = (url, key) => LibraryHttp.GetAsync(url, key, site.Client),
        };
        var sender = new LectureSender(store, () => cc, host);
        await sender.StepAsync();
        Assert.Equal(LectureState.Writing, store.Get(lecture.Id)!.State);
        await pipeline.RunPendingAsync();
        await sender.StepAsync();
        var filed = store.Get(lecture.Id)!;
        Assert.Equal(LectureState.Filed, filed.State);
        Assert.Equal("CS 101", filed.FiledClass);
        var row = db.Get(lecture.Id)!;
        Assert.Equal("CS 101", row.ClassName);
        Assert.Equal("Sam", row.Owner);
        Assert.Equal(1, row.HasTranscript);
    }
}
