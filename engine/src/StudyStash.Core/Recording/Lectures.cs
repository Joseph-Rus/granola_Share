using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyStash.Core;

/// <summary>Where a recorded lecture is on its way to the library.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LectureState>))]
public enum LectureState
{
    /// <summary>The microphone is on.</summary>
    Recording,
    Paused,
    /// <summary>Recorded; Whisper is still writing down the rest.</summary>
    Transcribing,
    /// <summary>Transcribed; waiting to reach the library (it's off, or this laptop is away from it).</summary>
    Sending,
    /// <summary>The library has it and is writing the notes.</summary>
    Writing,
    Filed,
    Failed,
}

/// <summary>
/// One recorded lecture, as the laptop keeps it (recordings/&lt;id&gt;.json beside its .wav): what was recorded, what
/// Whisper has written down so far, and whether the library has filed it. The audio never leaves the laptop; the
/// library gets the transcript.
/// </summary>
public sealed class Lecture
{
    public required string Id { get; init; }
    /// <summary>The class it was recorded for; empty lets the library sort it.</summary>
    public string ClassName { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>When recording started, with the laptop's UTC offset: "2026-09-23T10:02:12-07:00".</summary>
    public required string Started { get; init; }
    public double Seconds { get; set; }
    public LectureState State { get; set; } = LectureState.Recording;
    /// <summary>How far into the audio Whisper has got.</summary>
    public double TranscribedSeconds { get; set; }
    public List<Spoken> Segments { get; set; } = [];
    public string Language { get; set; } = "";
    /// <summary>Who recorded it, as the library shows it.</summary>
    public string Owner { get; set; } = "";
    /// <summary>What the library filed it as, once it has: the class and the lecture's title there.</summary>
    public string FiledClass { get; set; } = "";
    public string FiledTitle { get; set; } = "";
    public string Error { get; set; } = "";
    /// <summary>When a send last failed, so the next try waits (seconds since 1970).</summary>
    public double? RetryAt { get; set; }
    public int Tries { get; set; }

    [JsonIgnore]
    public double Progress => Seconds <= 0 ? 0 : Math.Clamp(TranscribedSeconds / Seconds, 0, 1);

    [JsonIgnore]
    public DateTimeOffset StartedAt => DateTimeOffset.Parse(Started, CultureInfo.InvariantCulture);

    public string Transcript() => TimedText.Format(Segments);

    /// <summary>A new lecture's id: when it started, and a little randomness so two laptops never clash.</summary>
    public static string NewId(DateTimeOffset now) =>
        "rec-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));

    /// <summary>What the library's /api/ingest takes.</summary>
    public System.Text.Json.Nodes.JsonObject Payload()
    {
        string title = Title.Length > 0 ? Title
            : ClassName.Length > 0 ? $"{ClassName} lecture, {StartedAt.ToString("ddd d MMM", CultureInfo.InvariantCulture)}"
            : $"Lecture, {StartedAt.ToString("ddd d MMM", CultureInfo.InvariantCulture)}";
        return new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = Id,
            ["title"] = title,
            ["date"] = Started,
            ["owner"] = Owner,
            ["folder"] = ClassName,
            ["transcript"] = Transcript(),
            ["raw"] = new System.Text.Json.Nodes.JsonObject
            {
                ["source"] = "recorder", ["seconds"] = Math.Round(Seconds, 1), ["language"] = Language,
            },
        };
    }
}

/// <summary>
/// The laptop's recordings folder: each lecture's .json and .wav. The recorder, Whisper and the sender all change
/// lectures, each on its own thread, so every change goes through <see cref="Update"/>; a lecture's list of
/// segments is replaced, never added to in place, so the screen can read one while Whisper writes.
/// </summary>
public sealed class LectureStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    readonly Lock gate = new();
    Dictionary<string, Lecture>? cache;

    public string Dir { get; }

    public LectureStore(string home)
    {
        Dir = Path.Combine(home, "recordings");
        Directory.CreateDirectory(Dir);
    }

    public string AudioPath(string id) => Path.Combine(Dir, id + ".wav");
    string MetaPath(string id) => Path.Combine(Dir, id + ".json");

    Dictionary<string, Lecture> Cache()
    {
        if (cache is not null) return cache;
        cache = [];
        foreach (string f in Directory.EnumerateFiles(Dir, "rec-*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Lecture>(File.ReadAllText(f), Json) is { } l) cache[l.Id] = l;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }
        return cache;
    }

    void Write(Lecture l)
    {
        string path = MetaPath(l.Id), tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(l, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public Lecture Add(Lecture l)
    {
        lock (gate)
        {
            Cache()[l.Id] = l;
            Write(l);
            return l;
        }
    }

    public Lecture? Get(string id)
    {
        lock (gate) return Cache().GetValueOrDefault(id);
    }

    /// <summary>Change a lecture and save it; null when there's no such lecture.</summary>
    public Lecture? Update(string id, Action<Lecture> change)
    {
        lock (gate)
        {
            if (!Cache().TryGetValue(id, out var l)) return null;
            change(l);
            Write(l);
            return l;
        }
    }

    /// <summary>Every lecture, newest first.</summary>
    public List<Lecture> All()
    {
        lock (gate) return Cache().Values.OrderByDescending(l => l.Started, StringComparer.Ordinal).ToList();
    }

    public void Delete(string id)
    {
        lock (gate)
        {
            Cache().Remove(id);
            File.Delete(MetaPath(id));
            File.Delete(AudioPath(id));
        }
    }

    /// <summary>Drop the audio of filed lectures older than this many days: the transcript and notes stay.</summary>
    public int PruneAudio(int days, DateTimeOffset now)
    {
        if (days <= 0) return 0;
        int n = 0;
        foreach (var l in All())
        {
            if (l.State != LectureState.Filed || (now - l.StartedAt).TotalDays < days) continue;
            string audio = AudioPath(l.Id);
            if (!File.Exists(audio)) continue;
            File.Delete(audio);
            n++;
        }
        return n;
    }
}
