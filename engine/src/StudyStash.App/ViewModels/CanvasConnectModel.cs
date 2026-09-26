using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StudyStash.App.Services;

namespace StudyStash.App.ViewModels;

/// <summary>Where one of connect flow's five steps stands.</summary>
public enum StepState { Done, Current, ToDo }

/// <summary>One step of connecting Canvas (design 07): its number and title, and, once past it, a short summary
/// ("school.instructure.com").</summary>
public sealed partial class ConnectStep : ObservableObject
{
    public required int Number { get; init; }
    public required string Title { get; init; }

    [NotifyPropertyChangedFor(nameof(IsDone), nameof(IsCurrent), nameof(IsToDo))]
    [ObservableProperty]
    public partial StepState State { get; set; } = StepState.ToDo;

    [ObservableProperty] public partial string Summary { get; set; } = "";

    public bool IsDone => State == StepState.Done;
    public bool IsCurrent => State == StepState.Current;
    public bool IsToDo => State == StepState.ToDo;
}

/// <summary>
/// Connecting Canvas (design 07): the school address, the Chrome extension, signing in, matching each class to a
/// course, and the first sync — one step open at a time, the rest Done or To do. Shares its <see cref="CanvasWatch"/>
/// with the status card and Settings, so it moves along the moment Chrome or Canvas answers back rather than polling
/// on its own. Hosted either inside first-run setup (<see cref="ShowFooter"/> false, the wizard's own Next/Finish)
/// or, from the status card's Connect/Show me how, in a small window of its own (<see cref="ShowFooter"/> true).
/// </summary>
public sealed partial class CanvasConnectModel : ObservableObject
{
    readonly CanvasContext context;
    readonly CanvasWatch watch;
    CanvasApi.State state = new();
    CanvasApi.Overview? found;
    string school = "";

    public CanvasConnectModel(CanvasContext context, CanvasWatch watch)
    {
        this.context = context;
        this.watch = watch;
        Steps =
        [
            new ConnectStep { Number = 1, Title = "School address" },
            new ConnectStep { Number = 2, Title = "Set up the Chrome extension" },
            new ConnectStep { Number = 3, Title = "Find my courses" },
            new ConnectStep { Number = 4, Title = "Match each class to a course" },
            new ConnectStep { Number = 5, Title = "Sync now" },
        ];
        watch.Changed += OnWatchChanged;
    }

    public IReadOnlyList<ConnectStep> Steps { get; }
    // Named steps for the views: simpler for compiled bindings than an indexer, and the same five objects always.
    public ConnectStep Step1 => Steps[0];
    public ConnectStep Step2 => Steps[1];
    public ConnectStep Step3 => Steps[2];
    public ConnectStep Step4 => Steps[3];
    public ConnectStep Step5 => Steps[4];

    [ObservableProperty] public partial int Current { get; set; } = 1;

    public const string Title = "Bring in Canvas";
    public const string Intro =
        "Assignments, due dates and course files, filed next to your lectures. Study Stash only reads from Canvas. Nothing there changes.";

    // ---- step 1: school address ----
    [ObservableProperty] public partial string SchoolField { get; set; } = "";
    [ObservableProperty] public partial string? SchoolError { get; set; }
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    [ObservableProperty]
    public partial bool SavingSchool { get; set; }

    // ---- step 2: the Chrome extension ----
    [ObservableProperty] public partial string? ExtensionFolder { get; set; }
    [ObservableProperty] public partial bool WaitingForExtension { get; set; }

    // ---- step 3: find my courses ----
    [ObservableProperty] public partial bool SignedOut { get; set; }
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    [ObservableProperty]
    public partial bool FindingCourses { get; set; }
    [ObservableProperty] public partial string? CoursesSay { get; set; }

    // ---- step 4: match each class to a course ----
    public ObservableCollection<CanvasCourseRow> Courses { get; } = [];
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    [ObservableProperty]
    public partial bool Linking { get; set; }
    [ObservableProperty] public partial string? LinkError { get; set; }

