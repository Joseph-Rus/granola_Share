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

/// <summary>What the recorder tells the student when it can't record, or had to stop: what happened and what to do.</summary>
public static class RecordingWords
{
    public const string DiskFull = "Your disk is full: recording paused. Free some space, then press Resume.";
    public const string NearlyFull = "Your disk is nearly full: free some space to record.";
    public const string MicStopped = "The microphone stopped. Plug it back in, then press Resume.";
    public const string CantHearMac = "Study Stash can't hear the microphone. Allow it in System Settings → Privacy & Security → Microphone.";
    public const string CantHearWindows = "Windows isn't letting Study Stash use the microphone. Turn on Settings → Privacy & security → Microphone → Let desktop apps access your microphone.";
    public const string CouldntRead = "The recording couldn't be read";

    /// <summary>A microphone that gives only silence: the system isn't letting Study Stash hear it.</summary>
    public static string CantHear => OperatingSystem.IsWindows() ? CantHearWindows : CantHearMac;

    public static string CouldntSave(string why) => $"Recording paused: the recording couldn't be saved ({why.TrimEnd('.')}).";
}

/// <summary>
/// Records lectures: the microphone to a WAV in the laptop's recordings folder, one lecture at a time. Whisper and
/// the library are the <see cref="TranscriptionWorker"/>'s and the <see cref="LectureSender"/>'s; this only
/// records, so a slow computer never drops sound. <see cref="Check"/>, called every second, notices a microphone
/// that went quiet (unplugged, or not back after the laptop slept), one that gives only silence (not allowed), and
/// a disk filling up: it opens the microphone again once, and otherwise pauses and says why.
/// </summary>
public sealed class Recorder : IDisposable
{
    const int LevelBlock = Sound.Rate / 20; // 50 ms per bar
    public const int LevelCount = 64;
    /// <summary>Room a new recording needs on the disk, and the least it keeps recording with.</summary>
    public const long SpaceToStart = 300L << 20, SpaceToKeepGoing = 100L << 20;
    static readonly TimeSpan QuietFor = TimeSpan.FromSeconds(3), SilentFor = TimeSpan.FromSeconds(5),
        AfterFailing = TimeSpan.FromSeconds(1), Napped = TimeSpan.FromSeconds(10);

    readonly LectureStore store;
    readonly Func<IAudioSource> openSource;
    readonly Func<DateTimeOffset> clock;
    readonly Action<string> log;
    readonly Func<string, bool, IWavSink> openFile;
    readonly Func<string, long?> freeBytes;
    // The file and the level bars, shared by the writer and the screen.
    readonly Lock gate = new();
    // Start, pause, resume, stop and the watchdog, one at a time (the watchdog runs on a timer's thread). Never
    // taken by the writer or the sound system's thread, so closing the microphone can wait for them.
    readonly Lock transitions = new();
    readonly double[] levels = new double[LevelCount];
    int levelAt;
    readonly List<float> levelBuf = [];

    IAudioSource? source;
    IWavSink? wav;
    Channel<float[]>? pending;
    Task? writer;
    int opened; // which opening of the microphone the writer belongs to

    // What the sound system's thread tells the watchdog: it counts, and never waits.
    long buffers;
    volatile bool heard;
    volatile string? failed;
    // The watchdog's own view, in Check's time.
    long buffersSeen, buffersAtStart;
    DateTime? watchedSince, lastBufferAt, lastCheckAt, failedAt;
    bool reopened;

    public Recorder(LectureStore store, Func<IAudioSource> openSource, Func<DateTimeOffset>? clock = null, Action<string>? log = null,
        Func<string, bool, IWavSink>? openFile = null, Func<string, long?>? freeBytes = null)
    {
        this.store = store;
        this.openSource = openSource;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        this.log = log ?? (_ => { });
        this.openFile = openFile ?? ((path, append) => new WavWriter(path, Sound.Rate, append));
        this.freeBytes = freeBytes ?? Disk.FreeBytes;
    }

    string? currentId;

