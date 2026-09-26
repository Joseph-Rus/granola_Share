using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace StudyStash.Core.Canvas;

/// <summary>
/// Everything Study Stash knows about one class's Canvas course, as the last sync left it: the Canvas screens, the API
/// and Claude's tools read this instead of Canvas. One JSON file per class at <c>home/canvas/&lt;class&gt;.json</c>.
/// While a sync runs, the crawl fills a staged copy beside it (<c>&lt;class&gt;.sync.json</c>, saved as it goes, so a
/// restart carries on); when the sync finishes each listing is promoted on its own (<see cref="Promoted"/>): read
/// completely, the new copy wins; hidden from the student, it's emptied; failed or unfinished, what was known is kept.
/// Paths to saved files are inside the class's folder, with <c>/</c> ("Canvas/assignments/Lab 3/submission/lab3.pdf").
/// Only the student's own work is here: people are display names, never ids, avatars or emails.
/// </summary>
public sealed class CourseIndex
{
    public string Class { get; set; } = "";
    public long CourseId { get; set; }
    /// <summary>The course as Canvas names it ("COMP 101", "COMP 101 · Intro to Programming", "Fall 2025").</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Term { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    /// <summary>When the sync that wrote this finished (ISO).</summary>
    public string SyncedAt { get; set; } = "";
    /// <summary>How each listing went in that sync: ok | hidden | failed | reading (it never finished).</summary>
    public Dictionary<string, string> Sections { get; set; } = [];
    /// <summary>When each listing was last read completely (ok or hidden), so a failed one still says how old its data is.</summary>
    public Dictionary<string, string> ReadAt { get; set; } = [];
    public List<AssignmentInfo> Assignments { get; set; } = [];
    public List<ModuleInfo> Modules { get; set; } = [];
    public List<CourseFileInfo> Files { get; set; } = [];
    /// <summary>Canvas doesn't let this student see the course's Files area.</summary>
    public bool FilesHidden { get; set; }
    public List<PageInfo> Pages { get; set; } = [];
    public PageInfo? FrontPage { get; set; }
    /// <summary>Where the syllabus was saved (null when the course has none).</summary>
    public string? Syllabus { get; set; }
    public List<AnnouncementInfo> Announcements { get; set; } = [];
    public List<QuizInfo> Quizzes { get; set; } = [];
    public List<DiscussionInfo> Discussions { get; set; } = [];
    public List<TodoInfo> Todos { get; set; } = [];
    /// <summary>Only in the staged copy: what the submissions listing said, kept apart so each listing is promoted on its own.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SyncParts? Staged { get; set; }

    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static string Folder(string home) => Path.Combine(home, "canvas");
    public static string PathIn(string home, string cls) => Path.Combine(Folder(home), Crawl.SafeName(cls) + ".json");
    public static string StagedPathIn(string home, string cls) => Path.Combine(Folder(home), Crawl.SafeName(cls) + ".sync.json");

    /// <summary>A class's index as the last sync left it; null before its first sync (or when the file is unreadable).</summary>
    public static CourseIndex? Load(string home, string cls) => Read(PathIn(home, cls));

    /// <summary>The copy a running sync is filling, or null.</summary>
    public static CourseIndex? LoadStaged(string home, string cls) => Read(StagedPathIn(home, cls));

