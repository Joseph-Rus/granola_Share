using System.Text.Json.Nodes;

namespace StudyStash.Core.Tests;

/// <summary>A microphone that plays back what a test gives it, at the rate and channels of a real one.</summary>
public sealed class FakeMic(int rate = 48000, int channels = 2) : IAudioSource
{
    public string Name => "Fake";
    public int SampleRate => rate;
    public int Channels => channels;
    public event Action<float[]>? Samples;
    public event Action<string>? Failed;
    public bool Running { get; private set; }

    public void Start() => Running = true;
    public void Stop() => Running = false;
    public void Dispose() => Running = false;

    /// <summary>Seconds of a tone (0 amplitude: silence), in buffers of 10 ms, as a sound card sends them.</summary>
    public void Play(double seconds, double hz = 440, double amplitude = 0.5)
    {
        int frames = (int)(seconds * rate), per = rate / 100;
        for (int at = 0; at < frames; at += per)
        {
            int n = Math.Min(per, frames - at);
            var buf = new float[n * channels];
            for (int i = 0; i < n; i++)
            {
                float v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * (at + i) / rate));
                for (int c = 0; c < channels; c++) buf[i * channels + c] = v;
            }
            Samples?.Invoke(buf);
        }
    }

    public void Break(string why) => Failed?.Invoke(why);
}

/// <summary>Whisper stand-in: one line per piece, saying where the piece started, and what it was told.</summary>
public sealed class FakeWhisper : ITranscriber
{
    public List<(int Samples, string Prompt, string Language)> Calls { get; } = [];
    public bool Disposed { get; private set; }

    public Task<Transcription> TranscribeAsync(float[] samples, string prompt, string language, CancellationToken stop)
    {
        Calls.Add((samples.Length, prompt, language));
        double seconds = samples.Length / (double)Sound.Rate;
        return Task.FromResult(new Transcription([new Spoken(0.5, seconds - 0.5, $"piece {Calls.Count}"), new Spoken(1, 2, "[BLANK_AUDIO]")], "en"));
    }

    public void Dispose() => Disposed = true;
}

public class RecordingTests
{
    // --- transcripts with times ------------------------------------------------------------------------------

    [Fact]
    public void Clock_and_length()
    {
        Assert.Equal("00:00", TimedText.Clock(0));
        Assert.Equal("18:05", TimedText.Clock(18 * 60 + 5.9));
        Assert.Equal("1:02:40", TimedText.Clock(3760));
        Assert.Equal("1 h 12 min", TimedText.Length(72 * 60));
        Assert.Equal("48 min", TimedText.Length(48 * 60 + 10));
        Assert.Equal("under a minute", TimedText.Length(20));
    }

    [Fact]
    public void Transcript_lines_round_trip()
    {
        var segs = new List<Spoken> { new(1085, 1090, " Okay, the midterm. "), new(1170, 1175, "[Music]"), new(3761, 3765, "Past the hour.") };
        string text = TimedText.Format(segs);
        Assert.Equal("[18:05] Okay, the midterm.\n[1:02:41] Past the hour.", text);
        var back = TimedText.Parse(text);
        Assert.Equal([1085.0, 3761.0], back.Select(s => s.Start));
        Assert.Equal(3761, back[0].End);
        Assert.Equal("Okay, the midterm.", back[0].Text);
        Assert.True(TimedText.HasTimes(text));
        Assert.Equal("Okay, the midterm.\nPast the hour.", TimedText.Plain(text));
        Assert.Equal("Just words", TimedText.Plain("Just words"));
        Assert.False(TimedText.HasTimes("Speaker: hello"));
        // A line someone typed without a time joins the one before it.
        Assert.Equal("a b", TimedText.Parse("[00:01] a\nb")[0].Text);
    }

    [Fact]
    public void Tidy_drops_what_isnt_speech()
    {
        Assert.Equal("", TimedText.Tidy(" [BLANK_AUDIO] "));
        Assert.Equal("", TimedText.Tidy("(music)"));
        Assert.Equal("said [sic] this", TimedText.Tidy("said  [sic]   this"));
    }

