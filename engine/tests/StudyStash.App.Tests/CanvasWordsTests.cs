using StudyStash.App.Services;
using StudyStash.App.ViewModels;

namespace StudyStash.App.Tests;

public class CanvasWordsTests
{
    static readonly TimeZoneInfo Zone = CanvasFixtures.Zone;
    static readonly DateTimeOffset Now = CanvasFixtures.Now; // Thu 25 Sep 2025, 10:24 local

    static CanvasApi.Item Item(string cls = "CS 101", string id = "1", string name = "An assignment", string kind = "assignment",
        DateTimeOffset? dueAt = null, double? points = null, string status = "to_hand_in", string label = "To do",
        double? score = null, string? grade = null, string? scoreText = null, bool late = false, bool missing = false,
        bool excused = false, DateTimeOffset? submitted = null, DateTimeOffset? gradedAt = null, DateTimeOffset? markedDone = null) =>
        new()
        {
            Class = cls, Id = id, Name = name, Kind = kind, DueAt = dueAt, Points = points, Status = status, Label = label,
            Score = score, Grade = grade, ScoreText = scoreText, Late = late, Missing = missing, Excused = excused,
            Submitted = submitted, GradedAt = gradedAt, MarkedDone = markedDone,
        };

    // ---- Full date ----

    [Fact]
    public void Full_matches_the_design_s_example() =>
        Assert.Equal("Tue 30 Sep, 11:59 PM", CanvasWords.Full(new DateTimeOffset(2025, 10, 1, 6, 59, 0, TimeSpan.Zero), Zone, Now));

    [Fact]
    public void Full_adds_the_year_when_it_is_not_this_one() =>
        Assert.Equal("Mon 5 Jan 2026, 12:00 PM", CanvasWords.Full(new DateTimeOffset(2026, 1, 5, 20, 0, 0, TimeSpan.Zero), Zone, Now));

    // ---- Day, ShortDay, ScoutDay ----

    [Fact]
    public void Day_is_weekday_day_and_month() =>
        Assert.Equal("Mon 22 Sep", CanvasWords.Day(new DateTimeOffset(2025, 9, 22, 18, 0, 0, TimeSpan.Zero), Zone));

    [Fact]
    public void ShortDay_drops_the_month() =>
        Assert.Equal("Tue 23", CanvasWords.ShortDay(new DateTimeOffset(2025, 9, 23, 20, 0, 0, TimeSpan.Zero), Zone));

    [Fact]
    public void ScoutDay_drops_the_weekday() =>
        Assert.Equal("20 Sep", CanvasWords.ScoutDay(new DateTimeOffset(2025, 9, 20, 18, 0, 0, TimeSpan.Zero), Zone));

    // ---- Clock ----

    [Theory]
    [InlineData("2025-09-25T17:24:00Z", "10:24")]
    [InlineData("2025-09-25T15:12:00Z", "8:12")]
    [InlineData("2025-09-25T18:24:00Z", "11:24")]
    public void Clock_has_no_am_or_pm(string at, string expected) => Assert.Equal(expected, CanvasWords.Clock(DateTimeOffset.Parse(at), Zone));

    // ---- When due ----

    [Fact]
    public void When_today_reads_Today() =>
        Assert.Equal("Today, 8:24 PM", CanvasWords.When(new DateTimeOffset(2025, 9, 26, 3, 24, 0, TimeSpan.Zero), Zone, Now));

    [Fact]
    public void When_tomorrow_matches_the_design_s_example() =>
        Assert.Equal("Tomorrow, 9:00 AM", CanvasWords.When(new DateTimeOffset(2025, 9, 26, 16, 0, 0, TimeSpan.Zero), Zone, Now));

    [Fact]
    public void When_beyond_tomorrow_is_the_full_date() =>
        Assert.Equal("Tue 30 Sep, 11:59 PM", CanvasWords.When(new DateTimeOffset(2025, 10, 1, 6, 59, 0, TimeSpan.Zero), Zone, Now));

