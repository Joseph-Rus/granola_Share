using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace StudyStash.Core;

/// <summary>A microphone, or the computer's own sound: interleaved float samples, as they come.</summary>
public interface IAudioSource : IDisposable
{
    string Name { get; }
    int SampleRate { get; }
    int Channels { get; }
    /// <summary>Raised on the sound system's thread with each buffer.</summary>
    event Action<float[]>? Samples;
    /// <summary>The device went away or refused (unplugged, permission taken back).</summary>
    event Action<string>? Failed;
    void Start();
    void Stop();
}

/// <summary>What Whisper wrote down from a piece of sound: the words with their times in it, and the language.</summary>
public sealed record Transcription(List<Spoken> Segments, string Language);

/// <summary>Speech to text on this computer (Whisper).</summary>
public interface ITranscriber : IDisposable
{
    /// <summary>16 kHz mono samples to words. <paramref name="prompt"/> is what was said just before, so names and
    /// terms stay spelled the same across pieces; <paramref name="language"/> is the lecture's so far ("" finds
    /// it), so a quiet piece isn't read as another language.</summary>
    Task<Transcription> TranscribeAsync(float[] samples, string prompt, string language, CancellationToken stop);
}

/// <summary>
/// Records lectures: the microphone to a WAV in the laptop's recordings folder, one lecture at a time. Whisper and
/// the library are the <see cref="TranscriptionWorker"/>'s and the <see cref="LectureSender"/>'s; this only
/// records, so a slow computer never drops sound.
/// </summary>
public sealed class Recorder : IDisposable
{
    const int LevelBlock = Sound.Rate / 20; // 50 ms per bar
    public const int LevelCount = 64;

    readonly LectureStore store;
    readonly Func<IAudioSource> openSource;
    readonly Func<DateTimeOffset> clock;
    readonly Action<string> log;
    readonly Lock gate = new();
    readonly double[] levels = new double[LevelCount];
    int levelAt;
    readonly List<float> levelBuf = [];

    IAudioSource? source;
    Resampler? resampler;
    WavWriter? wav;
    Channel<float[]>? pending;
    Task? writer;

    public Recorder(LectureStore store, Func<IAudioSource> openSource, Func<DateTimeOffset>? clock = null, Action<string>? log = null)
    {
        this.store = store;
        this.openSource = openSource;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.log = log ?? (_ => { });
    }

    string? currentId;

    /// <summary>The lecture being recorded, or paused; null when nothing is.</summary>
    public Lecture? Current => currentId is null ? null : store.Get(currentId);

    /// <summary>Recording or paused changed; the recording stopped; the device failed.</summary>
    public event Action? Changed;

    /// <summary>Raised when the audio device fails mid-lecture: what to tell the person.</summary>
    public event Action<string>? Problem;

    public bool IsRecording => Current?.State == LectureState.Recording;

    /// <summary>Seconds recorded so far (not counting pauses).</summary>
    public double Elapsed
    {
        get
        {
            lock (gate) return wav?.Seconds ?? Current?.Seconds ?? 0;
        }
    }

    /// <summary>The last few seconds' loudness, oldest first, 0 to 1: the waveform's bars.</summary>
    public double[] Levels()
    {
        lock (gate)
        {
            var result = new double[LevelCount];
            for (int i = 0; i < LevelCount; i++) result[i] = levels[(levelAt + i) % LevelCount];
            return result;
        }
    }

    /// <summary>Start recording a lecture for a class ("" lets the library sort it).</summary>
    public Lecture Start(string className, string owner = "")
    {
        lock (gate)
        {
            if (Current is { } already) return already;
            var now = clock();
            var lecture = store.Add(new Lecture
            {
                Id = Lecture.NewId(now), Started = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
                ClassName = className, Owner = owner,
            });
            currentId = lecture.Id;
            try
            {
                Open(append: false);
            }
            catch
            {
                currentId = null;
                store.Delete(lecture.Id);
                throw;
            }
            log($"[recorder] recording {lecture.Id} for '{className}'");
        }
        Changed?.Invoke();
        return Current!;
    }