    // --- sound ------------------------------------------------------------------------------------------------

    [Fact]
    public void Wav_round_trips_and_survives_a_crash()
    {
        using var dir = new TempDir();
        string path = dir["a.wav"];
        var tone = Enumerable.Range(0, 16000).Select(i => (float)(0.5 * Math.Sin(i / 10.0))).ToArray();
        using (var w = new WavWriter(path)) w.Write(tone);
        var back = Sound.ReadWav(path);
        Assert.Equal(16000, back.Length);
        Assert.True(tone.Zip(back).All(p => Math.Abs(p.First - p.Second) < 1e-4));
        Assert.Equal(1.0, Sound.WavSeconds(path), 3);

        // A crash: more sound written after the last header update, and a torn last byte.
        using (var f = new FileStream(path, FileMode.Append)) f.Write(new byte[2001]);
        Assert.Equal(17000, Sound.ReadWav(path).Length);
        using (var w = new WavWriter(path, append: true))
        {
            Assert.Equal(17000, w.Samples);
            w.Write(new float[500]);
        }
        Assert.Equal(17500, Sound.ReadWav(path).Length);
        Assert.Equal(1000, Sound.ReadWav(path, 16500).Length);
    }

    [Fact]
    public void Wav_from_another_tool_reads()
    {
        // afconvert puts a filler chunk before the sound.
        var info = Sound.Wav(Path.Combine(AppContext.BaseDirectory, "Fixtures", "speech.wav"));
        Assert.Equal(16000, info.Rate);
        Assert.True(info.DataStart > WavWriter.HeaderBytes);
        Assert.InRange(info.Seconds, 3, 10);
    }

    static double Amplitude(float[] s, int skip) => Math.Sqrt(2 * s.Skip(skip).Take(s.Length - 2 * skip).Average(x => x * (double)x));

    [Fact]
    public void Resampler_keeps_speech_and_removes_what_16k_cant_hold()
    {
        foreach (var (rate, channels) in new[] { (48000, 2), (44100, 1), (16000, 1), (96000, 4) })
        {
            float[] Tone(double hz)
            {
                var r = new Resampler(rate, channels);
                var output = new List<float>();
                for (int block = 0; block < 20; block++)
                {
                    var buf = new float[rate / 20 * channels];
                    for (int i = 0; i < rate / 20; i++)
                        for (int c = 0; c < channels; c++) buf[i * channels + c] = (float)(0.8 * Math.Sin(2 * Math.PI * hz * (block * rate / 20 + i) / rate));
                    output.AddRange(r.Process(buf));
                }
                output.AddRange(r.Flush());
                return [.. output];
            }
            var speech = Tone(1000);
            Assert.InRange(speech.Length, 15990, 16010);
            Assert.InRange(Amplitude(speech, 500), 0.78, 0.82);
            if (rate > 16000) Assert.True(Amplitude(Tone(11000), 500) < 0.01, $"{rate}: 11 kHz came through");
        }
    }

    [Fact]
    public void Pieces_are_cut_where_its_quiet()
    {
        var window = new float[(int)(29.5 * Sound.Rate)];
        for (int i = 0; i < window.Length; i++) window[i] = (float)(0.3 * Math.Sin(i / 7.0));
        for (int i = 24 * Sound.Rate; i < (int)(24.4 * Sound.Rate); i++) window[i] = 0; // a pause at 24 s
        int cut = Segmenter.CutLength(window, final: false);
        Assert.InRange(cut / (double)Sound.Rate, 24.0, 24.4);
        Assert.Equal(0, Segmenter.CutLength(window.AsSpan(0, 10 * Sound.Rate), final: false)); // still recording: wait
        Assert.Equal(10 * Sound.Rate, Segmenter.CutLength(window.AsSpan(0, 10 * Sound.Rate), final: true)); // stopped: the rest
    }