    // ---- step 5: sync now ----
    [NotifyPropertyChangedFor(nameof(CanFinish), nameof(HasProgress))]
    [ObservableProperty]
    public partial bool Syncing { get; set; }
    [ObservableProperty] public partial double? SyncProgress { get; set; }
    [ObservableProperty] public partial string? SyncedLine { get; set; }
    public bool HasProgress => Syncing;

    // ---- what the host (setup, or the small Connect window) controls ----
    [ObservableProperty] public partial string StepLabel { get; set; } = "";
    [ObservableProperty] public partial bool ShowFooter { get; set; }
    [ObservableProperty] public partial string FinishLabel { get; set; } = "Finish";
    public bool CanFinish => !SavingSchool && !FindingCourses && !Linking && !Syncing;
    public Action? OnSkip { get; set; }
    public Action? OnBack { get; set; }
    public Action? OnFinish { get; set; }

    [RelayCommand] void Skip() => OnSkip?.Invoke();
    [RelayCommand] void Back() => OnBack?.Invoke();
    [RelayCommand] void Finish() => OnFinish?.Invoke();

    /// <summary>Opens the flow at whichever step the library's state still needs: not connected at all → the
    /// school address; the extension missing → set it up; signed out, or no class linked yet → find courses;
    /// otherwise the whole thing already works, so → sync now.</summary>
    public Task StartAsync(CanvasApi.State s, IReadOnlyList<CanvasApi.ClassRow> classes, CancellationToken stop = default)
    {
        state = s;
        school = s.School.Length > 0 ? s.School : s.Url;
        SchoolField = school;
        return GoToAsync(DecideStep(s, classes), classes, stop);
    }

    static int DecideStep(CanvasApi.State s, IReadOnlyList<CanvasApi.ClassRow> classes) => s.Status switch
    {
        "not_set_up" => 1,
        "no_extension" => 2,
        "signed_out" => 3,
        _ => classes.Count > 0 && classes.All(c => !c.Linked) ? 3 : 5,
    };

    async Task GoToAsync(int step, IReadOnlyList<CanvasApi.ClassRow>? classes, CancellationToken stop)
    {
        bool entering = step != Current;
        for (int i = 0; i < Steps.Count; i++) Steps[i].State = i + 1 < step ? StepState.Done : i + 1 == step ? StepState.Current : StepState.ToDo;
        if (step > 1) Steps[0].Summary = school;
        Current = step;
        if (step == 5 && state.LastSync is { } synced) SyncedLine = $"Canvas synced {CanvasWords.Clock(synced, context.Clock.Zone)}.";
        if (!entering) return;
        switch (step)
        {
            case 2: await EnterExtensionStepAsync(stop); break;
            case 3: await EnterCoursesStepAsync(stop); break;
            case 4: EnterMatchStep(classes ?? []); break;
        }
    }

    /// <summary>Re-reads the library and moves on if it now needs a later step than the one showing (a step never
    /// moves backward on its own — "Change" is the only way back).</summary>
    async Task RefreshAndAdvanceAsync(CancellationToken stop = default)
    {
        if (context.Client is not { } client) return;
        if (await client.StateAsync(stop) is not { } s) return;
        state = s;
        school = s.School.Length > 0 ? s.School : s.Url;
        var classes = await client.ClassesAsync(stop) ?? [];
        int step = Math.Max(DecideStep(s, classes), Current);
        await GoToAsync(step, classes, stop);
    }

    async Task EnterExtensionStepAsync(CancellationToken stop)
    {
        WaitingForExtension = true;
        if (context.Client is not { } client) return;
        if (await client.ExtensionAsync(stop) is { } key)
        {
            ExtensionFolder = context.Actions.PrepareExtension(key.Key, key.Canvas);
            context.Actions.OpenChromeExtensions();
        }
    }

    async Task EnterCoursesStepAsync(CancellationToken stop)
    {
        SignedOut = state.Status == "signed_out";
        CoursesSay = null;
        if (!SignedOut) await FindCoursesInternalAsync(stop);
    }