    static CourseIndex? Read(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<CourseIndex>(File.ReadAllText(path), Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string home) => Write(PathIn(home, Class));
    public void SaveStaged(string home) => Write(StagedPathIn(home, Class));

    /// <summary>Forget a sync's staged copies (a new sync starts afresh).</summary>
    public static void DropStaged(string home)
    {
        if (!Directory.Exists(Folder(home))) return;
        foreach (string f in Directory.EnumerateFiles(Folder(home), "*.sync.json")) File.Delete(f);
    }

    void Write(string path)
    {
        Directory.CreateDirectory(Py.Parent(path));
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }

    static CourseIndex Copy(CourseIndex index) => JsonSerializer.Deserialize<CourseIndex>(JsonSerializer.Serialize(index, Options), Options)!;

    /// <summary>
    /// A finished sync's index: each listing's state decides whose data it keeps. <c>ok</c>: the staged copy (anything
    /// Canvas no longer lists is gone); <c>hidden</c>: nothing; <c>failed</c>, still <c>reading</c>, or not read at all:
    /// the previous index's. Submissions are a listing of their own: read, they replace each assignment's; not read,
    /// the assignments listing's own summary (status, score, dates) is fresh and the rest (files, rubric marks,
    /// comments, attempts) stays as last read.
    /// </summary>
    public static CourseIndex Promoted(CourseIndex? prev, CourseIndex staged, IReadOnlyDictionary<string, string> sections, string at)
    {
        // Worked on as copies: the indexes given are left as they were.
        prev = prev is null ? null : Copy(prev);
        staged = Copy(staged);
        string State(string listing) => sections.GetValueOrDefault(listing, "");
        List<T> Pick<T>(string listing, List<T> fresh, List<T>? old) => State(listing) switch { "ok" => fresh, "hidden" => [], _ => old ?? [] };
        static string Either(string fresh, string? old) => fresh.Length > 0 ? fresh : old ?? "";
        var index = new CourseIndex
        {
            Class = Either(staged.Class, prev?.Class),
            CourseId = staged.CourseId != 0 ? staged.CourseId : prev?.CourseId ?? 0,
            Code = Either(staged.Code, prev?.Code),
            Name = Either(staged.Name, prev?.Name),
            Term = Either(staged.Term, prev?.Term),
            HtmlUrl = Either(staged.HtmlUrl, prev?.HtmlUrl),
            SyncedAt = at,
            Sections = new(sections),
            ReadAt = new(prev?.ReadAt ?? []),
            Assignments = Pick("assignments", staged.Assignments, prev?.Assignments),
            Modules = Pick("modules", staged.Modules, prev?.Modules),
            Files = Pick("files", staged.Files, prev?.Files),
            FilesHidden = State("files") switch { "hidden" => true, "ok" => false, _ => prev?.FilesHidden ?? false },
            Pages = Pick("pages", staged.Pages, prev?.Pages),
            FrontPage = staged.FrontPage ?? prev?.FrontPage,
            Syllabus = staged.Syllabus ?? prev?.Syllabus,
            Announcements = Pick("announcements", staged.Announcements, prev?.Announcements),
            Quizzes = Pick("quizzes", staged.Quizzes, prev?.Quizzes),
            Discussions = Pick("discussions", staged.Discussions, prev?.Discussions),
            Todos = Pick("planner", staged.Todos, prev?.Todos),
        };
        foreach (var (listing, state) in sections)
            if (state is "ok" or "hidden") index.ReadAt[listing] = at;
        Submissions(index, prev, staged.Staged ?? new SyncParts(), State("assignments"), State("submissions"));
        return index;
    }

    static void Submissions(CourseIndex index, CourseIndex? prev, SyncParts read, string assignments, string submissions)
    {
        var old = (prev?.Assignments ?? []).GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First().Submission);
        foreach (var a in index.Assignments)
            a.Submission = submissions switch
            {
                "ok" => read.Submissions.GetValueOrDefault(a.Id) ?? a.Submission,
                "hidden" => a.Submission?.WithDetailsOf(null),
                // The assignments listing was read: its summary is fresh, the details are as last read.
                _ when assignments == "ok" => a.Submission?.WithDetailsOf(old.GetValueOrDefault(a.Id)) ?? old.GetValueOrDefault(a.Id),
                _ => a.Submission,
            };
        if (submissions != "ok") return;
        // A submission names its assignment too: that fills what the assignments listing didn't have.
        var known = index.Assignments.ToDictionary(a => a.Id);
        foreach (var (id, inline) in read.Assignments.OrderBy(kv => kv.Key))
        {
            if (known.TryGetValue(id, out var a))
            {
                if (a.Rubric.Count == 0) a.Rubric = inline.Rubric;
            }
            else if (assignments != "hidden")
            {
                inline.Submission = read.Submissions.GetValueOrDefault(id);
                index.Assignments.Add(inline);
            }
        }
    }

