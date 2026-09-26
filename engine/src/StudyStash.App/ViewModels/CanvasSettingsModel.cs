using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StudyStash.App.Services;

namespace StudyStash.App.ViewModels;

/// <summary>One course Settings → Canvas can offer a class, or the "Choose a course…" placeholder (<see cref="Id"/>
/// null) an unmatched class starts on.</summary>
public sealed record CourseChoice(string? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One "Last sync" row: an icon by the change's kind and its already-worded text ("New: CS 101 · Lab 3 ·
/// due Tue 11:59 PM").</summary>
public sealed record CanvasChangeRow(string Glyph, string Text);

/// <summary>One class in Settings' Courses list: its dot, Scout's line, and the course picker. A class Canvas hasn't
/// matched shows <see cref="CanvasWords.NotMatched"/> and a "Choose a course…" placeholder instead of a scout state.</summary>
public sealed partial class CanvasCourseRow : ObservableObject
{
    public required string Class { get; init; }
    public required IBrush Dot { get; init; }

    [ObservableProperty] public partial string ScoutLine { get; set; } = "";
    [ObservableProperty] public partial string ScoutGlyph { get; set; } = "";
    [ObservableProperty] public partial bool ScoutOk { get; set; }
    [ObservableProperty] public partial bool ScoutWarn { get; set; }
    [ObservableProperty] public partial bool ScoutSpinning { get; set; }
    [ObservableProperty] public partial bool ShowTryAgain { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }

    public ObservableCollection<CourseChoice> Choices { get; } = [];
    [ObservableProperty] public partial CourseChoice? Selected { get; set; }

    /// <summary>Set by <see cref="CanvasSettingsModel"/> when it builds the row, so picking a course or retrying
    /// Scout talks back to the model that owns the library call.</summary>
    public Action<CanvasCourseRow>? OnTryAgain { get; set; }
    public Action<CanvasCourseRow, CourseChoice>? OnPick { get; set; }

    [RelayCommand]
    void TryAgain() => OnTryAgain?.Invoke(this);

    [RelayCommand]
    void Pick(CourseChoice choice)
    {
        if (choice == Selected) return;
        Selected = choice;
        OnPick?.Invoke(this, choice);
    }
}

/// <summary>
/// Settings → Canvas (design 06): the connection (school, extension, sync interval), each class's course, and the
/// last sync's changes. <see cref="LoadAsync"/> reads the library's state, overview and classes together and shapes
/// every row from them; every button here ends in one save call and a fresh <see cref="LoadAsync"/>, so the screen
/// always shows what the library actually has, not what the click optimistically assumed.
/// </summary>
public sealed partial class CanvasSettingsModel : ObservableObject
{
    readonly CanvasContext context;
    readonly CanvasWatch? watch;

    public CanvasSettingsModel(CanvasContext context, CanvasWatch? watch = null)
    {
        this.context = context;
        this.watch = watch;
        Status = new CanvasStatusModel(context);
        if (watch is not null) watch.Changed += OnWatchChanged;
    }

    /// <summary>The header's status: a compact line when connected or syncing, the full card otherwise.</summary>
    public CanvasStatusModel Status { get; }

    [ObservableProperty] public partial bool Busy { get; set; }
    /// <summary>"Your library didn't answer." or "Your library runs an older Study Stash: update it to use
    /// Canvas." — set instead of loading the rows below.</summary>
    [ObservableProperty] public partial string? Say { get; set; }

    [ObservableProperty] public partial string School { get; set; } = "";
    [ObservableProperty] public partial bool EditingSchool { get; set; }
    [ObservableProperty] public partial string SchoolField { get; set; } = "";
    [ObservableProperty] public partial string? SchoolError { get; set; }
    [ObservableProperty] public partial bool SavingSchool { get; set; }

    [ObservableProperty] public partial string ExtensionLine { get; set; } = "";

    public static readonly IReadOnlyList<int> PollChoices = [15, 30, 60, 180, 1440];
    [NotifyPropertyChangedFor(nameof(PollLabel))]
    [ObservableProperty]
    public partial int PollMinutes { get; set; } = 60;
    public string PollLabel => CanvasWords.PollIntervalText(PollMinutes);

    public ObservableCollection<CanvasCourseRow> Courses { get; } = [];
    [ObservableProperty] public partial bool FindingCourses { get; set; }
    [ObservableProperty] public partial string? CoursesSay { get; set; }

    public ObservableCollection<CanvasChangeRow> Changes { get; } = [];
    [ObservableProperty] public partial string LastSyncHeader { get; set; } = "";
    public bool HasChanges => Changes.Count > 0;

    void OnWatchChanged()
    {
        if (watch?.State is { } s) Status.Show(s);
    }

    public async Task LoadAsync(CancellationToken stop = default)
    {
        if (Busy) return;
        if (context.Client is not { } client)
        {
            Say = "Your library didn't answer.";
            return;
        }
        Busy = true;
        try
        {
            var stateTask = client.StateAsync(stop);
            var overviewTask = client.OverviewAsync(stop);
            var classesTask = client.ClassesAsync(stop);
            await Task.WhenAll(stateTask, overviewTask, classesTask);
            if (await stateTask is not { } state || await overviewTask is not { } overview)
            {
                Say = "Your library runs an older Study Stash: update it to use Canvas.";
                return;
            }
            Say = null;
            Apply(state, overview, await classesTask ?? []);
        }
        catch (Exception e) when (e is CanvasLibraryException or HttpRequestException or TaskCanceledException)
        {
            Say = "Your library didn't answer.";
        }
        finally
        {
            Busy = false;
        }
    }

    void Apply(CanvasApi.State state, CanvasApi.Overview overview, IReadOnlyList<CanvasApi.ClassRow> classes)
    {
        var zone = context.Clock.Zone;
        var now = context.Clock.Now();
        Status.Show(state);
        School = state.School.Length > 0 ? state.School : overview.Url;
        ExtensionLine = state.Extension is { Seen: { } seen } ext ? CanvasWords.ExtensionCheckedInLine(seen, ext.Version, zone, now) : "Not set up yet";
        PollMinutes = state.PollMinutes > 0 ? state.PollMinutes : overview.PollMinutes;

        var choices = overview.Available.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new CourseChoice(kv.Key, kv.Value)).ToList();
        Courses.Clear();
        foreach (var c in classes)
        {
            var row = new CanvasCourseRow { Class = c.Class, Dot = context.DotOf(c.Class) };
            if (!c.Linked)
            {
                row.Choices.Add(new CourseChoice(null, "Choose a course…"));
                row.ScoutGlyph = "remove";
                row.ScoutLine = CanvasWords.NotMatched;
            }
            else if (c.Scout is { } scout)
            {
                row.ScoutLine = CanvasWords.ScoutSettingsLine(scout, zone);
                (row.ScoutGlyph, row.ScoutOk, row.ScoutWarn, row.ScoutSpinning, row.ShowTryAgain) = scout.State switch
                {
                    "done" => ("check_circle", true, false, false, false),
                    "exploring" => ("", false, false, true, false),
                    "failed" => ("error", false, true, false, true),
                    _ => ("", false, false, false, false),
                };
            }
            foreach (var choice in choices) row.Choices.Add(choice);
            row.Selected = c.Linked && c.Canvas is { } canvas ? row.Choices.FirstOrDefault(x => x.Id == canvas.Id) : row.Choices[0];
            row.OnTryAgain = r => _ = ScoutAsync(r);
            row.OnPick = (r, choice) => _ = PickCourseAsync(r, choice);
            Courses.Add(row);
        }

        Changes.Clear();
        foreach (var change in overview.LastChanges) Changes.Add(new CanvasChangeRow(ChangeGlyph(change.Kind), $"{ChangeLabel(change.Kind)}: {change.Text}"));
        OnPropertyChanged(nameof(HasChanges));
        LastSyncHeader = CanvasWords.LastSyncLine(state.LastSync, zone);
    }

    static string ChangeLabel(string kind) => kind switch
    {
        "new" => "New",
        "moved" => "Moved",
        "graded" => "Graded",
        "feedback" => "Feedback",
        "missing" => "Missing",
        "removed" => "Removed",
        "announcement" => "Announcement",
        _ => kind.Length == 0 ? "" : char.ToUpperInvariant(kind[0]) + kind[1..],
    };

    static string ChangeGlyph(string kind) => kind switch
    {
        "new" => "add_circle",
        "moved" => "event",
        "graded" => "grade",
        "feedback" => "chat",
        "missing" => "error",
        "removed" => "remove",
        "announcement" => "campaign",
        _ => "event",
    };

    [RelayCommand]
    void ChangeSchool()
    {
        SchoolField = School;
        SchoolError = null;
        EditingSchool = true;
    }

    [RelayCommand]
    void CancelSchool() => EditingSchool = false;

    [RelayCommand]
    async Task SaveSchool()
    {
        if (context.Client is not { } client) return;
        SavingSchool = true;
        SchoolError = null;
        try
        {
            await client.SaveAsync(url: SchoolField);
            EditingSchool = false;
            await LoadAsync();
        }
        catch (CanvasLibraryException e)
        {
            SchoolError = e.Message;
        }
        finally
        {
            SavingSchool = false;
        }
    }

    [RelayCommand]
    async Task PickPoll(int minutes)
    {
        if (context.Client is not { } client) return;
        await client.SaveAsync(pollMinutes: minutes);
        await LoadAsync();
    }

    [RelayCommand]
    async Task FindCourses()
    {
        if (context.Client is not { } client) return;
        FindingCourses = true;
        CoursesSay = null;
        try
        {
            var found = await client.FindCoursesAsync();
            CoursesSay = found is null ? null : found.Error.Length > 0 ? found.Error : $"Found {found.Available.Count} courses.";
            await LoadAsync();
        }
        catch (CanvasLibraryException e)
        {
            CoursesSay = e.Message;
        }
        finally
        {
            FindingCourses = false;
        }
    }

    async Task ScoutAsync(CanvasCourseRow row)
    {
        if (context.Client is not { } client) return;
        row.Busy = true;
        try
        {
            await client.ScoutAsync(row.Class);
            await LoadAsync();
        }
        finally
        {
            row.Busy = false;
        }
    }

    async Task PickCourseAsync(CanvasCourseRow row, CourseChoice choice)
    {
        if (context.Client is not { } client || choice.Id is null) return;
        row.Busy = true;
        try
        {
            await client.SaveAsync(courses: new Dictionary<string, double> { [row.Class] = double.Parse(choice.Id, CultureInfo.InvariantCulture) });
            await LoadAsync();
        }
        finally
        {
            row.Busy = false;
        }
    }
}