    // ---- Ago ----

    [Fact]
    public void Ago_under_a_minute_is_just_now() => Assert.Equal("just now", CanvasWords.Ago(Now.AddSeconds(-30), Zone, Now));

    [Fact]
    public void Ago_matches_the_design_s_2_min_ago() => Assert.Equal("2 min ago", CanvasWords.Ago(Now.AddMinutes(-2), Zone, Now));

    [Fact]
    public void Ago_counts_hours() => Assert.Equal("3 h ago", CanvasWords.Ago(Now.AddHours(-3), Zone, Now));

    [Fact]
    public void Ago_beyond_a_day_names_the_day() => Assert.Equal($"on {CanvasWords.Day(Now.AddDays(-2), Zone)}", CanvasWords.Ago(Now.AddDays(-2), Zone, Now));

    // ---- NotificationAge ----

    [Fact]
    public void NotificationAge_under_a_minute_is_now() => Assert.Equal("now", CanvasWords.NotificationAge(Now.AddSeconds(-10), Zone, Now));

    [Fact]
    public void NotificationAge_beyond_a_day_is_the_weekday() => Assert.Equal("Tue", CanvasWords.NotificationAge(Now.AddDays(-2), Zone, Now));

    // ---- DueRelative ----

    [Fact]
    public void DueRelative_matches_the_design_s_due_in_5_days() => Assert.Equal("Due in 5 days", CanvasWords.DueRelative(Now.AddDays(5), Zone, Now));

    [Fact]
    public void DueRelative_today_and_tomorrow() =>
        Assert.Multiple(
            () => Assert.Equal("Due today", CanvasWords.DueRelative(Now, Zone, Now)),
            () => Assert.Equal("Due tomorrow", CanvasWords.DueRelative(Now.AddDays(1), Zone, Now)));

    [Fact]
    public void DueRelative_yesterday_and_further_back() =>
        Assert.Multiple(
            () => Assert.Equal("Was due yesterday", CanvasWords.DueRelative(Now.AddDays(-1), Zone, Now)),
            () => Assert.Equal("Was due 3 days ago", CanvasWords.DueRelative(Now.AddDays(-3), Zone, Now)));

    // ---- Right of a row ----

    [Theory]
    [InlineData(null, "Missing", "Missing")]
    [InlineData(null, "To do", "To do")]
    [InlineData(null, "Submitted", "Submitted")]
    [InlineData("18/20", "Graded", "18/20")]
    [InlineData(null, "Excused", "Excused")]
    public void RightLabel_shows_the_score_when_graded_else_the_label(string? scoreText, string label, string expected) =>
        Assert.Equal(expected, CanvasWords.RightLabel(Item(scoreText: scoreText, label: label)));

    // ---- Due list sub ----

    [Fact]
    public void DueSub_an_overdue_item() =>
        Assert.Equal("HIST 210 · Was due Mon 22 Sep, 11:59 PM", CanvasWords.DueSub(
            Item(cls: "HIST 210", status: "missing", label: "Missing", missing: true, dueAt: new DateTimeOffset(2025, 9, 23, 6, 59, 0, TimeSpan.Zero)),
            Zone, Now));

    [Fact]
    public void DueSub_something_still_to_do() =>
        Assert.Equal("CALC II · Tomorrow, 9:00 AM", CanvasWords.DueSub(
            Item(cls: "CALC II", status: "to_hand_in", label: "To do", dueAt: new DateTimeOffset(2025, 9, 26, 16, 0, 0, TimeSpan.Zero)),
            Zone, Now));

    [Fact]
    public void DueSub_something_submitted() =>
        Assert.Equal("BIO 110 · Submitted Wed 24 Sep", CanvasWords.DueSub(
            Item(cls: "BIO 110", status: "submitted", label: "Submitted", submitted: new DateTimeOffset(2025, 9, 25, 3, 15, 0, TimeSpan.Zero)),
            Zone, Now));