    [Fact]
    public void Silence_isnt_sent_to_whisper()
    {
        Assert.True(new Chunk(0, new float[Sound.Rate * 5]).Silent());
        var quiet = Enumerable.Range(0, Sound.Rate * 5).Select(i => (float)(0.001 * Math.Sin(i))).ToArray();
        Assert.True(new Chunk(0, quiet).Silent());
        quiet[Sound.Rate * 3] = 0.9f;
        for (int i = 0; i < Sound.Rate / 2; i++) quiet[Sound.Rate * 3 + i] = (float)(0.2 * Math.Sin(i / 5.0));
        Assert.False(new Chunk(0, quiet).Silent());
    }

    // --- timetable --------------------------------------------------------------------------------------------

    static Timetable Week() => new()
    {
        Classes =
        [
            new("CS 101", [new(DayOfWeek.Tuesday, new(10, 0), new(11, 15)), new(DayOfWeek.Thursday, new(10, 0), new(11, 15))]),
            new("BIO 110", [new(DayOfWeek.Tuesday, new(11, 0), new(12, 30))]),
        ],
    };

    [Fact]
    public void Record_picks_the_class_on_now()
    {
        var t = Week();
        var tue = new DateTime(2026, 9, 22); // a Tuesday
        Assert.Equal("CS 101", t.Now(tue.AddHours(9).AddMinutes(52))?.Name); // ten minutes early counts
        Assert.Null(t.Now(tue.AddHours(9).AddMinutes(40)));
        Assert.Equal("BIO 110", t.Now(tue.AddHours(11).AddMinutes(5))?.Name); // overlap: the one that started last
        Assert.Equal("Tue 10:00–11:15", t.Now(tue.AddHours(10))!.Time.Describe());
        var next = t.Next(tue.AddHours(13));
        Assert.Equal("CS 101", next?.Class.Name);
        Assert.Equal(new DateTime(2026, 9, 24, 10, 0, 0), next?.Starts);
        Assert.Equal(new DateTime(2026, 9, 29, 10, 0, 0), t.Next(new DateTime(2026, 9, 24, 10, 30, 0))?.Starts);
    }

    [Theory]
    [InlineData("Tue Thu 10:00–11:15", "Tue 10:00–11:15|Thu 10:00–11:15")]
    [InlineData("Mon Wed Fri 9-9:50", "Mon 9:00–9:50|Wed 9:00–9:50|Fri 9:00–9:50")]
    [InlineData("MWF 9:00-9:50", "Mon 9:00–9:50|Wed 9:00–9:50|Fri 9:00–9:50")]
    [InlineData("TTh 2-3:15", "Tue 14:00–15:15|Thu 14:00–15:15")]
    [InlineData("tuesday 2pm to 3:15pm", "Tue 14:00–15:15")]
    [InlineData("Wed 11:30-1pm", "Wed 11:30–13:00")]
    [InlineData("sometime", "")]
    [InlineData("Mon 10-9", "")]
    public void Class_times_are_read_as_people_write_them(string text, string expected)
    {
        var times = ClassTime.ParseMany(text);
        Assert.Equal(expected, times is null ? "" : string.Join("|", times.Select(t => t.Describe())));
    }

    [Fact]
    public void Timetable_saves_and_follows_the_librarys_classes()
    {
        using var dir = new TempDir();
        Week().Save(dir.Path);
        var back = Timetable.Load(dir.Path);
        Assert.Equal(["CS 101", "BIO 110"], back.Classes.Select(c => c.Name));
        Assert.Equal(new TimeOnly(11, 15), back.Classes[0].Times[0].End);
        Assert.True(back.KeepOnly(["CS 101"]));
        Assert.Equal(["CS 101"], back.Classes.Select(c => c.Name));
        Assert.Empty(Timetable.Load(dir["nowhere"]).Classes);
    }

    [Fact]
    public void Class_colors_are_the_designs()
    {
        Assert.Equal(["#398AD6", "#2FA465", "#876CCA", "#D18E35"], Enumerable.Range(0, 4).Select(i => ClassColors.Hex(ClassColors.For(i))));
        Assert.Equal(ClassColors.For(1), ClassColors.For(9));
        foreach (var c in ClassColors.Palette) Assert.Matches("^#[0-9A-F]{6}$", ClassColors.Hex(c));
    }

    // --- recording ----------------------------------------------------------------------------------------------