    void EnterMatchStep(IReadOnlyList<CanvasApi.ClassRow> classes)
    {
        var choices = (found?.Available ?? new Dictionary<string, string>())
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new CourseChoice(kv.Key, kv.Value)).ToList();
        Courses.Clear();
        foreach (var c in classes)
        {
            var row = new CanvasCourseRow { Class = c.Class, Dot = context.DotOf(c.Class) };
            row.Choices.Add(new CourseChoice(null, "Choose a course…"));
            foreach (var choice in choices) row.Choices.Add(choice);
            string? preselect = c.Linked ? c.Canvas?.Id : c.Suggested;
            row.Selected = preselect is not null ? row.Choices.FirstOrDefault(x => x.Id == preselect) ?? row.Choices[0] : row.Choices[0];
            Courses.Add(row);
        }
    }

    void OnWatchChanged()
    {
        if (watch.State is not { } s) return;
        state = s;
        if (Current == 2 && s.Status != "no_extension")
        {
            _ = RefreshAndAdvanceAsync();
        }
        else if (Current == 3 && SignedOut && s.Status != "signed_out")
        {
            SignedOut = false;
            _ = FindCoursesInternalAsync();
        }
        else if (Current == 5)
        {
            Syncing = s.Status == "syncing";
            SyncProgress = s.Syncing is { Total: > 0 } sy ? (double)(sy.Total - sy.Left) / sy.Total : null;
            if (!Syncing && s.LastSync is { } t) SyncedLine = $"Canvas synced {CanvasWords.Clock(t, context.Clock.Zone)}.";
        }
    }

    [RelayCommand]
    async Task Continue()
    {
        if (context.Client is not { } client) return;
        SavingSchool = true;
        SchoolError = null;
        try
        {
            await client.SaveAsync(url: SchoolField);
            await RefreshAndAdvanceAsync();
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

    /// <summary>The design's "Change" link: back to editing the school address, whatever step is showing now.</summary>
    [RelayCommand]
    void ChangeSchool()
    {
        SchoolField = school;
        SchoolError = null;
        for (int i = 1; i < Steps.Count; i++) Steps[i].State = StepState.ToDo;
        Steps[0].State = StepState.Current;
        Current = 1;
    }

    [RelayCommand]
    void ShowFolder()
    {
        if (ExtensionFolder is { } f) context.Actions.RevealFolder(f);
    }

    [RelayCommand]
    void OpenChromeExtensions() => context.Actions.OpenChromeExtensions();

    [RelayCommand]
    void OpenCanvas() => context.Actions.OpenInChrome(state.Url);

    [RelayCommand]
    Task FindCourses() => FindCoursesInternalAsync();

    async Task FindCoursesInternalAsync(CancellationToken stop = default)
    {
        if (context.Client is not { } client) return;
        FindingCourses = true;
        CoursesSay = null;
        try
        {
            var answer = await client.FindCoursesAsync(stop);
            if (answer is null)
            {
                CoursesSay = "Your library didn't answer.";
                return;
            }
            found = answer;
            if (answer.Error.Length > 0)
            {
                CoursesSay = answer.Error;
                return;
            }
            CoursesSay = $"Found {answer.Available.Count} courses.";
            var classes = await client.ClassesAsync(stop) ?? [];
            await GoToAsync(4, classes, stop);
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

    [RelayCommand]
    async Task LinkThese()
    {
        if (context.Client is not { } client) return;
        Linking = true;
        LinkError = null;
        try
        {
            var body = new Dictionary<string, double>();
            foreach (var row in Courses)
                body[row.Class] = row.Selected?.Id is { } id ? double.Parse(id, CultureInfo.InvariantCulture) : 0;
            await client.SaveAsync(courses: body);
            await GoToAsync(5, null, default);
        }
        catch (CanvasLibraryException e)
        {
            LinkError = e.Message;
        }
        finally
        {
            Linking = false;
        }
    }

    [RelayCommand]
    async Task SyncNow()
    {
        if (context.Client is not { } client) return;
        Syncing = true;
        SyncedLine = null;
        await client.SaveAsync(sync: true);
    }
}
