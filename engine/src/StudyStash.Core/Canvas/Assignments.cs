using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Canvas;

/// <summary>One Canvas assignment and where you stand on it.</summary>
/// <param name="Due">Local wall-clock time, "2026-09-30T23:59", or "" when there's no due date.</param>
/// <param name="Status">graded | submitted | late | excused | missing | past due | open | no submission</param>
/// <param name="Late">Handed in late (graded work keeps it: "Submitted late · 17/20").</param>
/// <param name="Grade">The grade as Canvas shows it ("18", "A-", "complete"); "" when there isn't one.</param>
/// <param name="GradedAt">When it was graded, Canvas's UTC ISO; "" when it hasn't been.</param>
/// <param name="Kind">assignment | quiz | discussion</param>
/// <param name="DueAt">The due date as Canvas gave it (UTC ISO), or "".</param>
/// <param name="Comments">How many comments others (the grader) left; null in a list saved before it was counted.</param>
/// <param name="MarkedDone">Marked done in Canvas's planner.</param>
public sealed record Assignment(string ClassName, long Id, string Name, string Due, double? Points, string Status,
    double? Score, string Submitted, string Url, bool Late = false, bool Missing = false, bool Excused = false, string Grade = "",
    string GradedAt = "", string GradingType = "", string Kind = "assignment", string DueAt = "", int? Comments = null, bool MarkedDone = false)
{
    /// <summary>Handed in or doesn't need anything more.</summary>
    public bool Done => Status is "graded" or "submitted" or "excused" or "late";
}

/// <summary>Something that changed on Canvas between two syncs, in the design's words ("Graded: CS 101 · Problem set 4
/// · 18/20").</summary>
/// <param name="Kind">new | moved | graded | feedback | missing | removed</param>
public sealed record CanvasChange(string Kind, string Class, string Name, string Text, long? AssignmentId = null, long? AnnouncementId = null);

/// <summary>Canvas assignments: reading Canvas's JSON, what changed between two syncs, and the saved list.</summary>
public static class Assignments
{
    static readonly HashSet<string> NoSubmit = ["none", "on_paper", "not_graded"];