    [Fact]
    public void DueSub_something_graded() =>
        Assert.Equal("CS 101 · Graded Mon 22 Sep", CanvasWords.DueSub(
            Item(cls: "CS 101", status: "graded", label: "Graded", gradedAt: new DateTimeOffset(2025, 9, 22, 18, 0, 0, TimeSpan.Zero)),
            Zone, Now));

    [Fact]
    public void DueSub_something_marked_done() =>
        Assert.Equal("CS 101 · Marked done Mon 22 Sep", CanvasWords.DueSub(
            Item(cls: "CS 101", status: "marked_done", label: "Done", markedDone: new DateTimeOffset(2025, 9, 22, 18, 0, 0, TimeSpan.Zero)),
            Zone, Now));

    // ---- Due header ----

    [Fact]
    public void DueHeader_matches_the_design_s_example() =>
        Assert.Equal("3 to hand in · synced 10:24", CanvasWords.DueHeader(3, new DateTimeOffset(2025, 9, 25, 17, 24, 0, TimeSpan.Zero), Zone));

    [Fact]
    public void DueHeader_at_zero_says_nothing_to_hand_in() =>
        Assert.Equal("Nothing to hand in · synced 10:24", CanvasWords.DueHeader(0, new DateTimeOffset(2025, 9, 25, 17, 24, 0, TimeSpan.Zero), Zone));

    [Fact]
    public void DueHeader_never_synced() => Assert.Equal("Not synced yet", CanvasWords.DueHeader(3, null, Zone));

    // ---- Class tab rows ----

    [Fact]
    public void ClassTabRow_to_hand_in_leads_with_points() =>
        Assert.Equal("20 pts · Tue 30 Sep, 11:59 PM", CanvasWords.ClassTabRow(
            Item(status: "to_hand_in", points: 20, dueAt: new DateTimeOffset(2025, 10, 1, 6, 59, 0, TimeSpan.Zero)), Zone, Now));

    [Theory]
    [InlineData("graded", "Graded", "2025-09-17T06:59:00Z", "Graded · due Tue 16 Sep")]
    [InlineData("submitted", "Submitted late", "2025-09-12T06:59:00Z", "Submitted late · due Thu 11 Sep")]
    [InlineData("excused", "Excused", "2025-09-06T06:59:00Z", "Excused · due Fri 5 Sep")]
    public void ClassTabRow_a_done_item_leads_with_its_label(string status, string label, string dueAt, string expected) =>
        Assert.Equal(expected, CanvasWords.ClassTabRow(Item(status: status, label: label, dueAt: DateTimeOffset.Parse(dueAt)), Zone, Now));

    [Fact]
    public void ClassTabRow_with_no_due_date() =>
        Assert.Equal("Excused · No due date", CanvasWords.ClassTabRow(Item(status: "excused", label: "Excused"), Zone, Now));

    // ---- kind word, points, score ----

    [Theory]
    [InlineData("assignment", "Assignment")]
    [InlineData("quiz", "Quiz")]
    [InlineData("discussion", "Discussion")]
    [InlineData("to-do", "To-do")]
    public void KindWord_matches_the_design_s_words(string kind, string expected) => Assert.Equal(expected, CanvasWords.KindWord(kind));

    [Theory]
    [InlineData(20.0, "20")]
    [InlineData(7.5, "7.5")]
    [InlineData(null, "")]
    public void PointsText_drops_a_trailing_zero(double? points, string expected) => Assert.Equal(expected, CanvasWords.PointsText(points));

    [Fact]
    public void ScoreOrGradeText_matches_the_design_s_example() => Assert.Equal("18 / 20", CanvasWords.ScoreOrGradeText(18, 20, null, "points"));

    [Fact]
    public void ScoreOrGradeText_shows_the_letter_grade_when_that_s_how_it_s_graded() =>
        Assert.Equal("B+", CanvasWords.ScoreOrGradeText(null, null, "B+", "letter"));