    static readonly DateTimeOffset Tuesday = new(2026, 9, 22, 10, 2, 12, TimeSpan.FromHours(-7));

    [Fact]
    public void Records_pauses_and_stops()
    {
        using var dir = new TempDir();
        var store = new LectureStore(dir.Path);
        FakeMic? mic = null;
        using var rec = new Recorder(store, () => mic = new FakeMic(), () => Tuesday);
        int changes = 0;
        rec.Changed += () => changes++;
        var l = rec.Start("CS 101", "Sam");
        Assert.StartsWith("rec-20260922-100212-", l.Id);
        Assert.True(mic!.Running);
        mic.Play(3);
        rec.Pause();
        Assert.False(mic.Running);
        Assert.Equal(LectureState.Paused, rec.Current!.State);
        Assert.InRange(rec.Current.Seconds, 2.99, 3.01);
        rec.Resume();
        mic.Play(4, amplitude: 0.8);
        SpinWait.SpinUntil(() => rec.Levels().Any(x => x > 0.5), 2000);
        Assert.Contains(rec.Levels(), x => x > 0.5);
        var done = rec.Stop()!;
        Assert.Null(rec.Current);
        Assert.Equal(LectureState.Transcribing, done.State);
        Assert.InRange(done.Seconds, 6.99, 7.01);
        Assert.InRange(Sound.WavSeconds(store.AudioPath(l.Id)), 6.99, 7.01);
        Assert.Equal(4, changes);
        Assert.Equal("CS 101", store.All().Single().ClassName);
    }

    [Fact]
    public void A_moment_of_recording_isnt_kept()
    {
        using var dir = new TempDir();
        var store = new LectureStore(dir.Path);
        FakeMic? mic = null;
        using var rec = new Recorder(store, () => mic = new FakeMic());
        var l = rec.Start("");
        mic!.Play(1);
        Assert.Null(rec.Stop());
        Assert.Empty(store.All());
        Assert.False(File.Exists(store.AudioPath(l.Id)));
    }

    [Fact]
    public void A_microphone_that_fails_pauses_the_lecture()
    {
        using var dir = new TempDir();
        var store = new LectureStore(dir.Path);
        FakeMic? mic = null;
        using var rec = new Recorder(store, () => mic = new FakeMic());
        string? told = null;
        rec.Problem += why => told = why;
        rec.Start("CS 101");
        mic!.Play(2);
        mic.Break("unplugged");
        Assert.Equal("unplugged", told);
        Assert.Equal(LectureState.Paused, rec.Current!.State);
    }

    [Fact]
    public void A_lecture_cut_short_by_a_crash_is_kept()
    {
        using var dir = new TempDir();
        var store = new LectureStore(dir.Path);
        var l = store.Add(new Lecture { Id = "rec-1", Started = "2026-09-22T10:00:00-07:00", State = LectureState.Recording });
        using (var w = new WavWriter(store.AudioPath(l.Id))) w.Write(new float[Sound.Rate * 12]);
        store.Add(new Lecture { Id = "rec-2", Started = "2026-09-22T11:00:00-07:00", State = LectureState.Paused }); // no audio
        Assert.Equal(1, Recorder.Recover(new LectureStore(dir.Path)));
        var again = new LectureStore(dir.Path);
        Assert.Equal(LectureState.Transcribing, again.Get("rec-1")!.State);
        Assert.Equal(12, again.Get("rec-1")!.Seconds, 2);
        Assert.Null(again.Get("rec-2"));
    }

    // --- whisper -----------------------------------------------------------------------------------------------

    static void Speech(float[] into, double from, double to)
    {
        for (int i = (int)(from * Sound.Rate); i < (int)(to * Sound.Rate) && i < into.Length; i++) into[i] = (float)(0.3 * Math.Sin(i / 6.0));
    }