    void Open(bool append)
    {
        string id = currentId!;
        wav = new WavWriter(store.AudioPath(id), Sound.Rate, append);
        source = openSource();
        resampler = new Resampler(source.SampleRate, source.Channels);
        pending = Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true });
        var w = wav;
        var r = resampler;
        var p = pending;
        writer = Task.Run(async () =>
        {
            await foreach (var buffer in p.Reader.ReadAllAsync()) Write(w, r.Process(buffer));
            Write(w, r.Flush());
        });
        source.Samples += OnSamples;
        source.Failed += OnFailed;
        source.Start();
        store.Update(id, l => l.State = LectureState.Recording);
    }

    void OnSamples(float[] buffer) => pending?.Writer.TryWrite(buffer);

    void OnFailed(string why)
    {
        log($"[recorder] the microphone stopped: {why}");
        Pause();
        Problem?.Invoke(why);
    }

    void Write(WavWriter w, float[] samples)
    {
        if (samples.Length == 0) return;
        lock (gate)
        {
            w.Write(samples);
            foreach (float s in samples)
            {
                levelBuf.Add(s);
                if (levelBuf.Count < LevelBlock) continue;
                levels[levelAt] = Sound.Level(Sound.Db(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(levelBuf)));
                levelAt = (levelAt + 1) % LevelCount;
                levelBuf.Clear();
            }
        }
    }

    /// <summary>Stop the microphone and write out what was recorded; the file stays open for more.</summary>
    void Close()
    {
        IAudioSource? s;
        Task? w;
        lock (gate)
        {
            s = source;
            w = writer;
            source = null;
            writer = null;
        }
        if (s is not null)
        {
            s.Samples -= OnSamples;
            s.Failed -= OnFailed;
            try
            {
                s.Stop();
            }
            finally
            {
                s.Dispose();
            }
        }
        pending?.Writer.TryComplete();
        w?.Wait(TimeSpan.FromSeconds(10));
        lock (gate)
        {
            if (currentId is not null && wav is not null)
            {
                double seconds = wav.Seconds;
                store.Update(currentId, l => l.Seconds = seconds);
            }
            wav?.Dispose();
            wav = null;
            pending = null;
            Array.Clear(levels);
        }
    }

    public void Pause()
    {
        lock (gate)
        {
            if (Current is not { State: LectureState.Recording }) return;
        }
        Close();
        lock (gate)
        {
            if (currentId is not null) store.Update(currentId, l => l.State = LectureState.Paused);
        }
        Changed?.Invoke();
    }

    public void Resume()
    {
        lock (gate)
        {
            if (Current is not { State: LectureState.Paused }) return;
            Open(append: true);
        }
        Changed?.Invoke();
    }

    /// <summary>Stop recording: the lecture goes to Whisper for the rest, then to the library. Null when nothing
    /// was recording, or when it was too short to keep (it's thrown away).</summary>
    public Lecture? Stop(double keepAtLeastSeconds = 5)
    {
        Lecture? l = Current;
        if (l is null) return null;
        if (l.State == LectureState.Recording) Close();
        lock (gate)
        {
            currentId = null;
            string audio = store.AudioPath(l.Id);
            double seconds = File.Exists(audio) ? Sound.WavSeconds(audio) : 0;
            if (seconds < keepAtLeastSeconds)
            {
                store.Delete(l.Id);
                log($"[recorder] {l.Id} was {seconds:0.0}s: not kept");
                l = null;
            }
            else
            {
                l = store.Update(l.Id, x =>
                {
                    x.Seconds = seconds;
                    x.State = LectureState.Transcribing;
                });
                log($"[recorder] stopped {l!.Id} after {TimedText.Clock(seconds)}");
            }
        }
        Changed?.Invoke();
        return l;
    }

    /// <summary>A lecture left recording when the app last quit or crashed: it's stopped now, keeping what was
    /// recorded, and goes on to Whisper.</summary>
    public static int Recover(LectureStore store, Action<string>? log = null)
    {
        int n = 0;
        foreach (var l in store.All().Where(l => l.State is LectureState.Recording or LectureState.Paused))
        {
            string audio = store.AudioPath(l.Id);
            if (!File.Exists(audio))
            {
                store.Delete(l.Id);
                continue;
            }
            using (new WavWriter(audio, Sound.Rate, append: true)) { } // fixes the header a crash left behind
            double seconds = Sound.WavSeconds(audio);
            store.Update(l.Id, x =>
            {
                x.Seconds = seconds;
                x.State = LectureState.Transcribing;
            });
            log?.Invoke($"[recorder] {l.Id} was cut short at {TimedText.Clock(seconds)}: kept");
            n++;
        }
        return n;
    }

    public void Dispose()
    {
        if (Current is not null) Stop();
    }
}