    [Fact]
    public void DetailHeader_matches_the_design_s_example() => Assert.Equal("CS 101 · Assignment", CanvasWords.DetailHeader("CS 101", "assignment"));

    // ---- rubric ----

    [Fact]
    public void RubricPoints_with_no_mark_yet() => Assert.Equal("10 pts", CanvasWords.RubricPoints(new CanvasApi.RubricRow { Points = 10 }));

    [Fact]
    public void RubricPoints_once_marked() =>
        Assert.Equal("8 / 10", CanvasWords.RubricPoints(new CanvasApi.RubricRow { Points = 10, Mark = new CanvasApi.MarkInfo { Points = 8 } }));

    [Fact]
    public void Quote_wraps_a_comment_in_curly_quotes() =>
        Assert.Equal("“The frame for n = 1 is missing in 3b.”", CanvasWords.Quote("The frame for n = 1 is missing in 3b."));

    // ---- submission ----

    [Fact]
    public void SubmissionText_graded() =>
        Assert.Equal(new CanvasWords.SubmissionCopy("Graded · 18 / 20", "Submitted Tue 16 Sep, 9:41 PM"), CanvasWords.SubmissionText(
            new CanvasApi.SubmissionInfo { GradedAt = new DateTimeOffset(2025, 9, 22, 18, 0, 0, TimeSpan.Zero), SubmittedAt = new DateTimeOffset(2025, 9, 17, 4, 41, 0, TimeSpan.Zero) },
            18, 20, null, "points", excused: false, dueAt: new DateTimeOffset(2025, 9, 17, 6, 59, 0, TimeSpan.Zero), Zone, Now));

    [Fact]
    public void SubmissionText_nothing_handed_in_yet() =>
        Assert.Equal(new CanvasWords.SubmissionCopy("Nothing handed in yet", "Due in 5 days"), CanvasWords.SubmissionText(
            null, null, 20, null, "points", excused: false, dueAt: Now.AddDays(5), Zone, Now));

    [Fact]
    public void SubmissionText_excused() =>
        Assert.Equal(new CanvasWords.SubmissionCopy("Excused", ""), CanvasWords.SubmissionText(
            null, null, 5, null, "points", excused: true, dueAt: Now.AddDays(1), Zone, Now));

    [Theory]
    [InlineData(false, "Submitted")]
    [InlineData(true, "Submitted late")]
    public void SubmissionText_handed_in_but_not_graded_yet(bool late, string expected) =>
        Assert.Equal(expected, CanvasWords.SubmissionText(
            new CanvasApi.SubmissionInfo { Late = late, SubmittedAt = Now.AddDays(-1) }, null, 20, null, "points", excused: false, dueAt: Now, Zone, Now).Status);

    [Fact]
    public void HandItInLink_is_the_design_s_words() => Assert.Equal("Hand it in on Canvas ↗", CanvasWords.HandItInLink);

