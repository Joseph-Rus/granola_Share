using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudyStash.Audio;
using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>The app's own settings (app.json beside client.toml): what's been set up, and how to record.</summary>
public sealed class AppSettings
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public bool SetupDone { get; set; }
    /// <summary>The Whisper model's id; empty picks the one for this computer.</summary>
    public string Model { get; set; } = "";
    /// <summary>"" finds each lecture's language; "en" and so on fixes it.</summary>
    public string Language { get; set; } = "";
    /// <summary>Windows: record what the computer plays too (a lecture on Zoom).</summary>
    public bool ComputerAudio { get; set; }
    /// <summary>Filed lectures' audio is deleted after this many days (the notes and transcript stay). 0 keeps it.</summary>
    public int KeepAudioDays { get; set; } = 30;
    /// <summary>This computer is the library too (it runs the library's service).</summary>
    public bool LibraryHere { get; set; }
    public bool Shortcuts { get; set; } = true;
    public double? RecorderX { get; set; }
    public double? RecorderY { get; set; }

    public static string PathIn(string home) => System.IO.Path.Combine(home, "app.json");

    public static AppSettings Load(string home)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathIn(home)), Json) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(string home)
    {
        Directory.CreateDirectory(home);
        string path = PathIn(home), tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>How the library answered lately.</summary>
public enum LibraryState
{
    NotSetUp,
    Connected,
    Unreachable,
    WrongPassword,
}

/// <summary>
/// Everything the app runs, apart from its windows: the recorder, Whisper and the sender on their threads, the
/// library's classes (asked for every 20 seconds, which also says whether it's reachable), the model and its
/// download, and the timetable. The windows read it and are told when it changes.
/// </summary>
public sealed class AppHost : IDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly Action<string> log;
    KeepAwake? awake;
    CancellationTokenSource? download;

    public string Home { get; }
    public AppSettings Settings { get; private set; }
    public LectureStore Lectures { get; }
    public Recorder Recorder { get; }
    public TranscriptionWorker Whisper { get; }
    public LectureSender Sender { get; }
    public Timetable Timetable { get; private set; }

    public LibraryState Library { get; private set; } = LibraryState.NotSetUp;
    /// <summary>The library is from before /api/v2 (the Python engine): it files lectures, but can't be browsed, searched
    /// or asked from the app until it's updated.</summary>
    public bool OlderLibrary { get; private set; }
    /// <summary>The library's name, classes (in order: their colors) and the rest of /api/v2/library.</summary>
    public JsonObject? Overview { get; private set; }
    public DownloadProgress? Downloading { get; private set; }
    public string? DownloadProblem { get; private set; }

    /// <summary>Anything the windows show changed (called on a worker thread).</summary>
    public event Action? Changed;
    /// <summary>A lecture was filed: its title and class, for a notification.</summary>
    public event Action<Lecture>? Filed;
    /// <summary>Whisper wrote down more of the lecture being recorded.</summary>
    public event Action<Lecture, IReadOnlyList<Spoken>>? Heard;
    /// <summary>Something went wrong that the person should hear about now.</summary>
    public event Action<string>? Problem;

    public AppHost(string home, Func<IAudioSource>? microphone = null, Func<ITranscriber>? whisper = null, LaptopHost? laptop = null,
        Action<string>? log = null)
    {
        Home = home;
        this.log = log ?? (s => Console.WriteLine(s));
        Directory.CreateDirectory(home);
        Settings = AppSettings.Load(home);
        Timetable = Timetable.Load(home);
        Lectures = new LectureStore(home);
        Recorder.Recover(Lectures, this.log);
        Recorder = new Recorder(Lectures, microphone ?? (() => Microphones.Open(Settings.ComputerAudio)), log: this.log);
        Whisper = new TranscriptionWorker(Lectures, whisper ?? LoadWhisper, () => Recorder.Current, this.log);
        var net = laptop ?? new LaptopHost();
        Sender = new LectureSender(Lectures, Client, net, this.log);
        Recorder.Changed += () => Changed?.Invoke();
        Recorder.Problem += why => Problem?.Invoke(why);
        Whisper.Heard += (l, lines) =>
        {
            Heard?.Invoke(l, lines);
            Changed?.Invoke();
        };
        Whisper.Finished += _ =>
        {
            Sender.Wake();
            Changed?.Invoke();
        };
        Sender.Changed += l =>
        {
            if (l.State == LectureState.Filed) Filed?.Invoke(l);
            Changed?.Invoke();
        };
    }

    public ClientConfig Client() => Configs.LoadClient(Home);

    /// <summary>The library over its API, once one is set up.</summary>
    public RemoteLibrary? Remote()
    {
        var cc = Client();
        return cc.ServerUrl.Length > 0 ? new RemoteLibrary(cc.ServerUrl, cc.PoolKey) : null;
    }

    public WhisperModel Model => WhisperModels.Find(Settings.Model)
        ?? WhisperModels.Recommended(Machine.Platform, RuntimeInformation.OSArchitecture, Machine.TotalRamGb());

    /// <summary>STUDYSTASH_MODEL_FILE: a model file to use instead of the downloaded one (trying the app with a small one).</summary>
    static string? ModelFile => Environment.GetEnvironmentVariable("STUDYSTASH_MODEL_FILE") is { Length: > 0 } f && File.Exists(f) ? f : null;

    public bool ModelReady => ModelFile is not null || WhisperModels.IsDownloaded(Home, Model);

    ITranscriber LoadWhisper()
    {
        if (!ModelReady) throw new InvalidOperationException("The transcription model isn't downloaded yet.");
        return new WhisperTranscriber(ModelFile ?? WhisperModels.PathFor(Home, Model), Settings.Language);
    }

    public void Start()
    {
        _ = Task.Run(() => Whisper.RunAsync(stop.Token));
        _ = Task.Run(() => Sender.RunAsync(stop.Token));
        _ = Task.Run(WatchLibrary);
        _ = Task.Run(() => Lectures.PruneAudio(Settings.KeepAudioDays, DateTimeOffset.Now));
    }

    async Task WatchLibrary()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await CheckLibraryAsync();
            }
            catch (Exception e)
            {
                log($"[library] {e.Message}");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Library == LibraryState.Connected ? 20 : 10), stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Ask the library how it is; its classes come back, and they keep the timetable honest.</summary>
    public async Task CheckLibraryAsync()
    {
        var before = Library;
        if (Remote() is not { } lib)
        {
            Library = LibraryState.NotSetUp;
        }
        else
        {
            try
            {
                try
                {
                    Overview = await lib.OverviewAsync();
                    OlderLibrary = false;
                }
                catch (LibraryRefusedException e) when (e.Status == 404)
                {
                    // The Python engine's library: its classes from /api/health, in its order.
                    var cc = Client();
                    var health = await LibraryApi.CheckServerAsync(cc.ServerUrl, cc.PoolKey);
                    int i = 0;
                    Overview = new JsonObject
                    {
                        ["name"] = health["pool_name"]?.DeepClone(),
                        ["classes"] = new JsonArray((health["classes"] as JsonArray ?? []).Select(n => (JsonNode?)new JsonObject { ["name"] = n?.DeepClone(), ["lectures"] = 0, ["color"] = i++ }).ToArray()),
                        ["unsorted"] = 0,
                        ["ask"] = false,
                    };
                    OlderLibrary = true;
                }
                Library = LibraryState.Connected;
                var names = (Overview["classes"] as JsonArray ?? []).Select(c => c?["name"]?.GetValue<string>() ?? "").ToList();
                if (!OlderLibrary && names.Count > 0 && Timetable.KeepOnly(names)) Timetable.Save(Home);
            }
            catch (InvalidOperationException e) when (e.Message == "wrong password")
            {
                Library = LibraryState.WrongPassword;
            }
            catch (InvalidOperationException)
            {
                Library = LibraryState.Unreachable;
            }
            catch (LibraryRefusedException e) when (e.Status is 401 or 403)
            {
                Library = LibraryState.WrongPassword;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException or JsonException)
            {
                Library = LibraryState.Unreachable;
            }
        }
        if (before != Library && Library == LibraryState.Connected) Sender.Wake();
        Changed?.Invoke();
    }

    /// <summary>The library's classes, in its order: (name, color index, lectures).</summary>
    public List<(string Name, int Color, int Lectures)> Classes() =>
        (Overview?["classes"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => (c["name"]?.GetValue<string>() ?? "", c["color"]?.GetValue<int>() ?? 0, c["lectures"]?.GetValue<int>() ?? 0)).ToList();

    public int ColorOf(string className) => Classes().FirstOrDefault(c => c.Name == className) is { Name.Length: > 0 } c ? c.Color : -1;

    /// <summary>"Library connected · Model ready", or what needs doing, and whether all is well.</summary>
    public (string Text, bool Good) Status()
    {
        string lib = Library switch
        {
            LibraryState.Connected => "Library connected",
            LibraryState.Unreachable => "Can't reach your library",
            LibraryState.WrongPassword => "Library password changed",
            _ => "No library yet",
        };
        string model = ModelReady ? "Model ready" : Downloading is { } d ? $"Model {Math.Round(d.Fraction * 100)}%" : "No transcription model";
        return ($"{lib} · {model}", Library == LibraryState.Connected && ModelReady);
    }

    public void Save(Action<AppSettings> change)
    {
        change(Settings);
        Settings.Save(Home);
        Changed?.Invoke();
    }

    public void SaveTimetable(Timetable t)
    {
        Timetable = t;
        t.Save(Home);
        Changed?.Invoke();
    }

    // --- recording ------------------------------------------------------------------------------------------------

    /// <summary>The class Record means now, from the timetable: its name and "Tue 10:00–11:15", or nothing.</summary>
    public ClassNow? ClassNow() => Timetable.Now(DateTime.Now);

    public Lecture StartRecording(string className)
    {
        if (!ModelReady) throw new InvalidOperationException("Download the transcription model first (Settings → Recording).");
        var l = Recorder.Start(className, Client().DisplayName);
        awake ??= new KeepAwake("Recording a lecture");
        Whisper.Wake();
        return l;
    }

    public void Pause() => Recorder.Pause();

    public void Resume() => Recorder.Resume();

    public Lecture? StopRecording()
    {
        var l = Recorder.Stop();
        awake?.Dispose();
        awake = null;
        Whisper.Wake();
        return l;
    }

    // --- the model --------------------------------------------------------------------------------------------------

    /// <summary>Download the model for this computer (or pick up a download a closed laptop cut short).</summary>
    public async Task DownloadModelAsync(WhisperModel? model = null)
    {
        model ??= Model;
        if (WhisperModels.IsDownloaded(Home, model) || download is not null) return;
        download = new CancellationTokenSource();
        DownloadProblem = null;
        Downloading = new DownloadProgress(0, model.Bytes, 0);
        Changed?.Invoke();
        try
        {
            await ModelDownload.RunAsync(Home, model, new Progress<DownloadProgress>(p =>
            {
                Downloading = p;
                Changed?.Invoke();
            }), download.Token);
            log($"[model] {model.Name} downloaded");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException)
        {
            DownloadProblem = e is HttpRequestException ? "The download stopped (no internet?). It picks up where it left off." : e.Message;
            log($"[model] {e.Message}");
        }
        finally
        {
            download.Dispose();
            download = null;
            Downloading = null;
            Changed?.Invoke();
            Whisper.Wake();
        }
    }

    public void Dispose()
    {
        if (Recorder.Current is not null) StopRecording();
        stop.Cancel();
        download?.Cancel();
        awake?.Dispose();
    }
}