    [Fact]
    public async Task Whisper_writes_a_lecture_down_while_it_records_and_after()
    {
        using var dir = new TempDir();
        var store = new LectureStore(dir.Path);
        var whisper = new FakeWhisper();
        Lecture? live = store.Add(new Lecture { Id = "rec-1", Started = "2026-09-22T10:00:00-07:00", State = LectureState.Recording });
        var audio = new float[Sound.Rate * 70];
        Speech(audio, 0, 24);
        Speech(audio, 24.5, 50); // silence from 50 s to 62 s
        Speech(audio, 62, 70);
        using (var w = new WavWriter(store.AudioPath("rec-1"))) w.Write(audio.AsSpan(0, Sound.Rate * 45));
        var worker = new TranscriptionWorker(store, () => whisper, () => live);
        var heard = new List<string>();
        worker.Heard += (_, lines) => heard.AddRange(lines.Select(s => s.Text));
        Lecture? finished = null;
        worker.Finished += l => finished = l;

        Assert.True(await worker.StepAsync(default)); // the first full piece, cut in the pause at 24 s
        Assert.False(await worker.StepAsync(default)); // 45 s recorded: not a full second piece yet
        var l1 = store.Get("rec-1")!;
        Assert.InRange(l1.TranscribedSeconds, 24, 24.5);
        Assert.Equal(["piece 1"], heard);
        Assert.Equal(0.5 + 0, l1.Segments[0].Start);

        // Recording goes on to 70 s, then stops.
        using (var w = new WavWriter(store.AudioPath("rec-1"), append: true)) w.Write(audio.AsSpan(Sound.Rate * 45));
        store.Update("rec-1", x =>
        {
            x.State = LectureState.Transcribing;
            x.Seconds = 70;
        });
        live = null;
        while (await worker.StepAsync(default)) { }
        var l2 = store.Get("rec-1")!;
        Assert.Equal(LectureState.Sending, l2.State);
        Assert.Equal(70, l2.TranscribedSeconds, 2);
        Assert.Same(l2, finished);
        Assert.Equal("en", l2.Language);
        // Three pieces, each after the one before: the silent one wasn't sent, and each was told what came before.
        Assert.True(whisper.Calls.Count is 2 or 3, $"{whisper.Calls.Count} calls");
        Assert.Equal("", whisper.Calls[0].Prompt);
        Assert.Equal("piece 1", whisper.Calls[1].Prompt);
        Assert.Equal("en", whisper.Calls[1].Language);
        Assert.All(l2.Segments, s => Assert.DoesNotContain("BLANK", s.Text));
        Assert.Equal(l2.Segments.OrderBy(s => s.Start).Select(s => s.Start), l2.Segments.Select(s => s.Start));
        Assert.Contains("[00:00] piece 1", l2.Transcript());
    }

    [Fact]
    public async Task A_missing_recording_fails_the_lecture()
    {
        using var dir = new TempDir();
        var store = new LectureStore(dir.Path);
        store.Add(new Lecture { Id = "rec-1", Started = "2026-09-22T10:00:00-07:00", State = LectureState.Transcribing, Seconds = 30 });
        var worker = new TranscriptionWorker(store, () => new FakeWhisper(), () => null);
        await worker.StepAsync(default);
        Assert.Equal(LectureState.Failed, store.Get("rec-1")!.State);
        Assert.Contains("missing", store.Get("rec-1")!.Error);
    }

    // --- to the library -------------------------------------------------------------------------------------------

    sealed class FakeLibrary
    {
        public List<JsonObject> Sent { get; } = [];
        public Exception? Down { get; set; }
        public string Status { get; set; } = "queued";

        public LaptopHost Host(Func<DateTimeOffset>? clock = null) => new()
        {
            Post = (url, body, key) =>
            {
                if (Down is not null) throw Down;
                Assert.EndsWith("/api/ingest", url);
                Assert.Equal("pw", key);
                Sent.Add((JsonObject)JsonNode.Parse(body)!);
                return Task.FromResult(new JsonObject { ["status"] = "queued" });
            },
            Get = (url, key) => Task.FromResult<JsonObject?>(Sent.Count == 0 ? null : new JsonObject
            {
                ["status"] = Status, ["class_name"] = "CS 101", ["lecture_title"] = "Recursion and the call stack",
            }),
            Clock = clock ?? (() => Tuesday),
        };
    }