    // ---- sizes ----

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(4096, "4 KB")]
    [InlineData(1258291, "1.2 MB")]
    [InlineData(52428800, "50 MB")]
    public void Size_matches_the_design_s_examples(long bytes, string expected) => Assert.Equal(expected, CanvasWords.Size(bytes));

    // ---- class header ----

    [Theory]
    [InlineData(12, "12 lectures")]
    [InlineData(1, "1 lecture")]
    public void LectureCountText_singular_and_plural(int n, string expected) => Assert.Equal(expected, CanvasWords.LectureCountText(n));

    [Fact]
    public void ClassCanvasLine_with_a_code() => Assert.Equal("COMP 101 on Canvas", CanvasWords.ClassCanvasLine("COMP 101", "COMP 101 · Intro to Programming"));

    [Fact]
    public void ClassCanvasLine_falls_back_to_the_course_name() => Assert.Equal("General Studies on Canvas", CanvasWords.ClassCanvasLine(null, "General Studies"));

    [Theory]
    [InlineData("done", 14, "2025-09-20T18:00:00Z", "Scout explored this course · 14 files saved · 20 Sep")]
    [InlineData("exploring", 0, null, "Scout exploring…")]
    [InlineData("failed", 0, null, "Scout didn’t finish")]
    public void ScoutHeaderLine_matches_the_design_s_words(string state, int files, string? when, string expected) =>
        Assert.Equal(expected, CanvasWords.ScoutHeaderLine(new CanvasApi.Scout { State = state, Files = files, When = when is null ? null : DateTimeOffset.Parse(when) }, Zone));

    [Theory]
    [InlineData("done", 14, "2025-09-20T18:00:00Z", "Scout done · 14 files saved · 20 Sep")]
    [InlineData("exploring", 0, null, "Scout exploring…")]
    [InlineData("failed", 0, null, "Scout didn’t finish")]
    public void ScoutSettingsLine_matches_the_design_s_words(string state, int files, string? when, string expected) =>
        Assert.Equal(expected, CanvasWords.ScoutSettingsLine(new CanvasApi.Scout { State = state, Files = files, When = when is null ? null : DateTimeOffset.Parse(when) }, Zone));

    [Fact]
    public void NotMatched_is_the_design_s_words() => Assert.Equal("Not matched, so nothing syncs", CanvasWords.NotMatched);

    // ---- Settings ----

    [Theory]
    [InlineData(15, "Every 15 minutes")]
    [InlineData(30, "Every 30 minutes")]
    [InlineData(60, "Every hour")]
    [InlineData(180, "Every 3 hours")]
    [InlineData(1440, "Once a day")]
    public void PollIntervalText_matches_the_design_s_choices(int minutes, string expected) => Assert.Equal(expected, CanvasWords.PollIntervalText(minutes));

    [Fact]
    public void ExtensionCheckedInLine_matches_the_design_s_example() =>
        Assert.Equal("Checked in 2 min ago · version 1.4", CanvasWords.ExtensionCheckedInLine(Now.AddMinutes(-2), "1.4", Zone, Now));

    [Fact]
    public void LastSyncLine_matches_the_design_s_example() =>
        Assert.Equal("Last sync · 10:24", CanvasWords.LastSyncLine(new DateTimeOffset(2025, 9, 25, 17, 24, 0, TimeSpan.Zero), Zone));

    [Fact]
    public void ConnectedSummary_matches_the_design_s_example() =>
        Assert.Equal("Connected · last sync 10:24", CanvasWords.ConnectedSummary(new DateTimeOffset(2025, 9, 25, 17, 24, 0, TimeSpan.Zero), Zone));

    // ---- modules, files, announcements ----

    [Fact]
    public void ModuleItemWord_from_the_design_s_week_4()
    {
        var m = CanvasFixtures.Load<CanvasApi.ModulesResponse>("modules-cs101");
        var week4 = m.Modules.Single(x => x.Name == "Week 4 · Recursion");
        Assert.Equal("PDF", CanvasWords.ModuleItemWord(week4.Items[0]));
        Assert.Equal("Page", CanvasWords.ModuleItemWord(week4.Items[1]));
        Assert.Equal("Saved from Box", CanvasWords.ModuleItemWord(week4.Items[2]));
        Assert.Equal("Saved from Drive", CanvasWords.ModuleItemWord(week4.Items[3]));
    }

    [Theory]
    [InlineData(true, "video", "Video")]
    [InlineData(true, "pdf", "Too big")]
    public void ModuleItemWord_a_skipped_item(bool skipped, string format, string expected) =>
        Assert.Equal(expected, CanvasWords.ModuleItemWord(new CanvasApi.ModuleItem { Skipped = skipped, Format = format }));

    [Fact]
    public void ModuleItemWord_a_locked_item() => Assert.Equal("Locked", CanvasWords.ModuleItemWord(new CanvasApi.ModuleItem { Locked = true }));

    [Fact]
    public void ModuleItemWord_a_link_not_yet_saved() => Assert.Equal("Link", CanvasWords.ModuleItemWord(new CanvasApi.ModuleItem { Kind = "link", Saved = false }));

    [Theory]
    [InlineData(5, "5 items")]
    [InlineData(1, "1 item")]
    public void ItemsCountText_singular_and_plural(int n, string expected) => Assert.Equal(expected, CanvasWords.ItemsCountText(n));

    [Theory]
    [InlineData("Modules", 8, "Modules 8")]
    [InlineData("Files", 23, "Files 23")]
    public void CountLine_matches_the_design_s_examples(string label, int n, string expected) => Assert.Equal(expected, CanvasWords.CountLine(label, n));

    [Fact]
    public void AnnouncementsCountText_with_something_new() => Assert.Equal("4 · 1 new", CanvasWords.AnnouncementsCountText(4, 1));

    [Fact]
    public void AnnouncementsCountText_with_nothing_new() => Assert.Equal("4", CanvasWords.AnnouncementsCountText(4, 0));

    // ---- quick panel, dropdown ----

    [Fact]
    public void QuickDueLine_an_overdue_item() =>
        Assert.Equal("Missing · was due Mon", CanvasWords.QuickDueLine(
            Item(label: "Missing", missing: true, dueAt: new DateTimeOffset(2025, 9, 23, 6, 59, 0, TimeSpan.Zero)), Zone, Now));

    [Fact]
    public void QuickDueLine_something_still_to_do() =>
        Assert.Equal("Tomorrow, 9:00 AM", CanvasWords.QuickDueLine(Item(dueAt: new DateTimeOffset(2025, 9, 26, 16, 0, 0, TimeSpan.Zero)), Zone, Now));

    [Fact]
    public void DropdownNextDue_matches_the_design_s_example()
    {
        var due = CanvasFixtures.Load<CanvasApi.DueResponse>("due");
        Assert.Equal("Next due: Quiz 3 practice · Tomorrow, 9:00 AM", CanvasWords.DropdownNextDue(due.Next!, Zone, Now));
    }

    // ---- joining a list ----

    [Fact]
    public void JoinAnd_one_two_and_three_items() =>
        Assert.Multiple(
            () => Assert.Equal("CS 101", CanvasWords.JoinAnd(["CS 101"])),
            () => Assert.Equal("CS 101 and BIO 110", CanvasWords.JoinAnd(["CS 101", "BIO 110"])),
            () => Assert.Equal("BIO 110, CALC II and HIST 210", CanvasWords.JoinAnd(["BIO 110", "CALC II", "HIST 210"])));

    // ---- the states screen (design 08) ----

    [Theory]
    [InlineData("state-not-set-up", "Connect Canvas", "Bring in assignments, due dates and course files next to your lectures.")]
    [InlineData("state-no-extension", "Finish setting up the Chrome extension", "It takes three clicks in Chrome.")]
    [InlineData("state-chrome-away", "Is Chrome open?", "Chrome last checked in at 8:12. Canvas syncs only while Chrome is open.")]
    [InlineData("state-signed-out", "Sign in to Canvas in Chrome", "Syncing waits until you do.")]
    [InlineData("state-syncing", "Syncing… 3 left", "BIO 110, CALC II and HIST 210.")]
    [InlineData("state-connected", "Connected", "Last sync 10:24. Next at 11:24.")]
    [InlineData("state-updated", "The Chrome extension updated itself", "Now version 1.4. Nothing to do.")]
    [InlineData("state-error", "Canvas didn’t answer", "school.instructure.com didn’t respond at 10:24. Study Stash will try again at 11:24.")]
    public void Describe_matches_the_design_s_words_for_every_state(string fixture, string title, string text)
    {
        var s = CanvasFixtures.Load<CanvasApi.State>(fixture);
        var copy = CanvasWords.Describe(s, Zone);
        Assert.Equal(title, copy.Title);
        Assert.Equal(text, copy.Text);
    }
}