    /// <summary>The lecture being recorded, or paused; null when nothing is.</summary>
    public Lecture? Current => currentId is null ? null : store.Get(currentId);

    /// <summary>Recording or paused changed; the recording stopped; the device failed.</summary>
    public event Action? Changed;

    /// <summary>Raised when recording had to pause (the microphone, the disk): what to tell the person.</summary>
    public event Action<string>? Problem;

    /// <summary>Why the lecture is paused, when it paused by itself; null while all is well.</summary>
    public string? LastProblem { get; private set; }

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

    /// <summary>Start recording a lecture for a class ("" lets the library sort it). Throws InvalidOperationException
    /// with words to show when it can't: the microphone's, or the disk's.</summary>
    public Lecture Start(string className, string owner = "")
    {
        lock (transitions)
        {
            if (Current is { } already) return already;
            if (freeBytes(store.Dir) is < SpaceToStart)
            {
                log("[recorder] not starting: the disk is nearly full");
                throw new InvalidOperationException(RecordingWords.NearlyFull);
            }
            var now = clock();
            var lecture = new Lecture
            {
                Id = Lecture.NewId(now), Started = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
                ClassName = className, Owner = owner,
            };
            try
            {
                store.Add(lecture);
                currentId = lecture.Id;
                Open(append: false, fresh: true);
            }
            catch (Exception e)
            {
                currentId = null;
                log($"[recorder] couldn't start recording: {e.Message}");
                Forget(lecture.Id);
                if (e is IOException or UnauthorizedAccessException)
                    throw new InvalidOperationException(e is IOException io && Disk.IsFull(io) ? RecordingWords.NearlyFull : RecordingWords.CouldntSave(e.Message), e);
                throw;
            }
            LastProblem = null;
            log($"[recorder] recording {lecture.Id} for '{className}'");
        }
        Changed?.Invoke();
        return Current!;
    }

