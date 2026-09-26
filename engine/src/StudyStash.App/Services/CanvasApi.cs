using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyStash.App.Services;

/// <summary>
/// The library's Canvas JSON API (WS5 PLAN §5), as one static class so its wire records never clash with
/// <c>StudyStash.App.Services.CanvasCourse</c>/<c>CanvasLink</c> (Settings.cs) or <c>StudyStash.Core.Canvas</c>'s own
/// types. Every record uses <c>init</c> properties, never a primary constructor: a key the library hasn't shipped
/// yet must deserialize to a default, not throw.
/// </summary>
public static class CanvasApi
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Canvas ids travel as numbers or strings depending on the library version; we always want a string.</summary>
    public sealed class IdConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out long l) ? l.ToString(CultureInfo.InvariantCulture) : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Expected a Canvas id (a string or a number), got {reader.TokenType}."),
        };

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    // ---- GET canvas/state ----

    public sealed record State
    {
        [JsonPropertyName("state")] public string Status { get; init; } = "";
        public string School { get; init; } = "";
        public string Url { get; init; } = "";
        public ExtensionInfo? Extension { get; init; }
        public DateTimeOffset? LastSync { get; init; }
        public DateTimeOffset? NextSync { get; init; }
        public int PollMinutes { get; init; }
        public SyncingInfo? Syncing { get; init; }
        public DateTimeOffset? PausedUntil { get; init; }
        public ErrorInfo? Error { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = [];
    }

    public sealed record ExtensionInfo
    {
        public DateTimeOffset? Seen { get; init; }
        public string Version { get; init; } = "";
        public string? Latest { get; init; }
        public bool Outdated { get; init; }
        public ExtensionUpdate? Updated { get; init; }
    }

    public sealed record ExtensionUpdate
    {
        public string From { get; init; } = "";
        public string To { get; init; } = "";
        public DateTimeOffset At { get; init; }
    }

    public sealed record SyncingInfo
    {
        public int Left { get; init; }
        public int Total { get; init; }
        public IReadOnlyList<string> Classes { get; init; } = [];
    }

    public sealed record ErrorInfo
    {
        public string Text { get; init; } = "";
        public DateTimeOffset At { get; init; }
    }

    // ---- GET canvas (overview, old keys kept); also T1's fallback source for /state on an older library ----

    public sealed record Overview
    {
        public string Url { get; init; } = "";
        /// <summary>Old key: class name → linked course id (0 = not linked).</summary>
        public IReadOnlyDictionary<string, double> Courses { get; init; } = new Dictionary<string, double>();
        /// <summary>Old key: course id (as a string) → its name.</summary>
        public IReadOnlyDictionary<string, string> Available { get; init; } = new Dictionary<string, string>();
        public IReadOnlyList<Course> CourseInfo { get; init; } = [];
        public IReadOnlyList<ChangeRow> LastChanges { get; init; } = [];
        public int PollMinutes { get; init; }
        public string ExtensionSeen { get; init; } = "";
        public string ExtensionVersion { get; init; } = "";
        public bool NeedsLogin { get; init; }
        public bool Syncing { get; init; }
        public int Left { get; init; }
        public string Error { get; init; } = "";
        public DateTimeOffset? LastSync { get; init; }
        public ExtensionUpdate? ExtensionUpdate { get; init; }
    }

    public sealed record ChangeRow
    {
        public string Kind { get; init; } = "";
        public string Text { get; init; } = "";
        public DateTimeOffset At { get; init; }
    }

    // ---- GET canvas/extension ----

    public sealed record ExtensionKey
    {
        public string Key { get; init; } = "";
        public string Canvas { get; init; } = "";
        public string Version { get; init; } = "";
    }

    // ---- GET canvas/classes ----

    public sealed record ClassRow
    {
        public string Class { get; init; } = "";
        public bool Linked { get; init; }
        public Course? Canvas { get; init; }
        [JsonConverter(typeof(IdConverter))] public string? Suggested { get; init; }
        public DateTimeOffset? LastSync { get; init; }
        public Counts Counts { get; init; } = new();
        public bool FilesHidden { get; init; }
        public Scout? Scout { get; init; }
    }

    public sealed record Course
    {
        [JsonConverter(typeof(IdConverter))] public string Id { get; init; } = "";
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string Term { get; init; } = "";
        public string Url { get; init; } = "";
    }

    public sealed record Counts
    {
        public int ToHandIn { get; init; }
        public int Done { get; init; }
        public int Modules { get; init; }
        public int Files { get; init; }
        public int Announcements { get; init; }
        public int AnnouncementsNew { get; init; }
    }

    /// <summary>What Scout (the AI that explores a newly-linked course) found. <see cref="State"/> here is
    /// done|exploring|waiting|failed|never.</summary>
    public sealed record Scout
    {
        public string State { get; init; } = "";
        public int Files { get; init; }
        public DateTimeOffset? When { get; init; }
        public string? Report { get; init; }
    }

    // ---- GET canvas/due, GET canvas/assignments ----

    public sealed record DueResponse
    {
        public DateTimeOffset? Synced { get; init; }
        public int ToHandIn { get; init; }
        public Item? Next { get; init; }
        public IReadOnlyList<Group> Groups { get; init; } = [];
    }

    public sealed record Group
    {
        public string Key { get; init; } = ""; // overdue|week|later|undated|handed_in
        public string Label { get; init; } = "";
        public IReadOnlyList<Item> Items { get; init; } = [];
    }

    /// <summary>One assignment/quiz/discussion as the Due list and a class's assignment tabs show it. Submitted,
    /// GradedAt and MarkedDone are timestamps (their presence is the flag); Late/Missing/Excused are Canvas's own
    /// flags for a handed-in-or-not item.</summary>
    public sealed record Item
    {
        public string Class { get; init; } = "";
        [JsonConverter(typeof(IdConverter))] public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Kind { get; init; } = ""; // assignment|quiz|discussion|to-do
        public string? Due { get; init; } // library wall clock, yyyy-MM-ddTHH:mm
        public DateTimeOffset? DueAt { get; init; } // UTC
        public double? Points { get; init; }
        public string Status { get; init; } = "";
        public string Label { get; init; } = "";
        public double? Score { get; init; }
        public string? Grade { get; init; }
        public string? ScoreText { get; init; }
        public bool Late { get; init; }
        public bool Missing { get; init; }
        public bool Excused { get; init; }
        public DateTimeOffset? Submitted { get; init; }
        public DateTimeOffset? GradedAt { get; init; }
        public DateTimeOffset? MarkedDone { get; init; }
        public string? Url { get; init; }
        public string? Folder { get; init; }
    }

    public sealed record AssignmentsResponse
    {
        public string Class { get; init; } = "";
        public IReadOnlyList<Item> ToHandIn { get; init; } = [];
        public IReadOnlyList<Item> Done { get; init; } = [];
    }

    // ---- GET canvas/assignment ----

    public sealed record AssignmentDetail
    {
        public string Class { get; init; } = "";
        [JsonConverter(typeof(IdConverter))] public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string? Due { get; init; }
        public DateTimeOffset? DueAt { get; init; }
        public double? Points { get; init; }
        public string Status { get; init; } = "";
        public string Label { get; init; } = "";
        public double? Score { get; init; }
        public string? Grade { get; init; }
        public string? ScoreText { get; init; }
        public bool Late { get; init; }
        public bool Missing { get; init; }
        public bool Excused { get; init; }
        public DateTimeOffset? Submitted { get; init; }
        public DateTimeOffset? GradedAt { get; init; }
        public DateTimeOffset? MarkedDone { get; init; }
        public string? Url { get; init; }
        public string? Folder { get; init; }

        public string? Instructions { get; init; }
        public DateTimeOffset? UnlockAt { get; init; }
        public DateTimeOffset? LockAt { get; init; }
        public IReadOnlyList<string> SubmissionTypes { get; init; } = [];
        public int? AllowedAttempts { get; init; }
        public string? GradingType { get; init; }
        public IReadOnlyList<AssignmentFile> Files { get; init; } = [];
        public IReadOnlyList<RubricRow> Rubric { get; init; } = [];
        public SubmissionInfo? Submission { get; init; }
        public IReadOnlyList<CommentInfo> Comments { get; init; } = [];
        public string? Quiz { get; init; }
        public string? Spec { get; init; }
        public string? Feedback { get; init; }
    }

    public sealed record AssignmentFile
    {
        [JsonConverter(typeof(IdConverter))] public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public long Size { get; init; }
        public string? ContentType { get; init; }
        public string? Format { get; init; }
        public bool Local { get; init; }
        public bool Skipped { get; init; }
        public string? Url { get; init; }
    }

    public sealed record RubricRow
    {
        [JsonConverter(typeof(IdConverter))] public string Id { get; init; } = "";
        public string Criterion { get; init; } = "";
        public string? Description { get; init; }
        public double Points { get; init; }
        public IReadOnlyList<RatingRow> Ratings { get; init; } = [];
        public MarkInfo? Mark { get; init; }
    }

    public sealed record RatingRow
    {
        public string Label { get; init; } = "";
        public double Points { get; init; }
    }

    public sealed record MarkInfo
    {
        public double? Points { get; init; }
        public string? Rating { get; init; }
        public string? Comment { get; init; }
    }

    public sealed record SubmissionInfo
    {
        public string State { get; init; } = "";
        public int? Attempt { get; init; }
        public DateTimeOffset? SubmittedAt { get; init; }
        public DateTimeOffset? GradedAt { get; init; }
        public double? Score { get; init; }
        public string? Grade { get; init; }
        public bool Late { get; init; }
        public double? PointsDeducted { get; init; }
        public string? Body { get; init; }
        public IReadOnlyList<AssignmentFile> Files { get; init; } = [];
        public int? Attempts { get; init; }
    }

    public sealed record CommentInfo
    {
        public string Author { get; init; } = "";
        public DateTimeOffset At { get; init; }
        public string Text { get; init; } = "";
        public IReadOnlyList<AssignmentFile> Files { get; init; } = [];
        public string? MediaUrl { get; init; }
    }

    // ---- GET canvas/modules ----

    public sealed record ModulesResponse
    {
        public int Count { get; init; }
        public IReadOnlyList<ModuleRow> Modules { get; init; } = [];
    }

    public sealed record ModuleRow
    {
        [JsonConverter(typeof(IdConverter))] public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public int Position { get; init; }
        public string? State { get; init; }
        public DateTimeOffset? UnlockAt { get; init; }
        public int ItemsCount { get; init; }
        public IReadOnlyList<ModuleItem> Items { get; init; } = [];
    }

    public sealed record ModuleItem
    {
        [JsonConverter(typeof(IdConverter))] public string Id { get; init; } = "";
        public string Type { get; init; } = "";
        public string? Kind { get; init; } // file|page|assignment|quiz|discussion|link|tool|header
        public string Title { get; init; } = "";
        public int Indent { get; init; }
        public string? Format { get; init; }
        public long? Size { get; init; }
        public bool Local { get; init; }
        public bool Saved { get; init; }
        public string? Source { get; init; } // box|drive|onedrive|youtube|null
        public string? Url { get; init; }
        public string? ExternalUrl { get; init; }
        [JsonConverter(typeof(IdConverter))] public string? AssignmentId { get; init; }
        public bool Locked { get; init; }
        public bool Skipped { get; init; }
    }

    // ---- GET canvas/files ----

    public sealed record FilesResponse
    {
        public bool Allowed { get; init; }
        public int Count { get; init; }
        public IReadOnlyList<FileRow> Files { get; init; } = [];
    }

    public sealed record FileRow
    {
        [JsonConverter(typeof(IdConverter))] public string Id { get; init; } = "";
        public string? Folder { get; init; }
        public string Name { get; init; } = "";
        public long Size { get; init; }
        public string? ContentType { get; init; }
        public string? Format { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
        public bool Local { get; init; }
        public bool Skipped { get; init; }
    }

    // ---- GET canvas/announcements ----

    public sealed record AnnouncementsResponse
    {
        public int Count { get; init; }
        public int New { get; init; }
        public IReadOnlyList<AnnouncementRow> Items { get; init; } = [];
    }

    public sealed record AnnouncementRow
    {
        [JsonConverter(typeof(IdConverter))] public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public DateTimeOffset PostedAt { get; init; }
        public string? Author { get; init; }
        public bool New { get; init; }
        public bool ReadOnCanvas { get; init; }
        public string? Body { get; init; }
        public IReadOnlyList<AssignmentFile> Files { get; init; } = [];
        public string? Url { get; init; }
    }

    // ---- GET canvas/notifications ----

    public sealed record NotificationsResponse
    {
        public DateTimeOffset? Last { get; init; }
        public IReadOnlyList<NotificationRow> Items { get; init; } = [];
    }

    public sealed record NotificationRow
    {
        [JsonConverter(typeof(IdConverter))] public string Id { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Title { get; init; } = "";
        public string? Text { get; init; }
        public string? Class { get; init; }
        [JsonConverter(typeof(IdConverter))] public string? AssignmentId { get; init; }
        [JsonConverter(typeof(IdConverter))] public string? AnnouncementId { get; init; }
        public DateTimeOffset At { get; init; }
        public bool Seen { get; init; }
    }

    // ---- GET files?class=&path= ----

    public sealed record TextFile
    {
        public string Text { get; init; } = "";
    }
}