    static string S(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.String ? j.GetValue<string>() : "";
    static bool B(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.True;
    // Numbers parsed from JSON and numbers set here (ints) read the same way.
    static double? D(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.Number
        ? double.Parse(j.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>"2026-09-30T23:59" in this computer's time zone, from Canvas's UTC time.</summary>
    public static string Local(string? iso, TimeZoneInfo? zone = null) =>
        string.IsNullOrEmpty(iso) || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? "" : TimeZoneInfo.ConvertTime(t, zone ?? TimeZoneInfo.Local).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    public static string StatusOf(JsonObject a, DateTimeOffset now)
    {
        var s = a["submission"] as JsonObject ?? [];
        return StatusOf(SubmissionInfo.Summary(s), (a["submission_types"] as JsonArray ?? []).Select(S), S(a["due_at"]), now);
    }

    public static string StatusOf(AssignmentInfo a, DateTimeOffset now) => StatusOf(a.Submission, a.SubmissionTypes, a.DueAt, now);

    static string StatusOf(SubmissionInfo? s, IEnumerable<string> submissionTypes, string? dueAt, DateTimeOffset now)
    {
        s ??= new SubmissionInfo();
        if (s.Excused) return "excused";
        // Pass/fail and letter grades can come with a grade and no score.
        if (s.State == "graded" && (s.Score is not null || !string.IsNullOrEmpty(s.Grade))) return "graded";
        if (s.Missing) return "missing";
        if (!string.IsNullOrEmpty(s.SubmittedAt) || s.State is "submitted" or "pending_review") return s.Late ? "late" : "submitted";
        if (submissionTypes.All(NoSubmit.Contains)) return "no submission";
        return DateTimeOffset.TryParse(dueAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var due) && due < now
            ? "past due" : "open";
    }

    /// <summary>A status as the design says it: "To do", "Missing", "Submitted late" (late work keeps that once graded)…</summary>
    public static string Label(string status, bool late = false) => status switch
    {
        "open" => "To do",
        "missing" => "Missing",
        "past due" => "Past due",
        "late" => "Submitted late",
        "graded" => late ? "Submitted late" : "Graded",
        "submitted" => late ? "Submitted late" : "Submitted",
        "excused" => "Excused",
        "no submission" => "Nothing to hand in",
        _ => status,
    };

    /// <summary>The mark as the design shows it: "18/20", or the grade for letter, GPA, percentage and pass/fail
    /// grading ("A-", "Complete"); "" when there's no mark yet.</summary>
    public static string ScoreText(double? score, string? grade, string? gradingType, double? points)
    {
        string g = grade ?? "";
        switch (gradingType)
        {
            case "pass_fail":
                return g.Length > 0 ? char.ToUpperInvariant(g[0]) + g[1..] : "";
            case "letter_grade" or "gpa_scale" or "percent":
                return g.Length > 0 ? g : score is double s0 ? $"{Num(s0)}/{Num(points)}" : "";
            case "not_graded":
                return "";
            default:
                return score is double s ? $"{Num(s)}/{Num(points)}" : "";
        }
    }

    public static string ScoreText(Assignment a) => ScoreText(a.Score, a.Grade, a.GradingType, a.Points);

    public static Assignment From(string className, JsonObject a, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var s = a["submission"] as JsonObject ?? [];
        return new Assignment(className, (long)(D(a["id"]) ?? 0), Py.Strip(S(a["name"])), Local(S(a["due_at"]), zone), D(a["points_possible"]),
            StatusOf(a, now), D(s["score"]), Local(S(s["submitted_at"]), zone), S(a["html_url"]), B(s["late"]), B(s["missing"]), B(s["excused"]),
            S(s["grade"]), S(s["graded_at"]), S(a["grading_type"]), "assignment", S(a["due_at"]));
    }

    /// <summary>An assignment's row in the list, from the class's index.</summary>
    public static Assignment From(string className, AssignmentInfo a, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var s = a.Submission;
        return new Assignment(className, a.Id, a.Name, Local(a.DueAt, zone), a.Points, StatusOf(a, now), s?.Score, Local(s?.SubmittedAt, zone),
            a.HtmlUrl, s?.Late ?? false, s?.Missing ?? false, s?.Excused ?? false, s?.Grade ?? "", s?.GradedAt ?? "", a.GradingType, a.Kind,
            a.DueAt ?? "", s?.Comments.Count(c => !c.Mine) ?? 0);
    }

    public static bool Published(JsonObject a) => a["published"] is not JsonValue p || p.GetValueKind() != JsonValueKind.False;

    static string Num(double? d) => (d ?? 0).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>A due date in few words: "Tue 11:59 PM" within the coming week, else "Mon 22 Sep, 11:59 PM".</summary>
    public static string Short(string due, DateTime now)
    {
        if (due.Length == 0) return "no due date";
        var d = DateTime.Parse(due, CultureInfo.InvariantCulture);
        return (d.Date - now.Date).Days is >= 0 and <= 6
            ? d.ToString("ddd h:mm tt", CultureInfo.InvariantCulture)
            : d.ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture);
    }

    /// <summary>What changed between two syncs, in words: new assignments, moved due dates, new marks, new comments
    /// from the grader, work gone missing, and assignments Canvas no longer lists. <paramref name="now"/> is the
    /// wall clock the due dates are in.</summary>
    public static List<CanvasChange> Diff(IReadOnlyList<Assignment> before, IReadOnlyList<Assignment> after, DateTime now)
    {
        var old = before.GroupBy(a => (a.ClassName, a.Id)).ToDictionary(g => g.Key, g => g.First());
        var said = new List<CanvasChange>();
        void Say(string kind, Assignment a, string word, string what) =>
            said.Add(new CanvasChange(kind, a.ClassName, a.Name, $"{word}: {a.ClassName} · {a.Name}{(what.Length > 0 ? " · " + what : "")}", a.Id));
        foreach (var a in after)
        {
            if (!old.TryGetValue((a.ClassName, a.Id), out var o))
            {
                Say("new", a, "New", a.Due.Length > 0 ? "due " + Short(a.Due, now) : "no due date");
                continue;
            }
            if (o.Due != a.Due) Say("moved", a, "Moved", "now " + Short(a.Due, now));
            // A list saved before grades were kept has no grade to compare: only a new score is news then.
            if (a.Status == "graded" && (o.Status != "graded" || o.Score != a.Score || o.Grade.Length > 0 && o.Grade != a.Grade))
                Say("graded", a, "Graded", ScoreText(a));
            if (a.Comments is int n && n > (o.Comments ?? n))
                Say("feedback", a, "Feedback", n - (o.Comments ?? 0) == 1 ? "1 new comment" : $"{n - (o.Comments ?? 0)} new comments");
            if (a.Status == "missing" && o.Status != "missing") Say("missing", a, "Missing", a.Due.Length > 0 ? "was due " + Short(a.Due, now) : "");
        }
        var listed = after.Select(a => (a.ClassName, a.Id)).ToHashSet();
        foreach (var o in before.Where(o => !listed.Contains((o.ClassName, o.Id)))) Say("removed", o, "Removed", "");
        return said;
    }

    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static string PathIn(string home) => Path.Combine(home, "canvas_assignments.json");

    public static List<Assignment> Load(string home)
    {
        try
        {
            return File.Exists(PathIn(home)) ? JsonSerializer.Deserialize<List<Assignment>>(File.ReadAllText(PathIn(home)), Options) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Save(string home, IEnumerable<Assignment> items)
    {
        string tmp = PathIn(home) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(items.ToList(), Options));
        File.Move(tmp, PathIn(home), overwrite: true);
    }

    /// <summary>What's still to do, soonest first: open, past due or missing, due within <paramref name="days"/> (or
    /// overdue), plus anything with no due date that's open.</summary>
    public static List<Assignment> Upcoming(IEnumerable<Assignment> all, DateTime now, int days = 14, string? className = null) =>
        all.Where(a => (className is null || a.ClassName == className) && !a.Done && a.Status != "no submission")
            // By calendar day: "within a week" includes the whole of the seventh day.
            .Where(a => a.Due.Length == 0 || DateTime.Parse(a.Due, CultureInfo.InvariantCulture) < now.Date.AddDays(days + 1))
            .Where(a => a.Due.Length == 0 || a.Status != "past due" || DateTime.Parse(a.Due, CultureInfo.InvariantCulture) >= now.AddDays(-21))
            .OrderBy(a => a.Due.Length == 0).ThenBy(a => a.Due, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>"Tue 30 Sep, 11:59 PM", or "Today, 11:59 PM" / "Tomorrow, …".</summary>
    public static string Say(string due, DateTime now)
    {
        if (due.Length == 0) return "No due date";
        var d = DateTime.Parse(due, CultureInfo.InvariantCulture);
        string time = d.ToString("h:mm tt", CultureInfo.InvariantCulture);
        return (d.Date - now.Date).Days switch
        {
            0 => $"Today, {time}",
            1 => $"Tomorrow, {time}",
            -1 => $"Yesterday, {time}",
            _ => d.ToString("ddd d MMM", CultureInfo.InvariantCulture) + ", " + time,
        };
    }
}