    /// <summary>
    /// The microphone first, then the file, so a microphone that won't open leaves no file behind (and nothing holding
    /// one open), and a file that can't be made leaves no microphone on. <paramref name="fresh"/>: a start or a
    /// resume, which the watchdog watches from the beginning again; the watchdog's own reopening isn't.
    /// </summary>
    void Open(bool append, bool fresh)
    {
        string id = currentId!;
        IAudioSource? s = null;
        IWavSink? file = null;
        var p = Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true });
        try
        {
            s = openSource();
            var r = new Resampler(s.SampleRate, s.Channels);
            if (fresh)
            {
                heard = false;
                reopened = false;
                watchedSince = lastCheckAt = null;
                buffersAtStart = Interlocked.Read(ref buffers);
            }
            failed = null;
            failedAt = lastBufferAt = null;
            buffersSeen = Interlocked.Read(ref buffers);
            // Sound that comes before the file is open waits in the channel.
            pending = p;
            s.Samples += OnSamples;
            s.Failed += OnFailed;
            s.Start();
            file = openFile(store.AudioPath(id), append);
            int turn = ++opened;
            lock (gate)
            {
                source = s;
                wav = file;
            }
            writer = Task.Run(() => WriteAll(file, r, p, id, turn));
            Save(id, l => l.State = LectureState.Recording);
        }
        catch (Exception e)
        {
            log($"[recorder] couldn't open the microphone and the file: {e.Message}");
            p.Writer.TryComplete();
            if (ReferenceEquals(pending, p)) pending = null;
            lock (gate)
            {
                if (ReferenceEquals(source, s)) source = null;
                if (ReferenceEquals(wav, file)) wav = null;
            }
            if (s is not null)
            {
                s.Samples -= OnSamples;
                s.Failed -= OnFailed;
                Quietly(s.Stop, "the microphone didn't stop");
                Quietly(s.Dispose, "the microphone didn't close");
            }
            if (file is not null) Quietly(file.Dispose, "the file didn't close");
            throw;
        }
    }

    /// <summary>The sound system's thread: count the buffer, note whether it's more than silence, pass it on. Never waits.</summary>
    void OnSamples(float[] buffer)
    {
        Interlocked.Increment(ref buffers);
        if (!heard)
        {
            foreach (float s in buffer)
            {
                if (s == 0) continue;
                heard = true;
                break;
            }
        }
        pending?.Writer.TryWrite(buffer);
    }

    /// <summary>The device's thread: the watchdog opens it again (or pauses) a second later, from its own thread,
    /// since closing a device from inside its own event can hang.</summary>
    void OnFailed(string why)
    {
        log($"[recorder] the microphone stopped: {why}");
        failed = why;
    }

    async Task WriteAll(IWavSink file, Resampler r, Channel<float[]> p, string id, int turn)
    {
        try
        {
            await foreach (var buffer in p.Reader.ReadAllAsync()) Write(file, r.Process(buffer));
            Write(file, r.Flush());
        }
        catch (Exception e)
        {
            // Take no more sound (nothing piles up), and pause from another thread: pausing waits for this one.
            p.Writer.TryComplete();
            log($"[recorder] the recording couldn't be written: {e.Message}");
            string why = e is IOException io && Disk.IsFull(io) ? RecordingWords.DiskFull : RecordingWords.CouldntSave(e.Message);
            _ = Task.Run(() => PauseBecause(id, turn, why));
        }
    }

    void Write(IWavSink w, float[] samples)
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

    /// <summary>Stop the microphone and write out what was recorded; the file stays on disk for more. Never throws:
    /// a microphone that won't stop, or a disk that won't take the last of it, is written in the log.</summary>
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
            Quietly(s.Stop, "the microphone didn't stop");
            Quietly(s.Dispose, "the microphone didn't close");
        }
        pending?.Writer.TryComplete();
        if (w is not null && !w.Wait(TimeSpan.FromSeconds(10))) log("[recorder] the recording was still being written after 10 seconds");
        lock (gate)
        {
            if (wav is { } file)
            {
                double seconds = file.Seconds;
                if (currentId is { } id) Save(id, l => l.Seconds = seconds);
                Quietly(file.Dispose, "the recording's file didn't close");
            }
            wav = null;
            pending = null;
            Array.Clear(levels);
        }
    }

    /// <summary>Pause, if recording; true when it did. Inside <see cref="transitions"/>.</summary>
    bool PauseLocked()
    {
        if (Current is not { State: LectureState.Recording } l) return false;
        Close();
        Save(l.Id, x => x.State = LectureState.Paused);
        return true;
    }

    public void Pause()
    {
        bool paused;
        lock (transitions) paused = PauseLocked();
        if (paused) Changed?.Invoke();
    }

    /// <summary>The file couldn't be written: pause the lecture it was writing, if it's still recording to it.</summary>
    void PauseBecause(string id, int turn, string why)
    {
        bool paused;
        lock (transitions)
        {
            paused = currentId == id && turn == opened && PauseLocked();
            if (paused) LastProblem = why;
        }
        if (!paused) return;
        Changed?.Invoke();
        Problem?.Invoke(why);
    }

    /// <summary>Go on recording. When the microphone won't open again or the disk is full, it stays paused and
    /// <see cref="Problem"/> says why.</summary>
    public void Resume()
    {
        string? problem = null;
        lock (transitions)
        {
            if (Current is not { State: LectureState.Paused }) return;
            if (freeBytes(store.Dir) is < SpaceToKeepGoing)
            {
                problem = RecordingWords.DiskFull;
            }
            else
            {
                try
                {
                    Open(append: true, fresh: true);
                }
                catch (Exception e)
                {
                    problem = e is IOException io && Disk.IsFull(io) ? RecordingWords.DiskFull : e.Message;
                }
            }
            LastProblem = problem;
        }
        if (problem is not null) Problem?.Invoke(problem);
        else Changed?.Invoke();
    }

    /// <summary>
    /// The watchdog, every second or so (<paramref name="now"/> from any clock that goes forward). While recording:
    /// under 100 MB free, it pauses; a microphone that failed, or sent nothing for 3 seconds, is opened again once
    /// (the new default one after an unplug, the same one after a nap), and pauses when that doesn't bring it back;
    /// nothing but exact silence in the first 5 seconds after a start or a resume is the system not letting Study
    /// Stash hear it, and pauses. A quiet room later on is only quiet: it's never flagged.
    /// </summary>
    public void Check(DateTime now)
    {
        string? problem = null;
        lock (transitions)
        {
            if (Current is not { State: LectureState.Recording } l)
            {
                lastCheckAt = now;
                return;
            }
            if (lastCheckAt is { } last && now - last > Napped)
            {
                // The computer slept: the recording skips that time. Give the microphone a moment to come back.
                log($"[recorder] {l.Id}: nothing for {TimedText.Length((now - last).TotalSeconds)} (the computer slept?): the recording skips it");
                lastBufferAt = now;
            }
            lastCheckAt = now;
            watchedSince ??= now;
            lastBufferAt ??= now;
            long n = Interlocked.Read(ref buffers);
            if (n != buffersSeen)
            {
                buffersSeen = n;
                lastBufferAt = now;
                reopened = false;
            }

            if (freeBytes(store.Dir) is < SpaceToKeepGoing)
            {
                problem = RecordingWords.DiskFull;
            }
            else if (failed is { } why)
            {
                failedAt ??= now;
                if (now - failedAt >= AfterFailing && !TryReopen(now, $"it failed ({why})", out string? error)) problem = error ?? why;
            }
            else if (now - lastBufferAt >= QuietFor)
            {
                if (!TryReopen(now, "nothing came for 3 seconds", out _)) problem = RecordingWords.MicStopped;
            }
            else if (!heard && n > buffersAtStart && now - watchedSince >= SilentFor)
            {
                log($"[recorder] {l.Id}: only silence from the microphone: it isn't allowed");
                problem = RecordingWords.CantHear;
            }
            if (problem is not null)
            {
                PauseLocked();
                LastProblem = problem;
            }
        }
        if (problem is null) return;
        log($"[recorder] paused: {problem}");
        Changed?.Invoke();
        Problem?.Invoke(problem);
    }

    /// <summary>Close the microphone and open it again, once until sound comes again; false when it was already
    /// tried or won't open (<paramref name="error"/>: the microphone's words).</summary>
    bool TryReopen(DateTime now, string why, out string? error)
    {
        error = null;
        if (reopened) return false;
        reopened = true;
        log($"[recorder] the microphone: {why}: opened it again");
        Close();
        try
        {
            Open(append: true, fresh: false);
        }
        catch (Exception e)
        {
            error = e is IOException io && Disk.IsFull(io) ? RecordingWords.DiskFull : e.Message;
            return false;
        }
        lastBufferAt = now;
        return true;
    }

    /// <summary>Stop recording: the lecture goes to Whisper for the rest, then to the library. Null when nothing
    /// was recording, or when it was too short to keep (it's thrown away). A recording that can't be read back is
    /// kept and marked Failed ("The recording couldn't be read"); nothing is thrown.</summary>
    public Lecture? Stop(double keepAtLeastSeconds = 5)
    {
        Lecture? l;
        lock (transitions)
        {
            l = Current;
            if (l is null) return null;
            if (l.State == LectureState.Recording) Close();
            currentId = null;
            LastProblem = null;
            string audio = store.AudioPath(l.Id);
            double? seconds;
            try
            {
                seconds = File.Exists(audio) ? Sound.WavSeconds(audio) : 0;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                log($"[recorder] {l.Id}: the recording couldn't be read: {e.Message}");
                seconds = null;
            }
            if (seconds is not { } s)
            {
                l = Save(l.Id, x =>
                {
                    x.State = LectureState.Failed;
                    x.Error = RecordingWords.CouldntRead;
                });
            }
            else if (s < keepAtLeastSeconds)
            {
                Forget(l.Id);
                log($"[recorder] {l.Id} was {s:0.0}s: not kept");
                l = null;
            }
            else
            {
                l = Save(l.Id, x =>
                {
                    x.Seconds = s;
                    x.State = LectureState.Transcribing;
                });
                log($"[recorder] stopped {l!.Id} after {TimedText.Clock(s)}");
            }
        }
        Changed?.Invoke();
        return l;
    }

    /// <summary>Change the lecture and save it. A full disk can't take its record: the change holds in memory (and is
    /// written with the next change that fits), so recording carries on.</summary>
    Lecture? Save(string id, Action<Lecture> change)
    {
        try
        {
            return store.Update(id, change);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"[recorder] {id}: couldn't save its record: {e.Message}");
            return store.Get(id);
        }
    }

    void Forget(string id)
    {
        try
        {
            store.Delete(id);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"[recorder] couldn't delete {id}: {e.Message}");
        }
    }

    void Quietly(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            log($"[recorder] {what}: {e.Message}");
        }
    }

    /// <summary>A lecture left recording when the app last quit or crashed: it's stopped now, keeping what was
    /// recorded, and goes on to Whisper. One whose recording can't be opened (in use, not allowed) is marked Failed,
    /// and the app still starts.</summary>
    public static int Recover(LectureStore store, Action<string>? log = null)
    {
        int n = 0;
        foreach (var l in store.All().Where(l => l.State is LectureState.Recording or LectureState.Paused))
        {
            string audio = store.AudioPath(l.Id);
            try
            {
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"[recorder] {l.Id} was cut short and can't be opened: {e.Message}");
                try
                {
                    store.Update(l.Id, x =>
                    {
                        x.State = LectureState.Failed;
                        x.Error = RecordingWords.CouldntRead;
                    });
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"[recorder] {l.Id}: couldn't save its record: {again.Message}");
                }
            }
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
/// how far it got, so a restart carries on where it left off. Whisper that won't load leaves the lectures waiting
/// (and says why in <see cref="Problem"/>); a piece Whisper fails on is tried three times before the lecture fails,
/// and a failed lecture can be tried again (<see cref="Retry"/>).
/// </summary>
public sealed class TranscriptionWorker(LectureStore store, Func<ITranscriber> load, Func<Lecture?> recording,
    Action<string>? log = null, double minSeconds = 20, double maxSeconds = 29.5)
{
    const int TriesPerPiece = 3;
    static readonly TimeSpan LoadAgainAfter = TimeSpan.FromMinutes(1);
    readonly Action<string> log = log ?? (_ => { });
    // Counts wakes; one look takes them all (see RunAsync).
    readonly SemaphoreSlim wake = new(0);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> tries = new();
    ITranscriber? model;
    DateTime usedAt;
    DateTime? loadFailedAt;
    volatile bool loadNow;

    /// <summary>A lecture got new words: the recorder window shows them.</summary>
    public event Action<Lecture, IReadOnlyList<Spoken>>? Heard;

    /// <summary>A lecture finished transcribing (it's ready for the library), or failed.</summary>
    public event Action<Lecture>? Finished;

    /// <summary><see cref="Problem"/> changed.</summary>
    public event Action? ProblemChanged;

    /// <summary>Why Whisper isn't writing lectures down ("Whisper couldn't start: …"); null while it works.</summary>
    public string? Problem { get; private set; }

    /// <summary>Keep Whisper in memory this long after its last piece of work (it's gigabytes).</summary>
    public TimeSpan KeepLoaded { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>The time, for keeping Whisper loaded and trying it again (a test's own clock).</summary>
    public Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    /// <summary>There's work, or something changed (a model arrived): look now, and load Whisper now if it failed.</summary>
    public void Wake()
    {
        loadNow = true;
        wake.Release();
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
            float[] window;
            try
            {
                window = Sound.ReadWav(audio, from, (long)(maxSeconds * Sound.Rate));
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                log($"[whisper] {l.Id}: couldn't read the recording: {e.Message}");
                if (!isLive) Fail(l.Id, RecordingWords.CouldntRead);
                continue;
            }
            int take = Segmenter.CutLength(window, final: !isLive, minSeconds, maxSeconds);
            if (take == 0) continue; // a live lecture without a full piece yet
            return await TranscribeAsync(l, isLive, new Chunk(from, window[..take]), stop);
        }
        return false;
    }

    /// <summary>One piece: false when Whisper isn't there to do it (it won't load).</summary>
    async Task<bool> TranscribeAsync(Lecture l, bool isLive, Chunk chunk, CancellationToken stop)
    {
        var heard = new List<Spoken>();
        string language = "";
        if (!chunk.Silent())
        {
            if (Model() is not { } whisper) return false;
            string prompt = string.Join(" ", l.Segments.TakeLast(6).Select(s => s.Text));
            Transcription t;
            try
            {
                t = await whisper.TranscribeAsync(chunk.Samples, prompt.Length > 600 ? prompt[^600..] : prompt, l.Language, stop);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                int n = tries.AddOrUpdate(l.Id, 1, (_, k) => k + 1);
                log($"[whisper] {l.Id}: {e.Message} (try {n} of {TriesPerPiece})");
                // A lecture still recording isn't failed under the recorder: its piece waits for the next look.
                if (isLive && n >= TriesPerPiece) return false;
                if (n < TriesPerPiece) return true;
                tries.TryRemove(l.Id, out _);
                Fail(l.Id, $"Whisper couldn't write part of it down: {e.Message}");
                return true;
            }
            usedAt = Clock();
            foreach (var s in t.Segments)
            {
                string text = TimedText.Tidy(s.Text);
                if (text.Length == 0) continue;
                heard.Add(new Spoken(chunk.StartSeconds + s.Start, chunk.StartSeconds + s.End, text));
            }
            language = t.Language;
        }
        tries.TryRemove(l.Id, out _);
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
        if (saved is null) return true; // deleted meanwhile
        if (heard.Count > 0) Heard?.Invoke(saved, heard);
        if (done)
        {
            log($"[whisper] {saved.Id}: {saved.Segments.Count} lines from {TimedText.Clock(saved.Seconds)}");
            Finished?.Invoke(saved);
        }
        return true;
    }

    /// <summary>Whisper, loaded if it isn't yet. Null when it won't load: <see cref="Problem"/> says why, and it's
    /// tried again after a minute, or at the next <see cref="Wake"/>.</summary>
    ITranscriber? Model()
    {
        if (model is not null) return model;
        var now = Clock();
        if (loadFailedAt is { } failedAt && now - failedAt < LoadAgainAfter && !loadNow) return null;
        loadNow = false;
        log("[whisper] loading the model");
        try
        {
            model = load();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            loadFailedAt = now;
            log($"[whisper] couldn't load the model: {e.Message}");
            SetProblem($"Whisper couldn't start: {e.Message}");
            return null;
        }
        loadFailedAt = null;
        SetProblem(null);
        return model;
    }

    void SetProblem(string? problem)
    {
        if (Problem == problem) return;
        Problem = problem;
        ProblemChanged?.Invoke();
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

    /// <summary>Try a failed lecture again: back to Whisper, or on to the library when it was all written down
    /// already. Null when there's no such failed lecture.</summary>
    public Lecture? Retry(string id)
    {
        bool retried = false;
        var l = store.Update(id, x =>
        {
            if (x.State != LectureState.Failed) return;
            x.State = x.Seconds > 0 && x.TranscribedSeconds >= x.Seconds - 0.01 ? LectureState.Sending : LectureState.Transcribing;
            x.Error = "";
            x.RetryAt = null;
            x.Tries = 0;
            retried = true;
        });
        if (!retried) return null;
        tries.TryRemove(id, out _);
        log($"[whisper] {id}: trying again ({l!.State})");
        Wake();
        return l;
    }

    /// <summary>Work until <paramref name="stop"/>: a piece at a time, then a look every few seconds while a lecture
    /// records (every 30 when none does), or at once when woken.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (true)
            {
                stop.ThrowIfCancellationRequested();
                bool worked;
                try
                {
                    worked = await StepAsync(stop);
                }
                catch (Exception e) when (!stop.IsCancellationRequested)
                {
                    log($"[whisper] {e.Message}");
                    worked = false;
                }
                if (worked) continue;
                if (model is not null && Clock() - usedAt > KeepLoaded && recording() is null)
                {
                    model.Dispose();
                    model = null;
                    log("[whisper] unloaded the model");
                }
                await wake.WaitAsync(TimeSpan.FromSeconds(recording() is null ? 30 : 3), stop);
                while (wake.Wait(0)) { } // this look answers every wake so far
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            log("[whisper] stopped");
        }
        finally
        {
            model?.Dispose();
            model = null;
        }
    }
}

/// <summary>
/// Takes transcribed lectures to the library, and asks after them until they're filed. A library that's off, or
/// out of reach, gets the lecture later; nothing is lost while the laptop is away from it. The waits between tries
/// are for a library that's still away: the first pass after the app starts, and <see cref="RetryNow"/> (the library
/// answered again), send what's waiting at once.
/// </summary>
public sealed class LectureSender(LectureStore store, Func<ClientConfig> config, LaptopHost host, Action<string>? log = null)
{
    static readonly double[] Backoff = [30, 60, 120, 300, 600];
    readonly Action<string> log = log ?? (_ => { });
    readonly string home = Path.GetDirectoryName(store.Dir)!; // where timetable.json is
    // Counts wakes; one look takes them all (see RunAsync).
    readonly SemaphoreSlim wake = new(0);
    // Lectures whose library didn't answer when asked after them: said once in the log, not every 10 seconds.
    readonly HashSet<string> unanswered = [];
    bool started;

    /// <summary>A lecture's state changed: sent, filed, or refused.</summary>
    public event Action<Lecture>? Changed;

    /// <summary>The last answer from the library: null while all is well, else why lectures are waiting.</summary>
    public string? Problem { get; private set; }

    /// <summary>"password", "unreachable" or "other" while lectures can't reach the library.</summary>
    public string? ProblemKind { get; private set; }

    public void Wake() => wake.Release();

    /// <summary>The library answers again: lectures waiting out a failed try go now, not when their wait ends.</summary>
    public void RetryNow()
    {
        int n = 0;
        foreach (var l in store.All().Where(l => l.State == LectureState.Sending && l.RetryAt is not null))
        {
            store.Update(l.Id, x => x.RetryAt = null);
            n++;
        }
        if (n > 0) log($"[send] the library answers: sending {n} waiting now");
        Wake();
    }

    public async Task<int> StepAsync()
    {
        var cc = config();
        if (cc.ServerUrl.Length == 0) return 0;
        string server = cc.ServerUrl.TrimEnd('/');
        double now = host.Clock().ToUnixTimeMilliseconds() / 1000.0;
        // A wait saved before the app last quit was for a library that may well be back now.
        bool first = !started;
        started = true;
        int n = 0;
        foreach (var l in store.All())
        {
            Lecture? changed = null;
            if (l.State == LectureState.Sending && (first || l.RetryAt is null || l.RetryAt <= now))
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
                    if (unanswered.Add(l.Id)) log($"[send] {l.Id}: the library didn't say how it is ({e.Message}); asking again");
                    continue;
                }
                unanswered.Remove(l.Id);
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

    /// <summary>Send and ask until <paramref name="stop"/>: every 10 seconds while the library writes notes, every
    /// 30 otherwise, or at once when woken.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        try
        {
            while (true)
            {
                stop.ThrowIfCancellationRequested();
                try
                {
                    await StepAsync();
                }
                catch (Exception e)
                {
                    log($"[send] {e.Message}");
                }
                bool writing = store.All().Any(l => l.State == LectureState.Writing);
                await wake.WaitAsync(TimeSpan.FromSeconds(writing ? 10 : 30), stop);
                while (wake.Wait(0)) { } // this look answers every wake so far
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            log("[send] stopped");
        }
    }
}
