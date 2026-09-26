using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StudyStash.Core.Canvas;

/// <summary>
/// The Markdown the sync writes from a class's index when it finishes: each assignment's spec.md (what to do) and
/// feedback.md (where you stand). Pure functions of the index, the time zone and the time, so the same Canvas always
/// writes the same bytes and links point at where files really landed.
/// </summary>
public static class CanvasMarkdown
{
    /// <summary>"Tue 30 Sep 2025, 11:59 PM" in <paramref name="zone"/>; "" when there's no time.</summary>
    public static string When(string? iso, TimeZoneInfo zone) =>
        !string.IsNullOrEmpty(iso) && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? TimeZoneInfo.ConvertTime(t, zone).ToString("ddd d MMM yyyy, h:mm tt", CultureInfo.InvariantCulture) : "";

    /// <summary>A file size the way the design writes it: "4 KB", "1.2 MB".</summary>
    public static string Size(long? bytes) => bytes switch
    {
        null => "",
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{Math.Max(1, Math.Round(bytes.Value / 1024.0)).ToString("0", CultureInfo.InvariantCulture)} KB",
        < 1024L * 1024 * 1024 => $"{(bytes.Value / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture)} MB",
        _ => $"{(bytes.Value / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture)} GB",
    };

    static string Num(double? d) => (d ?? 0).ToString("0.##", CultureInfo.InvariantCulture);

