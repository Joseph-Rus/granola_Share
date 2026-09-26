using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyStash.Core.Canvas;

/// <summary>
/// Canvas, as this library knows it (canvas.json beside config.toml): the school's Canvas address, which Canvas
/// course each class is, and how the last sync went. Canvas is read through a small Chrome extension, with the
/// person's own sign-in, because many schools turn off Canvas's access tokens. Nothing here writes to Canvas.
/// </summary>
public sealed class CanvasSettings
{
    /// <summary>The school's Canvas, like https://school.instructure.com. Empty: Canvas isn't set up.</summary>
    public string Url { get; set; } = "";
    /// <summary>Class name → Canvas course id.</summary>
    public Dictionary<string, long> Courses { get; set; } = [];
    /// <summary>The person's Canvas courses (id → name), as last looked up, for picking which class is which.</summary>
    public Dictionary<string, string> Available { get; set; } = [];
    /// <summary>The same courses' code and term (id → info), for <see cref="CourseMatch"/> and the classes screen.</summary>
    public Dictionary<string, CourseInfo> CourseInfo { get; set; } = [];
    /// <summary>How often the extension reads Canvas again.</summary>
    public int PollMinutes { get; set; } = 60;
    /// <summary>When the last sync started (ISO), and when the extension last asked for work.</summary>
    public string LastSync { get; set; } = "";
    /// <summary>When the last sync finished (ISO); "" before any sync has finished. <see cref="LastSync"/> is when it
    /// started, which is what scheduling (<see cref="Due"/>) goes by.</summary>
    public string LastDone { get; set; } = "";
    /// <summary>When the current <see cref="Error"/> started (ISO); "" while there's no error.</summary>
    public string ErrorAt { get; set; } = "";
    public string ExtensionSeen { get; set; } = "";
    /// <summary>The version of the extension Chrome last ran ("1.3").</summary>
    public string ExtensionVersion { get; set; } = "";
    /// <summary>Chrome's extension updated itself (it reloads from a folder Study Stash keeps up to date): Settings
    /// says so ("The Chrome extension updated itself. Now version 1.3.") until it's dismissed.</summary>
    public ExtensionUpdate? ExtensionUpdate { get; set; }
    /// <summary>What went wrong last, for Settings; empty when all is well.</summary>
    public string Error { get; set; } = "";
    /// <summary>Chrome isn't signed in to Canvas: syncing waits until it is.</summary>
    public bool NeedsLogin { get; set; }
    /// <summary>Sync on the extension's next visit instead of waiting for <see cref="PollMinutes"/>.</summary>
    public bool SyncNow { get; set; }
    /// <summary>What changed in the last sync that changed anything ("New: CS 101 · Lab 3 · due Tue 11:59 PM").</summary>
    public List<string> Changes { get; set; } = [];
    /// <summary>The same changes with what each is about (kind, class, assignment), for the app's list and notifications.</summary>
    public List<CanvasChange> LastChanges { get; set; } = [];
    /// <summary>What the course scout said last, per class.</summary>
    public Dictionary<string, ScoutReport> Scouts { get; set; } = [];

    [JsonIgnore] public bool On => Url.Length > 0;

    /// <summary>The extension Chrome runs is older than this library's: its folder wasn't brought up to date (one the
    /// laptop app made, say), so it can't reload into the new version by itself.</summary>
    [JsonIgnore] public bool ExtensionOutdated => Extension.IsOlder(ExtensionVersion, Extension.Version());

    public static string PathIn(string home) => Path.Combine(home, "canvas.json");

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    static readonly Lock Gate = new();

    public static CanvasSettings Load(string home)
    {
        lock (Gate)
        {
            string p = PathIn(home);
            if (!File.Exists(p)) return new CanvasSettings();
            try
            {
                return JsonSerializer.Deserialize<CanvasSettings>(File.ReadAllText(p), Options) ?? new CanvasSettings();
            }
            catch (JsonException)
            {
                return new CanvasSettings();
            }
        }
    }

    public void Save(string home)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(home);
            string p = PathIn(home), tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options) + "\n");
            File.Move(tmp, p, overwrite: true);
        }
    }

    /// <summary>Load, change and save in one step, so the web pages and the sync don't lose each other's changes.</summary>
    public static CanvasSettings Update(string home, Action<CanvasSettings> change)
    {
        lock (Gate)
        {
            var s = Load(home);
            change(s);
            s.Save(home);
            return s;
        }
    }

    /// <summary>Time for the extension to read Canvas again.</summary>
    public bool Due(DateTimeOffset now) =>
        On && Courses.Count > 0 && (SyncNow || !DateTimeOffset.TryParse(LastSync, out var last) || last.AddMinutes(PollMinutes) <= now);

    /// <summary>"https://school.instructure.com" from whatever was pasted (a course page, no scheme, a trailing slash).
    /// Null when it isn't a web address.</summary>
    public static string? CleanUrl(string typed)
    {
        string t = Py.Strip(typed);
        if (t.Length == 0) return null;
        if (!t.Contains("://", StringComparison.Ordinal)) t = "https://" + t;
        return Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" && u.Host.Contains('.')
            ? $"{u.Scheme}://{u.Authority}" : null;
    }

    /// <summary>The extension's own key (canvas_key, made once): it may only hand Canvas pages to this library.</summary>
    public static string ExtensionKey(string home)
    {
        string p = Path.Combine(home, "canvas_key");
        if (!File.Exists(p))
        {
            Directory.CreateDirectory(home);
            Py.WriteText(p, Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
            Py.OwnerOnly(p);
        }
        return Py.Strip(File.ReadAllText(p));
    }

    public static bool KeyMatches(string home, string? given) =>
        !string.IsNullOrEmpty(given) && File.Exists(Path.Combine(home, "canvas_key"))
        && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(given), System.Text.Encoding.UTF8.GetBytes(ExtensionKey(home)));
}

/// <summary>What the course scout found last time: whether it finished, its summary, and how many files it saved.</summary>
public sealed record ScoutReport(bool Ok, string Report, string When, int Files);

/// <summary>A Canvas course as Canvas names it, for matching it to a class and showing it in Settings.</summary>
public sealed record CourseInfo(string Code, string Name, string Term);

/// <summary>Chrome's extension went from one version to a newer one at <paramref name="At"/> (ISO); dismissed once the
/// student has seen it.</summary>
public sealed record ExtensionUpdate(string From, string To, string At, bool Dismissed);
