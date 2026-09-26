using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyStash.Core.Canvas;

/// <summary>One thing worth telling the student about: a new assignment, a moved due date, a new score or comment,
/// work gone missing, a new announcement, or something due soon.</summary>
public sealed class CanvasNotification
{
    public long Id { get; set; }
    /// <summary>new_assignment | due_moved | new_score | new_feedback | new_announcement | missing | due_soon</summary>
    public string Kind { get; set; } = "";
    /// <summary>"New assignment", "Due date moved", … (the design's words, one per kind).</summary>
    public string Title { get; set; } = "";
    /// <summary>"CS 101 · Lab 3: recursion traces · due Tue 11:59 PM".</summary>
    public string Text { get; set; } = "";
    public string Class { get; set; } = "";
    public long? AssignmentId { get; set; }
    public long? AnnouncementId { get; set; }
    public string At { get; set; } = "";
    public bool Seen { get; set; }
}

sealed class NotificationsFile
{
    public long NextId { get; set; } = 1;
    public List<CanvasNotification> Items { get; set; } = [];
}

/// <summary>
/// Canvas changes, said as notifications for the app: kept in <c>home/canvas_notifications.json</c> (ids always
/// increasing, so a poller asks for what's after the last one it saw), capped at 200. A sync's changes are appended
/// when it finishes; "due soon" is added the first time the Due list is asked about work due within a day, once per
/// assignment, never repeated.
/// </summary>
public static class CanvasNotifications
{
    const int Cap = 200;

    static readonly Dictionary<string, (string Kind, string Title, string Word)> Map = new()
    {
        ["new"] = ("new_assignment", "New assignment", "New"),
        ["moved"] = ("due_moved", "Due date moved", "Moved"),
        ["graded"] = ("new_score", "New score", "Graded"),
        ["feedback"] = ("new_feedback", "New feedback", "Feedback"),
        ["missing"] = ("missing", "Missing", "Missing"),
        ["announcement"] = ("new_announcement", "New announcement", "Announcement"),
    };

    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    static readonly Lock Gate = new();

    static string PathIn(string home) => Path.Combine(home, "canvas_notifications.json");

    static NotificationsFile Load(string home)
    {
        try
        {
            return File.Exists(PathIn(home)) ? JsonSerializer.Deserialize<NotificationsFile>(File.ReadAllText(PathIn(home)), Options) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    static void Save(string home, NotificationsFile f)
    {
        string tmp = PathIn(home) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(f, Options));
        File.Move(tmp, PathIn(home), overwrite: true);
    }

    static void Add(NotificationsFile f, string kind, string title, string text, string cls, long? assignmentId, long? announcementId, string at)
    {
        f.Items.Add(new CanvasNotification
        {
            Id = f.NextId++, Kind = kind, Title = title, Text = text, Class = cls,
            AssignmentId = assignmentId, AnnouncementId = announcementId, At = at,
        });
        if (f.Items.Count > Cap) f.Items.RemoveRange(0, f.Items.Count - Cap);
    }

    /// <summary>What a sync found becomes notifications ("removed" isn't news worth one).</summary>
    public static void AppendChanges(string home, IReadOnlyList<CanvasChange> changes, string at)
    {
        if (changes.Count == 0) return;
        lock (Gate)
        {
            var f = Load(home);
            foreach (var c in changes)
            {
                if (!Map.TryGetValue(c.Kind, out var m)) continue;
                string text = c.Text.StartsWith(m.Word + ": ", StringComparison.Ordinal) ? c.Text[(m.Word.Length + 2)..] : c.Text;
                Add(f, m.Kind, m.Title, text, c.Class, c.AssignmentId, c.AnnouncementId, at);
            }
            Save(home, f);
        }
    }

    /// <summary>Open work due within a day gets one "Due soon" notification the first time it's noticed, never twice
    /// for the same assignment.</summary>
    public static void EnsureDueSoon(string home, IReadOnlyList<Assignment> due, DateTimeOffset now, TimeZoneInfo zone)
    {
        lock (Gate)
        {
            var f = Load(home);
            var already = f.Items.Where(i => i.Kind == "due_soon").Select(i => i.AssignmentId).ToHashSet();
            var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
            bool changed = false;
            foreach (var a in due.Where(a => !a.Done && a.Due.Length > 0 && !already.Contains(a.Id)))
            {
                var d = DateTime.Parse(a.Due, CultureInfo.InvariantCulture);
                if (d < local || d > local.AddHours(24)) continue;
                Add(f, "due_soon", "Due soon", $"{a.ClassName} · {a.Name} · due {Assignments.Short(a.Due, local)}", a.ClassName, a.Id, null,
                    now.ToString("o", CultureInfo.InvariantCulture));
                changed = true;
            }
            if (changed) Save(home, f);
        }
    }

    /// <summary>Every notification up to and including this id is seen.</summary>
    public static void MarkSeen(string home, long upTo)
    {
        lock (Gate)
        {
            var f = Load(home);
            foreach (var i in f.Items) if (i.Id <= upTo) i.Seen = true;
            Save(home, f);
        }
    }

    /// <summary>The highest id known (0 when there are none), and every notification after <paramref name="after"/>.</summary>
    public static (long Last, List<CanvasNotification> Items) Since(string home, long after)
    {
        var f = Load(home);
        long last = f.Items.Count > 0 ? f.Items[^1].Id : 0;
        return (last, f.Items.Where(i => i.Id > after).ToList());
    }
}