    [Fact]
    public async Task Sends_a_lecture_and_hears_when_its_filed()
    {
        using var dir = new TempDir();
        var store = new LectureStore(dir.Path);
        store.Add(new Lecture
        {
            Id = "rec-1", Started = "2026-09-22T10:02:12-07:00", State = LectureState.Sending, ClassName = "CS 101", Seconds = 4320,
            Segments = [new(0, 5, "Okay."), new(1085, 1090, "The midterm.")], Language = "en",
        });
        var lib = new FakeLibrary();
        var cc = new ClientConfig(dir.Path) { ServerUrl = "http://mini:8000/", PoolKey = "pw", DisplayName = "Sam" };
        var sender = new LectureSender(store, () => cc, lib.Host());
        Assert.Equal(1, await sender.StepAsync());
        var sent = lib.Sent.Single();
        Assert.Equal("rec-1", sent["id"]!.GetValue<string>());
        Assert.Equal("CS 101", sent["folder"]!.GetValue<string>());
        Assert.Equal("Sam", sent["owner"]!.GetValue<string>());
        Assert.Equal("CS 101 lecture, Tue 22 Sep", sent["title"]!.GetValue<string>());
        Assert.Equal("[00:00] Okay.\n[18:05] The midterm.", sent["transcript"]!.GetValue<string>());
        Assert.Equal("recorder", sent["raw"]!["source"]!.GetValue<string>());
        Assert.Equal(LectureState.Writing, store.Get("rec-1")!.State);

        Assert.Equal(0, await sender.StepAsync()); // still being written
        lib.Status = "done";
        Assert.Equal(1, await sender.StepAsync());
        var filed = store.Get("rec-1")!;
        Assert.Equal(LectureState.Filed, filed.State);
        Assert.Equal("CS 101", filed.FiledClass);
        Assert.Equal("Recursion and the call stack", filed.FiledTitle);
    }

    [Fact]
    public async Task A_library_out_of_reach_gets_the_lecture_later()
    {
        using var dir = new TempDir();
        var store = new LectureStore(dir.Path);
        store.Add(new Lecture { Id = "rec-1", Started = "2026-09-22T10:02:12-07:00", State = LectureState.Sending, Segments = [new(0, 5, "Hi.")] });
        var lib = new FakeLibrary { Down = new HttpRequestException("no route to host") };
        var now = Tuesday;
        var sender = new LectureSender(store, () => new ClientConfig(dir.Path) { ServerUrl = "http://mini:8000", PoolKey = "pw" }, lib.Host(() => now));
        await sender.StepAsync();
        Assert.Equal("unreachable", sender.ProblemKind);
        Assert.Equal(LectureState.Sending, store.Get("rec-1")!.State);
        lib.Down = null;
        Assert.Equal(0, await sender.StepAsync()); // waits its turn
        now = now.AddSeconds(31);
        Assert.Equal(1, await sender.StepAsync());
        Assert.Null(sender.ProblemKind);
        Assert.Equal(LectureState.Writing, store.Get("rec-1")!.State);

        lib.Down = new LibraryRefusedException(401, "wrong password");
        store.Update("rec-1", x => x.State = LectureState.Sending);
        await sender.StepAsync();
        Assert.Equal("password", sender.ProblemKind);
    }

    [Fact]
    public void Old_audio_goes_filed_notes_stay()
    {
        using var dir = new TempDir();
        var store = new LectureStore(dir.Path);
        foreach (var (id, days, state) in new[] { ("rec-old", 40, LectureState.Filed), ("rec-new", 2, LectureState.Filed), ("rec-waiting", 40, LectureState.Sending) })
        {
            store.Add(new Lecture { Id = id, Started = Tuesday.AddDays(-days).ToString("yyyy-MM-dd'T'HH:mm:sszzz"), State = state });
            using var w = new WavWriter(store.AudioPath(id));
        }
        Assert.Equal(1, store.PruneAudio(30, Tuesday));
        Assert.False(File.Exists(store.AudioPath("rec-old")));
        Assert.True(File.Exists(store.AudioPath("rec-new")));
        Assert.True(File.Exists(store.AudioPath("rec-waiting")));
        Assert.NotNull(store.Get("rec-old"));
    }
}