    /// <summary>Promote a class's staged copy (see <see cref="Promoted"/>), save it, and drop the staged file.
    /// <paramref name="check"/> sees the index before it's saved.</summary>
    public static CourseIndex Promote(string home, string cls, IReadOnlyDictionary<string, string> sections, string at, Action<CourseIndex>? check = null)
    {
        var index = Promoted(Load(home, cls), LoadStaged(home, cls) ?? new CourseIndex { Class = cls }, sections, at);
        index.Class = cls;
        check?.Invoke(index);
        index.Save(home);
        File.Delete(StagedPathIn(home, cls));
        return index;
    }
}

/// <summary>What a running sync's submissions listing said, by assignment id: each submission, and the assignment it
/// names (to fill gaps in the assignments listing).</summary>
public sealed class SyncParts
{
    public Dictionary<long, SubmissionInfo> Submissions { get; set; } = [];
    public Dictionary<long, AssignmentInfo> Assignments { get; set; } = [];
}

/// <summary>One assignment: what to do, by when, how it's marked, and (in <see cref="Submission"/>) where you stand.</summary>
public sealed class AssignmentInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>assignment | quiz | discussion</summary>
    public string Kind { get; set; } = "assignment";
    /// <summary>Canvas's times, UTC ISO; null when not set.</summary>
    public string? DueAt { get; set; }
    public string? UnlockAt { get; set; }
    public string? LockAt { get; set; }
    public double? Points { get; set; }
    /// <summary>points | percent | letter_grade | gpa_scale | pass_fail | not_graded</summary>
    public string GradingType { get; set; } = "points";
    public List<string> SubmissionTypes { get; set; } = [];
    /// <summary>How many tries Canvas allows; -1 or null: as many as you like.</summary>
    public int? AllowedAttempts { get; set; }
    public string HtmlUrl { get; set; } = "";
    /// <summary>The instructions, as Markdown.</summary>
    public string Instructions { get; set; } = "";
    public List<FileRef> InstructionFiles { get; set; } = [];
    public List<RubricCriterion> Rubric { get; set; } = [];
    public long? QuizId { get; set; }
    public long? DiscussionTopicId { get; set; }
    /// <summary>Its folder in the class ("Canvas/assignments/Problem set 4"): spec.md, feedback.md, submission/.</summary>
    public string Folder { get; set; } = "";
    public SubmissionInfo? Submission { get; set; }

    static string S(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.String ? j.GetValue<string>() : "";
    static string? T(JsonNode? v) => S(v) is { Length: > 0 } s ? s : null;
    static double? D(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.Number
        ? double.Parse(j.ToJsonString(), CultureInfo.InvariantCulture) : null;
    static long? L(JsonNode? v) => D(v) is double d ? (long)d : null;

    /// <summary>An assignment from Canvas's JSON (with its <c>submission</c> summary when Canvas included one).</summary>
    public static AssignmentInfo From(JsonObject a, string folder)
    {
        var types = (a["submission_types"] as JsonArray ?? []).Select(S).Where(t => t.Length > 0).ToList();
        long? quiz = L(a["quiz_id"]), topic = L(a["discussion_topic"]?["id"]);
        return new AssignmentInfo
        {
            Id = L(a["id"]) ?? 0,
            Name = Py.Strip(S(a["name"])),
            Kind = quiz is not null || types.Contains("online_quiz") ? "quiz" : topic is not null || types.Contains("discussion_topic") ? "discussion" : "assignment",
            DueAt = T(a["due_at"]), UnlockAt = T(a["unlock_at"]), LockAt = T(a["lock_at"]),
            Points = D(a["points_possible"]),
            GradingType = T(a["grading_type"]) ?? "points",
            SubmissionTypes = types,
            AllowedAttempts = D(a["allowed_attempts"]) is double n ? (int)n : null,
            HtmlUrl = S(a["html_url"]),
            Instructions = HtmlText.ToMarkdown(S(a["description"])),
            Rubric = (a["rubric"] as JsonArray ?? []).OfType<JsonObject>().Select(r => new RubricCriterion
            {
                Id = S(r["id"]), Description = Py.Strip(S(r["description"])), LongDescription = Py.Strip(S(r["long_description"])), Points = D(r["points"]),
                Ratings = (r["ratings"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(x => new RubricRating { Id = S(x["id"]), Description = Py.Strip(S(x["description"])), Points = D(x["points"]) }).ToList(),
            }).ToList(),
            QuizId = quiz, DiscussionTopicId = topic,
            Folder = folder,
            Submission = a["submission"] is JsonObject s ? SubmissionInfo.Summary(s) : null,
        };
    }
}

/// <summary>A rubric row: what's marked, out of how many points, and the levels a marker picks from.</summary>
public sealed class RubricCriterion
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string LongDescription { get; set; } = "";
    public double? Points { get; set; }
    public List<RubricRating> Ratings { get; set; } = [];
}

public sealed class RubricRating
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public double? Points { get; set; }
}

/// <summary>The student's submission: its state and mark, what they handed in, and what the grader said.</summary>
public sealed class SubmissionInfo
{
    /// <summary>Canvas's workflow_state: unsubmitted | submitted | pending_review | graded.</summary>
    public string State { get; set; } = "";
    public int? Attempt { get; set; }
    public string? SubmittedAt { get; set; }
    public string? GradedAt { get; set; }
    public double? Score { get; set; }
    /// <summary>The grade as Canvas shows it: "18", "A-", "complete".</summary>
    public string? Grade { get; set; }
    public bool Late { get; set; }
    public bool Missing { get; set; }
    public bool Excused { get; set; }
    public double? PointsDeducted { get; set; }
    /// <summary>A text entry (or the link) handed in, as Markdown.</summary>
    public string Body { get; set; } = "";
    /// <summary>The latest attempt's files (saved in submission/).</summary>
    public List<FileRef> Files { get; set; } = [];
    /// <summary>Every attempt, oldest first, the latest included; older ones' files are in submission/attempt N/.</summary>
    public List<AttemptInfo> Attempts { get; set; } = [];
    /// <summary>Rubric marks by criterion id.</summary>
    public Dictionary<string, RubricMark> Marks { get; set; } = [];
    public List<CommentInfo> Comments { get; set; } = [];

    static string S(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.String ? j.GetValue<string>() : "";
    static string? T(JsonNode? v) => S(v) is { Length: > 0 } s ? s : null;
    static bool B(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.True;
    static double? D(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.Number
        ? double.Parse(j.ToJsonString(), CultureInfo.InvariantCulture) : null;

    /// <summary>Where a submission stands (state, mark, dates), from Canvas's JSON; no files or comments.</summary>
    public static SubmissionInfo Summary(JsonObject s) => new()
    {
        State = S(s["workflow_state"]),
        Attempt = D(s["attempt"]) is double n ? (int)n : null,
        SubmittedAt = T(s["submitted_at"]), GradedAt = T(s["graded_at"]),
        Score = D(s["score"]), Grade = T(s["grade"]),
        Late = B(s["late"]), Missing = B(s["missing"]), Excused = B(s["excused"]),
        PointsDeducted = D(s["points_deducted"]),
    };

    /// <summary>This summary with another read's files, marks, comments and attempts (none when null).</summary>
    public SubmissionInfo WithDetailsOf(SubmissionInfo? other) => new()
    {
        State = State, Attempt = Attempt, SubmittedAt = SubmittedAt, GradedAt = GradedAt, Score = Score, Grade = Grade,
        Late = Late, Missing = Missing, Excused = Excused, PointsDeducted = PointsDeducted,
        Body = other?.Body ?? "", Files = other?.Files ?? [], Attempts = other?.Attempts ?? [],
        Marks = other?.Marks ?? [], Comments = other?.Comments ?? [],
    };
}

public sealed class AttemptInfo
{
    public int Attempt { get; set; }
    public string? SubmittedAt { get; set; }
    public bool Late { get; set; }
    public List<FileRef> Files { get; set; } = [];
}

/// <summary>One rubric mark: the points, the level picked, and what the marker wrote.</summary>
public sealed class RubricMark
{
    public double? Points { get; set; }
    public string? RatingId { get; set; }
    public string Comment { get; set; } = "";
}

/// <summary>A comment on the submission: who (display name only), when, what, and what they attached.</summary>
public sealed class CommentInfo
{
    public string Author { get; set; } = "";
    public string? At { get; set; }
    public string Text { get; set; } = "";
    public List<FileRef> Files { get; set; } = [];
    /// <summary>An audio or video comment: a link to it on Canvas (never downloaded).</summary>
    public string? MediaUrl { get; set; }
    /// <summary>The student wrote it (not news when it's new).</summary>
    public bool Mine { get; set; }
}

/// <summary>A Canvas file: what it is, where the sync saved it (<see cref="Local"/>, in the class's folder), or why it
/// didn't (<see cref="Skipped"/>: "too big", "video" (video or audio), "locked"), and where it is on Canvas.</summary>
public sealed class FileRef
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long? Size { get; set; }
    public string ContentType { get; set; } = "";
    public string? UpdatedAt { get; set; }
    public string? Local { get; set; }
    public string? Skipped { get; set; }
    public string Url { get; set; } = "";
}

// --- filled by later listings (modules, the Files area, pages, announcements, quizzes, discussions, the planner) ---

/// <summary>A module and its items, in Canvas's order.</summary>
public sealed class ModuleInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Position { get; set; }
    public string? State { get; set; }
    public string? UnlockAt { get; set; }
    public int ItemsCount { get; set; }
    public List<ModuleItemInfo> Items { get; set; } = [];
}

/// <summary>A module item: kind is file | page | assignment | quiz | discussion | link | tool | header.</summary>
public sealed class ModuleItemInfo
{
    public long Id { get; set; }
    public string Type { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public int Indent { get; set; }
    public string? Format { get; set; }
    public long? Size { get; set; }
    public string? Local { get; set; }
    public bool Saved { get; set; }
    /// <summary>box | drive | onedrive | youtube, for a link to one of those.</summary>
    public string? Source { get; set; }
    public string Url { get; set; } = "";
    public string? ExternalUrl { get; set; }
    public string? PageUrl { get; set; }
    public long? AssignmentId { get; set; }
    public bool Locked { get; set; }
    public string? Skipped { get; set; }
}

/// <summary>A file in the course's Files area.</summary>
public sealed class CourseFileInfo
{
    public long Id { get; set; }
    public string Folder { get; set; } = "";
    public string Name { get; set; } = "";
    public long? Size { get; set; }
    public string ContentType { get; set; } = "";
    public string? Format { get; set; }
    public string? UpdatedAt { get; set; }
    public string? Local { get; set; }
    public string? Skipped { get; set; }
}

/// <summary>A course page (a wiki page), saved as Markdown.</summary>
public sealed class PageInfo
{
    public string Title { get; set; } = "";
    /// <summary>Canvas's slug for it ("lab-3-instructions").</summary>
    public string Url { get; set; } = "";
    public string HtmlUrl { get; set; } = "";
    public string? UpdatedAt { get; set; }
    public string? Local { get; set; }
    public bool InModule { get; set; }
    public bool FrontPage { get; set; }
    public bool Locked { get; set; }
}

/// <summary>An announcement: title, when, who (display name), whether it's been read, and its text.</summary>
public sealed class AnnouncementInfo
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? PostedAt { get; set; }
    public string Author { get; set; } = "";
    public bool ReadOnCanvas { get; set; }
    public string Body { get; set; } = "";
    public List<FileRef> Files { get; set; } = [];
    public string HtmlUrl { get; set; } = "";
}

/// <summary>A quiz's facts (never its questions).</summary>
public sealed class QuizInfo
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string QuizType { get; set; } = "";
    public string? DueAt { get; set; }
    public double? Points { get; set; }
    public int? TimeLimit { get; set; }
    public int? AllowedAttempts { get; set; }
    public int? QuestionCount { get; set; }
    public string Description { get; set; } = "";
    public long? AssignmentId { get; set; }
    public bool Locked { get; set; }
    public string HtmlUrl { get; set; } = "";
    public string? Local { get; set; }
}

/// <summary>A discussion's prompt (never anyone's replies).</summary>
public sealed class DiscussionInfo
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public long? AssignmentId { get; set; }
    public string? TodoDate { get; set; }
    public bool Locked { get; set; }
    public string HtmlUrl { get; set; } = "";
    public string? Local { get; set; }
}

/// <summary>A to-do from Canvas's planner that isn't an assignment (an ungraded quiz, discussion or page with a date,
/// or the student's own note).</summary>
public sealed class TodoInfo
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string? TodoAt { get; set; }
    public string HtmlUrl { get; set; } = "";
    public bool MarkedDone { get; set; }
}
