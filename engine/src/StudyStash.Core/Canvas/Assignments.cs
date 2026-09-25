using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Canvas;

/// <summary>One Canvas assignment and where you stand on it.</summary>
/// <param name="Due">Local wall-clock time, "2026-09-30T23:59", or "" when there's no due date.</param>
/// <param name="Status">graded | submitted | late | excused | missing | past due | open | no submission</param>
public sealed record Assignment(string ClassName, long Id, string Name, string Due, double? Points, string Status,
    double? Score, string Submitted, string Url)
{
    /// <summary>Handed in or doesn't need anything more.</summary>
    public bool Done => Status is "graded" or "submitted" or "excused" or "late";
}

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
        if (B(s["excused"])) return "excused";
        if (S(s["workflow_state"]) == "graded" && D(s["score"]) is not null) return "graded";
        if (B(s["missing"])) return "missing";
        if (S(s["submitted_at"]).Length > 0 || S(s["workflow_state"]) is "submitted" or "pending_review")
            return B(s["late"]) ? "late" : "submitted";
        var types = (a["submission_types"] as JsonArray ?? []).Select(S).ToList();
        if (types.All(NoSubmit.Contains)) return "no submission";
        return DateTimeOffset.TryParse(S(a["due_at"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var due) && due < now
            ? "past due" : "open";
    }

    public static Assignment From(string className, JsonObject a, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var s = a["submission"] as JsonObject ?? [];
        return new Assignment(className, (long)(D(a["id"]) ?? 0), Py.Strip(S(a["name"])), Local(S(a["due_at"]), zone), D(a["points_possible"]),
            StatusOf(a, now), D(s["score"]), Local(S(s["submitted_at"]), zone), S(a["html_url"]));
    }

    public static bool Published(JsonObject a) => a["published"] is not JsonValue p || p.GetValueKind() != JsonValueKind.False;

    static string When(string due) => due.Length > 0 ? due.Replace('T', ' ') : "no due date";
    static string Num(double? d) => (d ?? 0).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>What changed, in words: new assignments, moved due dates, and new statuses or scores.</summary>
    public static List<string> Diff(IReadOnlyList<Assignment> before, IReadOnlyList<Assignment> after)
    {
        var old = before.GroupBy(a => (a.ClassName, a.Id)).ToDictionary(g => g.Key, g => g.First());
        var said = new List<string>();
        foreach (var a in after)
        {
            if (!old.TryGetValue((a.ClassName, a.Id), out var o))
            {
                said.Add($"New: {a.ClassName} · {a.Name} · due {When(a.Due)} · {Num(a.Points)} pts");
                continue;
            }
            if (o.Due != a.Due) said.Add($"Due date moved: {a.ClassName} · {a.Name} · {When(o.Due)} to {When(a.Due)}");
            if (o.Status != a.Status || o.Score != a.Score)
                said.Add($"Now {a.Status}: {a.ClassName} · {a.Name}" + (a.Score is double sc ? $" ({Num(sc)}/{Num(a.Points)})" : ""));
        }
        var now = after.Select(a => (a.ClassName, a.Id)).ToHashSet();
        said.AddRange(before.Where(o => !now.Contains((o.ClassName, o.Id))).Select(o => $"Removed: {o.ClassName} · {o.Name}"));
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
            .Where(a => a.Due.Length == 0 || DateTime.Parse(a.Due, CultureInfo.InvariantCulture) <= now.AddDays(days))
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
