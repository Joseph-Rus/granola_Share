using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>What Granola's "Copy transcript" button gives: the note's title, its day, and the transcript itself.</summary>
public sealed record Copied(string Title, DateOnly? Date, string Body);

/// <summary>
/// Transcripts copied out of the Granola app, for accounts whose plan doesn't share them (transcript_grab.py's store).
/// Copying them is the Python engine's job until the Study Stash app takes it over; the laptop's watcher here reads
/// what was copied, from the same folder, and sends it along with its lecture.
/// </summary>
public static partial class Transcripts
{
    [GeneratedRegex(@"^(Meeting Title|Date|Meeting participants|Transcript):\s*(.*)$")]
    private static partial Regex Header();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotWord();

    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1f]")]
    private static partial Regex NotInFileNames();

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})")]
    private static partial Regex IsoDayPrefix();

    /// <summary>Split what "Copy transcript" puts on the clipboard into title, date, and transcript.</summary>
    public static Copied? ParseCopied(string? text, DateOnly? today = null)
    {
        string title = "";
        DateOnly? when = null;
        int bodyStart = 0;
        var lines = Py.SplitLines(text ?? "");
        for (int i = 0; i < Math.Min(8, lines.Count); i++)
        {
            var m = Header().Match(Py.Strip(lines[i]));
            if (!m.Success) continue;
            string key = m.Groups[1].Value, value = m.Groups[2].Value;
            if (key == "Meeting Title") title = Py.Strip(value);
            else if (key == "Date") when = ParseDay(value, today ?? DateOnly.FromDateTime(DateTime.Now));
            else if (key == "Transcript")
            {
                bodyStart = i + 1;
                break;
            }
        }
        string body = Py.Strip(string.Join("\n", lines.Skip(bodyStart)));
        return body.Length == 0 ? null : new Copied(title, when, body);
    }

    static readonly string[] WithYear = ["MMM d, yyyy", "MMMM d, yyyy", "MMM d yyyy"];
    static readonly string[] WithoutYear = ["MMM d", "MMMM d"];

    /// <summary>"Sep 24" (Granola leaves out the year, so it's the last Sep 24) or "Sep 24, 2026".</summary>
    public static DateOnly? ParseDay(string value, DateOnly today)
    {
        value = Py.Strip(value);
        const DateTimeStyles loose = DateTimeStyles.AllowWhiteSpaces;
        foreach (string format in WithYear)
            if (DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, loose, out var d)) return d;
        foreach (string format in WithoutYear)
            if (DateOnly.TryParseExact($"{value} {today.Year}", format + " yyyy", CultureInfo.InvariantCulture, loose, out var d))
                return d > today ? d.AddYears(-1) : d;
        return null;
    }

    /// <summary>A title reduced to its letters and digits, for matching a copy to its lecture.</summary>
    public static string NormTitle(string? s) => Py.Strip(NotWord().Replace(Py.Lower(s ?? ""), " "));

    /// <summary>"2026-09-24T15:00:00" → that local time; null for a bare date (Python's fromisoformat on [:19]).</summary>
    public static DateTime? IsoTime(string? s)
    {
        if (s is null || s.Length < 16) return null;
        string head = s[..Math.Min(19, s.Length)];
        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm"];
        return DateTime.TryParseExact(head, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    }

    public static DateOnly? IsoDay(string? s)
    {
        var m = IsoDayPrefix().Match(s ?? "");
        if (!m.Success) return null;
        try
        {
            return new DateOnly(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A title as a file name: no characters Windows or macOS refuse there.</summary>
    public static string SafeName(string s) => NotInFileNames().Replace(s, " ");
}

/// <summary>Copied transcripts on disk, one file per note: &lt;home&gt;/transcripts/&lt;date&gt; &lt;title&gt;.txt</summary>
public sealed class TranscriptStore(string home)
{
    public string Dir { get; } = Path.Combine(home, "transcripts");

    /// <summary>
    /// Keep the newest copy, unless it is much shorter (a partial panel) than one we have. `recordedTo` is when the
    /// recording stopped, for copies made right then: Granola may not have named the note yet, so those are matched
    /// to their note by time instead of title. The path, and whether it changed.
    /// </summary>
    public (string Path, bool Changed) Save(Copied copied, string raw, DateTime? recordedTo = null)
    {
        Directory.CreateDirectory(Dir);
        string slug = Py.Head(Py.Strip(Transcripts.SafeName(copied.Title.Length > 0 ? copied.Title : "untitled")), 80);
        string stamp = recordedTo is DateTime t ? " " + t.ToString("HHmm", CultureInfo.InvariantCulture) : "";
        string path = Path.Combine(Dir, $"{(copied.Date is DateOnly d ? Transcripts.Day(d) : "undated")}{stamp} {slug}.txt");
        if (File.Exists(path) && Transcripts.ParseCopied(Py.ReadText(path)) is Copied old
            && (old.Body == copied.Body || copied.Body.Length < 0.8 * old.Body.Length))
            return (path, false);
        Py.WriteText(path, raw);
        if (recordedTo is DateTime stopped)
            Py.WriteText(Path.ChangeExtension(path, ".json"),
                PyJson.Dumps(new JsonObject { ["recorded_to"] = stopped.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) }));
        return (path, true);
    }

    /// <summary>
    /// The transcript for a note: matched on title (and date), else by time for copies made as the recording stopped.
    /// `day` can be the note's full start time ("2026-09-24T15:00:00").
    /// </summary>
    public string Find(string title, string? day = null)
    {
        string want = Transcripts.NormTitle(title);
        if (!Directory.Exists(Dir)) return "";
        var when = Transcripts.IsoDay(day);
        var started = Transcripts.IsoTime(day);
        string best = "";
        (DateTime Stopped, string Body)? byTime = null;
        foreach (string path in Directory.EnumerateFiles(Dir, "*.txt"))
        {
            if (Transcripts.ParseCopied(Py.ReadText(path)) is not Copied c) continue;
            if (want.Length > 0 && Transcripts.NormTitle(c.Title) == want)
            {
                if (when is DateOnly w && c.Date is DateOnly cd && Math.Abs(cd.DayNumber - w.DayNumber) > 1) continue;
                if (c.Body.Length > best.Length) best = c.Body;
            }
            else if (started is DateTime s && RecordedTo(path) is DateTime stopped)
            {
                // The first recording that ended after this note started (and within a long lecture's length).
                if (s <= stopped && stopped <= s.AddHours(5) && (byTime is null || stopped < byTime.Value.Stopped))
                    byTime = (stopped, c.Body);
            }
        }
        return best.Length > 0 ? best : byTime?.Body ?? "";
    }

    static DateTime? RecordedTo(string path)
    {
        try
        {
            return Py.JsonLoads(Py.ReadText(Path.ChangeExtension(path, ".json")))?["recorded_to"] is JsonValue v && Py.AsString(v) is string s
                ? Transcripts.IsoTime(s) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