/// <summary>
/// Writes lectures down with Whisper, a piece at a time, straight from their WAV files: while a lecture records (so
/// the recorder can show what was said), and after it stops. It works from the file and the lecture's own record of
/// how far it got, so a restart carries on where it left off.
/// </summary>
public sealed class TranscriptionWorker(LectureStore store, Func<ITranscriber> load, Func<Lecture?> recording,
    Action<string>? log = null, double minSeconds = 20, double maxSeconds = 29.5)
{
    readonly Action<string> log = log ?? (_ => { });
    readonly SemaphoreSlim wake = new(0, 1);
    ITranscriber? model;
    DateTime usedAt;

    /// <summary>A lecture got new words: the recorder window shows them.</summary>
    public event Action<Lecture, IReadOnlyList<Spoken>>? Heard;

    /// <summary>A lecture finished transcribing (it's ready for the library), or failed.</summary>
    public event Action<Lecture>? Finished;

    /// <summary>Keep Whisper in memory this long after its last piece of work (it's gigabytes).</summary>
    public TimeSpan KeepLoaded { get; set; } = TimeSpan.FromMinutes(3);

    public void Wake()
    {
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>One piece of work, if there is any: true when it did something.</summary>
    public async Task<bool> StepAsync(CancellationToken stop)
    {
        var live = recording();
        var candidates = store.All().Where(l => l.State == LectureState.Transcribing || (live is not null && l.Id == live.Id))
            .OrderBy(l => l.Id == live?.Id ? 1 : 0).ThenBy(l => l.Started, StringComparer.Ordinal).ToList();
        foreach (var l in candidates)
        {
            bool isLive = l.Id == live?.Id;
            string audio = store.AudioPath(l.Id);
            if (!File.Exists(audio))
            {
                if (!isLive) Fail(l.Id, "its recording is missing");
                continue;
            }
            long from = (long)Math.Round(l.TranscribedSeconds * Sound.Rate);
            float[] window = Sound.ReadWav(audio, from, (long)(maxSeconds * Sound.Rate));
            int take = Segmenter.CutLength(window, final: !isLive, minSeconds, maxSeconds);
            if (take == 0) continue; // a live lecture without a full piece yet
            await TranscribeAsync(l, new Chunk(from, window[..take]), stop);
            return true;
        }
        return false;
    }

    async Task TranscribeAsync(Lecture l, Chunk chunk, CancellationToken stop)
    {
        var heard = new List<Spoken>();
        string language = "";
        if (!chunk.Silent())
        {
            model ??= LoadModel();
            string prompt = string.Join(" ", l.Segments.TakeLast(6).Select(s => s.Text));
            Transcription t;
            try
            {
                t = await model.TranscribeAsync(chunk.Samples, prompt.Length > 600 ? prompt[^600..] : prompt, l.Language, stop);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Fail(l.Id, $"Whisper couldn't read part of it: {e.Message}");
                return;
            }
            usedAt = DateTime.UtcNow;
            foreach (var s in t.Segments)
            {
                string text = TimedText.Tidy(s.Text);
                if (text.Length == 0) continue;
                heard.Add(new Spoken(chunk.StartSeconds + s.Start, chunk.StartSeconds + s.End, text));
            }
            language = t.Language;
        }
        bool done = false;
        // The recording may have stopped while Whisper worked: the saved lecture says so now.
        var saved = store.Update(l.Id, x =>
        {
            x.Segments = [.. x.Segments, .. heard];
            x.TranscribedSeconds = chunk.EndSeconds;
            if (x.Language.Length == 0) x.Language = language;
            done = x.State == LectureState.Transcribing && x.TranscribedSeconds >= x.Seconds - 0.01;
            if (done) x.State = LectureState.Sending;
        });
        if (saved is null) return; // deleted meanwhile
        if (heard.Count > 0) Heard?.Invoke(saved, heard);
        if (done)
        {
            log($"[whisper] {saved.Id}: {saved.Segments.Count} lines from {TimedText.Clock(saved.Seconds)}");
            Finished?.Invoke(saved);
        }
    }

    ITranscriber LoadModel()
    {
        log("[whisper] loading the model");
        return load();
    }

    void Fail(string id, string why)
    {
        var l = store.Update(id, x =>
        {
            x.State = LectureState.Failed;
            x.Error = why;
        });
        log($"[whisper] {id}: {why}");
        if (l is not null) Finished?.Invoke(l);
    }

    public async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            bool worked;
            try
            {
                worked = await StepAsync(stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                log($"[whisper] {e.Message}");
                worked = false;
            }
            if (worked) continue;
            if (model is not null && DateTime.UtcNow - usedAt > KeepLoaded && recording() is null)
            {
                model.Dispose();
                model = null;
                log("[whisper] unloaded the model");
            }
            try
            {
                await wake.WaitAsync(TimeSpan.FromSeconds(recording() is null ? 30 : 3), stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        model?.Dispose();
        model = null;
    }
}

/// <summary>
/// Takes transcribed lectures to the library, and asks after them until they're filed. A library that's off, or
/// out of reach, gets the lecture later; nothing is lost while the laptop is away from it.
/// </summary>
public sealed class LectureSender(LectureStore store, Func<ClientConfig> config, LaptopHost host, Action<string>? log = null)
{
    static readonly double[] Backoff = [30, 60, 120, 300, 600];
    readonly Action<string> log = log ?? (_ => { });
    readonly string home = Path.GetDirectoryName(store.Dir)!; // where timetable.json is
    readonly SemaphoreSlim wake = new(0, 1);

    /// <summary>A lecture's state changed: sent, filed, or refused.</summary>
    public event Action<Lecture>? Changed;

    /// <summary>The last answer from the library: null while all is well, else why lectures are waiting.</summary>
    public string? Problem { get; private set; }

    /// <summary>"password", "unreachable" or "other" while lectures can't reach the library.</summary>
    public string? ProblemKind { get; private set; }

    public void Wake()
    {
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public async Task<int> StepAsync()
    {
        var cc = config();
        if (cc.ServerUrl.Length == 0) return 0;
        string server = cc.ServerUrl.TrimEnd('/');
        double now = host.Clock().ToUnixTimeMilliseconds() / 1000.0;
        int n = 0;
        foreach (var l in store.All())
        {
            Lecture? changed = null;
            if (l.State == LectureState.Sending && (l.RetryAt is null || l.RetryAt <= now))
            {
                var payload = l.Payload();
                if (l.Owner.Length == 0) payload["owner"] = cc.DisplayName;
                // Recorded without a class: the one the timetable says was on then files it without asking the AI.
                // Its own clock time (not this computer's zone) is what the timetable means.
                if (l.ClassName.Length == 0)
                    payload["folder"] = Timetable.Load(home).Now(l.StartedAt.DateTime, TimeSpan.FromMinutes(15))?.Name ?? "";
                try
                {
                    await host.Post($"{server}/api/ingest", payload.ToJsonString(), cc.PoolKey);
                    changed = store.Update(l.Id, x =>
                    {
                        x.State = LectureState.Writing;
                        x.Error = "";
                        x.RetryAt = null;
                        x.Tries = 0;
                    });
                    Problem = ProblemKind = null;
                    log($"[send] {l.Id} → {server}");
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
                {
                    changed = store.Update(l.Id, x => Refused(x, e, now));
                }
            }
            else if (l.State == LectureState.Writing)
            {
                JsonObject? status;
                try
                {
                    status = await host.Get($"{server}/api/notes/{Uri.EscapeDataString(l.Id)}/status", cc.PoolKey);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
                {
                    continue; // asked again next time
                }
                string state = status is null ? "" : Py.Str(status["status"]);
                if (status is null)
                {
                    // The library lost it (restored from a backup, or someone deleted it): send it again.
                    changed = store.Update(l.Id, x => x.State = LectureState.Sending);
                }
                else if (state is Store.Done or Store.Failed)
                {
                    changed = store.Update(l.Id, x =>
                    {
                        x.State = LectureState.Filed;
                        x.FiledClass = Py.AsString(status["class_name"]) ?? "";
                        x.FiledTitle = Py.AsString(status["lecture_title"]) ?? "";
                        x.Error = state == Store.Failed ? "The library couldn't write notes for it" : "";
                    });
                    log($"[send] {l.Id} filed in {changed?.FiledClass}");
                }
            }
            if (changed is null) continue;
            Changed?.Invoke(changed);
            n++;
        }
        return n;
    }

    void Refused(Lecture l, Exception e, double now)
    {
        l.Tries++;
        l.RetryAt = now + Backoff[Math.Min(l.Tries - 1, Backoff.Length - 1)];
        (ProblemKind, Problem) = e switch
        {
            LibraryRefusedException { Status: 401 or 403 } => ("password", "The library's password changed. Sign in again in Settings."),
            LibraryRefusedException r => ("other", $"The library turned it down ({r.Status})."),
            _ => ("unreachable", "Can't reach your library. Lectures wait here until it's back."),
        };
        l.Error = Problem;
        log($"[send] {l.Id}: {e.Message}");
    }

    public async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await StepAsync();
            }
            catch (Exception e)
            {
                log($"[send] {e.Message}");
            }
            bool writing = store.All().Any(l => l.State == LectureState.Writing);
            try
            {
                await wake.WaitAsync(TimeSpan.FromSeconds(writing ? 10 : 30), stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