    // Inside a table cell: one line, and no bar to end the cell early.
    static string Cell(string s) => s.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal).Trim();

    /// <summary>How an assignment is marked, when it isn't points: "Complete/incomplete", "Letter grade".</summary>
    static string? Grading(string type) => type switch
    {
        "pass_fail" => "Complete/incomplete",
        "letter_grade" => "Letter grade",
        "gpa_scale" => "GPA scale",
        "percent" => "Percentage",
        "not_graded" => "Not graded",
        _ => null,
    };

    /// <summary>spec.md: what the assignment asks, by when, how it's marked.</summary>
    public static string Spec(string cls, AssignmentInfo a, TimeZoneInfo zone)
    {
        var sb = new StringBuilder();
        sb.Append("---\n")
            .Append("title: ").Append(JsonSerializer.Serialize(a.Name)).Append('\n')
            .Append("class: ").Append(cls).Append('\n')
            .Append("canvas_id: ").Append(a.Id.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("due: ").Append(When(a.DueAt, zone) is { Length: > 0 } due ? due : "none").Append('\n')
            .Append("points: ").Append(Num(a.Points)).Append('\n')
            .Append("submission_types: ").Append(string.Join(", ", a.SubmissionTypes)).Append('\n')
            .Append("url: ").Append(a.HtmlUrl).Append('\n')
            .Append(Crawl.Generated).Append(" (from Canvas; rewritten when Canvas changes)\n---\n\n")
            .Append("# ").Append(a.Name).Append("\n\n");
        var facts = new List<string> { When(a.DueAt, zone) is { Length: > 0 } d ? $"**Due** {d}" : "**No due date**" };
        if (a.GradingType != "not_graded") facts.Add($"**{Num(a.Points)} points**");
        if (Grading(a.GradingType) is { } how) facts.Add(how);
        if (a.AllowedAttempts is int n and > 0) facts.Add(n == 1 ? "1 attempt" : $"{n} attempts");
        sb.Append(string.Join(" · ", facts)).Append("\n\n");
        var window = new List<string>();
        if (When(a.UnlockAt, zone) is { Length: > 0 } from) window.Add($"Available from {from}");
        if (When(a.LockAt, zone) is { Length: > 0 } until) window.Add($"Closes {until}");
        if (window.Count > 0) sb.Append(string.Join(" · ", window)).Append("\n\n");
        sb.Append(a.Instructions.Length > 0 ? a.Instructions : "_No instructions on Canvas._").Append('\n');
        if (a.Rubric.Count > 0)
        {
            sb.Append("\n## Rubric\n\n| Criterion | Points | Levels |\n|---|---|---|\n");
            foreach (var r in a.Rubric)
            {
                string levels = string.Join("; ", r.Ratings.Select(x => $"{x.Description} ({Num(x.Points)})"));
                string desc = r.Description + (r.LongDescription.Length > 0 ? $" ({r.LongDescription})" : "");
                sb.Append("| ").Append(Cell(desc)).Append(" | ").Append(Num(r.Points)).Append(" | ").Append(Cell(levels)).Append(" |\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>There's something to say about the student's work: handed in, marked, or commented on.</summary>
    public static bool HasFeedback(AssignmentInfo a) => a.Submission is { } s
        && (!string.IsNullOrEmpty(s.SubmittedAt) || s.Score is not null || !string.IsNullOrEmpty(s.Grade) || s.Comments.Count > 0 || s.Marks.Count > 0);

    /// <summary>feedback.md: the mark, status, files handed in, rubric marks, comments and earlier attempts.</summary>
    public static string Feedback(string cls, AssignmentInfo a, TimeZoneInfo zone, DateTimeOffset now)
    {
        var s = a.Submission ?? new SubmissionInfo();
        var sb = new StringBuilder();
        sb.Append("---\ncanvas_id: ").Append(a.Id.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("class: ").Append(cls).Append('\n')
            .Append("url: ").Append(a.HtmlUrl).Append('\n')
            .Append(Crawl.Generated).Append(" (from Canvas)\n---\n\n")
            .Append("# ").Append(a.Name).Append(": my submission\n\n");
        string score = Assignments.ScoreText(s.Score, s.Grade, a.GradingType, a.Points);
        // A letter or pass/fail grade says the points too, when Canvas gave them.
        if (score.Length > 0 && a.GradingType is not ("points" or "") && s.Score is double pts && a.Points is > 0) score += $" ({Num(pts)}/{Num(a.Points)})";
        sb.Append("- **Score:** ").Append(score.Length > 0 ? score : s.Excused ? "excused" : "not graded yet").Append('\n');
        string status = Assignments.StatusOf(a, now);
        sb.Append("- **Status:** ").Append(Assignments.Label(status, s.Late)).Append('\n');
        if (When(s.SubmittedAt, zone) is { Length: > 0 } submitted) sb.Append("- **Submitted:** ").Append(submitted).Append('\n');
        if (When(s.GradedAt, zone) is { Length: > 0 } graded && status == "graded") sb.Append("- **Graded:** ").Append(graded).Append('\n');
        if (s.Attempt is int attempt)
            sb.Append("- **Attempt:** ").Append(attempt).Append(a.AllowedAttempts is int most and > 0 ? $" of {most}" : "").Append('\n');
        if (s.PointsDeducted is double off && off > 0) sb.Append("- **Late penalty:** −").Append(Num(off)).Append(off == 1 ? " point\n" : " points\n");
        if (s.Files.Count > 0) sb.Append("- **Files:** ").Append(Files(s.Files, a.Folder)).Append('\n');
        if (s.Body.Length > 0) sb.Append("\n## What I submitted\n\n").Append(s.Body).Append('\n');
        if (s.Marks.Count > 0)
        {
            sb.Append("\n## Rubric marks\n\n| Criterion | Mark | Rating | Comment |\n|---|---|---|---|\n");
            var criteria = a.Rubric.ToDictionary(r => r.Id);
            // In the rubric's order; a mark for a criterion the rubric no longer has comes last, by its id.
            var order = a.Rubric.Select(r => r.Id).Where(s.Marks.ContainsKey).Concat(s.Marks.Keys.Where(k => !criteria.ContainsKey(k)).Order(StringComparer.Ordinal));
            foreach (string id in order)
            {
                var mark = s.Marks[id];
                var c = criteria.GetValueOrDefault(id);
                string points = mark.Points is double p ? Num(p) + (c?.Points is double of ? $" / {Num(of)}" : "") : "–";
                string rating = c?.Ratings.FirstOrDefault(r => r.Id == mark.RatingId)?.Description ?? "";
                sb.Append("| ").Append(Cell(c?.Description ?? id)).Append(" | ").Append(points).Append(" | ").Append(Cell(rating)).Append(" | ")
                    .Append(Cell(mark.Comment)).Append(" |\n");
            }
        }
        if (s.Comments.Count > 0)
        {
            sb.Append("\n## Comments\n\n");
            foreach (var c in s.Comments)
            {
                sb.Append("- **").Append(c.Author.Length > 0 ? c.Author : "Someone").Append("**");
                if (When(c.At, zone) is { Length: > 0 } at) sb.Append(" (").Append(at).Append(')');
                sb.Append(": ").Append(Py.Strip(c.Text).ReplaceLineEndings(" ")).Append('\n');
                if (c.Files.Count > 0) sb.Append("  - Attached: ").Append(Files(c.Files, a.Folder)).Append('\n');
                if (c.MediaUrl is { Length: > 0 } media) sb.Append("  - [Audio or video comment, on Canvas](").Append(media).Append(")\n");
            }
        }
        if (s.Attempts.Count > 1)
        {
            sb.Append("\n## Attempts\n\n");
            foreach (var t in s.Attempts.OrderByDescending(t => t.Attempt))
            {
                sb.Append("- Attempt ").Append(t.Attempt);
                if (t.Attempt == s.Attempt) sb.Append(" (latest)");
                if (When(t.SubmittedAt, zone) is { Length: > 0 } at) sb.Append(", submitted ").Append(at);
                if (t.Late) sb.Append(", late");
                if (t.Files.Count > 0) sb.Append(": ").Append(Files(t.Files, a.Folder));
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>"[ps4.py](submission/ps4.py) (4 KB)", or the Canvas link and why it wasn't saved.</summary>
    static string Files(IEnumerable<FileRef> files, string folder) => string.Join(", ", files.Select(f =>
    {
        string size = Size(f.Size);
        if (f.Local is { Length: > 0 } local) return $"[{f.Name}]({RelativeLink(folder, local)})" + (size.Length > 0 ? $" ({size})" : "");
        string why = f.Skipped is { Length: > 0 } skipped ? $"not saved: {skipped}" : "not saved";
        return $"[{f.Name}]({f.Url}) ({(size.Length > 0 ? size + ", " : "")}{why}, on Canvas)";
    }));

    /// <summary>A link from a file in <paramref name="fromFolder"/> to <paramref name="path"/> (both inside the class's
    /// folder, with /), each part escaped for Markdown: "submission/attempt%201/lab2.pdf", "../Lab%202/spec.md".</summary>
    public static string RelativeLink(string fromFolder, string path)
    {
        string[] from = fromFolder.Split('/', StringSplitOptions.RemoveEmptyEntries), to = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int same = 0;
        while (same < from.Length && same < to.Length - 1 && from[same] == to[same]) same++;
        return string.Join('/', Enumerable.Repeat("..", from.Length - same).Concat(to.Skip(same).Select(Uri.EscapeDataString)));
    }
}
