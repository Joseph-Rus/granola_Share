using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StudyStash.App.Services;

namespace StudyStash.App.ViewModels;

public enum SetupStep
{
    Microphone,
    Library,
    Model,
    Classes,
    /// <summary>Windows only: pin the tray icon out of the overflow.</summary>
    Taskbar,
}

/// <summary>A step in setup's sidebar: done (a check), the one you're on, or still to come.</summary>
public sealed partial class StepItem : ObservableObject
{
    public SetupStep Step { get; init; }
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public bool Optional { get; init; }
    [ObservableProperty] public partial bool Done { get; set; }
    [ObservableProperty] public partial bool Current { get; set; }

    public bool Todo => !Done && !Current;
    public bool ShowNumber => !Done;

    partial void OnDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(Todo));
        OnPropertyChanged(nameof(ShowNumber));
    }

    partial void OnCurrentChanged(bool value) => OnPropertyChanged(nameof(Todo));
}

/// <summary>A class you're adding in setup, and when it meets: "CS 101, Tue Thu 10:00–11:15".</summary>
public sealed partial class SetupClass : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string When { get; set; } = "";
    public IBrush Dot { get; init; } = Brushes.Gray;
}

/// <summary>
/// First-run setup: the microphone, the library (this computer, or another), the transcription model (a download of a
/// few gigabytes), your classes (optional), and on Windows, keeping the icon on the taskbar.
/// </summary>
public sealed partial class SetupModel : ObservableObject
{
    public ObservableCollection<StepItem> Steps { get; } = [];
    [ObservableProperty] public partial SetupStep Step { get; set; }
    /// <summary>What this computer is for: set by <see cref="SetRole"/>, which also rebuilds <see cref="Steps"/>.</summary>
    [ObservableProperty] public partial AppRole Role { get; set; } = AppRole.Laptop;

    // Microphone
    [ObservableProperty] public partial bool MicAllowed { get; set; }
    [ObservableProperty] public partial bool MicDenied { get; set; }
    /// <summary>The mic check's bars, oldest first, 0 to 1 (24 of them).</summary>
    [ObservableProperty] public partial IReadOnlyList<double>? MicLevels { get; set; }
    /// <summary>A level has passed the "hears you" mark since the mic check opened.</summary>
    [ObservableProperty] public partial bool MicHeard { get; set; }
    public string MicLine => MicHeard ? "Study Stash hears you." : "Say something. The bars move when Study Stash hears you.";
    public string SkipRecordingText => $"This {DeviceWord} won't record: it's only the library";

    // Library
    [ObservableProperty] public partial bool ThisComputer { get; set; }
    [ObservableProperty] public partial bool OtherComputer { get; set; } = true;
    /// <summary>This computer is only the library: it never records. Implies <see cref="ThisComputer"/>.</summary>
    [ObservableProperty] public partial bool OnlyLibrary { get; set; }
    [ObservableProperty] public partial string Address { get; set; } = "";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string LibraryName { get; set; } = "";
    [ObservableProperty] public partial string? LibraryResult { get; set; }
    [ObservableProperty] public partial bool LibraryOk { get; set; }
    [ObservableProperty] public partial bool Connecting { get; set; }
    /// <summary>Looking for a library on this computer or your Tailscale network ("Find it").</summary>
    [ObservableProperty] public partial bool Finding { get; set; }
    /// <summary>Start Study Stash when the student logs in: off unless they tick it.</summary>
    [ObservableProperty] public partial bool StartAtLogin { get; set; }
    public bool ShowStartAtLogin => ThisComputer || OnlyLibrary;

    // Model
    [ObservableProperty] public partial string ModelName { get; set; } = "Whisper large-v3";
    [ObservableProperty] public partial string ModelSize { get; set; } = "3 GB";
    [ObservableProperty] public partial double ModelProgress { get; set; }
    [ObservableProperty] public partial string ModelDone { get; set; } = "";
    [ObservableProperty] public partial string ModelLeft { get; set; } = "";
    [ObservableProperty] public partial bool ModelReady { get; set; }
    [ObservableProperty] public partial string? ModelProblem { get; set; }

    // Classes
    public ObservableCollection<SetupClass> Classes { get; } = [];
    [ObservableProperty] public partial string NewClass { get; set; } = "";
    [ObservableProperty] public partial string NewWhen { get; set; } = "";
    /// <summary>The times typed couldn't be read: how to write them.</summary>
    [ObservableProperty] public partial string? ClassProblem { get; set; }
    public bool HasClassProblem => !string.IsNullOrEmpty(ClassProblem);
    partial void OnClassProblemChanged(string? value) => OnPropertyChanged(nameof(HasClassProblem));

    public string DeviceWord => Skin.Current == SkinKind.Mac ? "Mac" : "PC";
    public int Count => Steps.Count;
    public int Index => Steps.ToList().FindIndex(s => s.Step == Step) + 1;
    public bool IsLast => Index == Count;
    public string StepLabel => IsLast && Skin.Current == SkinKind.Win ? "Last step" : $"Step {Index} of {Count}";
    public string ContinueLabel => IsLast ? "Finish" : "Continue";
    public bool CanGoBack => Index > 1;

    public bool OnMicrophone => Step == SetupStep.Microphone;
    public bool OnLibrary => Step == SetupStep.Library;
    public bool OnModel => Step == SetupStep.Model;
    public bool OnClasses => Step == SetupStep.Classes;
    public bool OnTaskbar => Step == SetupStep.Taskbar;
    public bool HasLibraryResult => !string.IsNullOrEmpty(LibraryResult);
    public bool HasModelProblem => !string.IsNullOrEmpty(ModelProblem);
    public bool ModelDownloading => !ModelReady && !HasModelProblem;
    public double ModelBarWidth => Math.Clamp(ModelProgress, 0, 1) * 440;
    public double ModelBarWidthWin => Math.Clamp(ModelProgress, 0, 1) * 440;
    public string ModelBody => $"Study Stash turns speech into text on this {DeviceWord}, so your recordings never leave it. The model is about {ModelSize} and only downloads once.";

    public static SetupModel For(SkinKind skin)
    {
        var m = new SetupModel();
        m.SetRole(AppRole.Laptop, skin);
        return m;
    }

    /// <summary>What this computer is for: rebuilds <see cref="Steps"/> (Laptop and Both keep the microphone and the
    /// model; Library skips both) and stays on the current step when it's still one of them, else goes to the first.</summary>
    public void SetRole(AppRole role, SkinKind? skin = null)
    {
        Role = role;
        var sk = skin ?? Skin.Current;
        var wanted = Step;
        Steps.Clear();
        int n = 1;
        if (role != AppRole.Library) Steps.Add(new StepItem { Step = SetupStep.Microphone, Number = n++, Title = "Microphone" });
        Steps.Add(new StepItem { Step = SetupStep.Library, Number = n++, Title = "Library" });
        if (role != AppRole.Library) Steps.Add(new StepItem { Step = SetupStep.Model, Number = n++, Title = "Transcription model" });
        Steps.Add(new StepItem { Step = SetupStep.Classes, Number = n++, Title = "Classes", Optional = true });
        if (sk == SkinKind.Win) Steps.Add(new StepItem { Step = SetupStep.Taskbar, Number = n++, Title = "Taskbar" });
        Go(Steps.Any(s => s.Step == wanted) ? wanted : Steps[0].Step);
        NotifyStepDerived();
    }

    /// <summary>Steps before this one are done (a check); this one and the ones after are still to come, even if
    /// they were done before: going Back undoes their checks too.</summary>
    public void Go(SetupStep step)
    {
        Step = step;
        int at = Steps.ToList().FindIndex(s => s.Step == step);
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].Current = i == at;
            Steps[i].Done = at >= 0 && i < at;
        }
    }

    void NotifyStepDerived()
    {
        foreach (string p in new[] { nameof(Index), nameof(IsLast), nameof(StepLabel), nameof(ContinueLabel), nameof(CanGoBack), nameof(OnMicrophone),
                     nameof(OnLibrary), nameof(OnModel), nameof(OnClasses), nameof(OnTaskbar) })
            OnPropertyChanged(p);
    }

    partial void OnStepChanged(SetupStep value) => NotifyStepDerived();

    partial void OnThisComputerChanged(bool value)
    {
        if (value) OtherComputer = false;
        OnPropertyChanged(nameof(ShowStartAtLogin));
    }

    partial void OnOtherComputerChanged(bool value)
    {
        if (value)
        {
            ThisComputer = false;
            OnlyLibrary = false;
        }
    }

    partial void OnOnlyLibraryChanged(bool value) => OnPropertyChanged(nameof(ShowStartAtLogin));

    partial void OnMicHeardChanged(bool value) => OnPropertyChanged(nameof(MicLine));

    partial void OnLibraryResultChanged(string? value) => OnPropertyChanged(nameof(HasLibraryResult));

    partial void OnModelProgressChanged(double value)
    {
        OnPropertyChanged(nameof(ModelBarWidth));
        OnPropertyChanged(nameof(ModelBarWidthWin));
    }

    partial void OnModelReadyChanged(bool value) => OnPropertyChanged(nameof(ModelDownloading));

    partial void OnModelProblemChanged(string? value)
    {
        OnPropertyChanged(nameof(HasModelProblem));
        OnPropertyChanged(nameof(ModelDownloading));
    }

    partial void OnModelSizeChanged(string value) => OnPropertyChanged(nameof(ModelBody));

    public Action? OnAllowMic { get; set; }
    public Action? OnMicSettings { get; set; }
    public Func<Task>? OnConnect { get; set; }
    public Func<Task>? OnFind { get; set; }
    public Action? OnRetryModel { get; set; }
    public Func<Task>? OnAddClass { get; set; }
    public Action? OnTaskbarSettings { get; set; }
    public Func<SetupStep, bool>? CanLeave { get; set; }
    public Action? OnFinish { get; set; }

    [RelayCommand] void AllowMic() => OnAllowMic?.Invoke();
    [RelayCommand] void MicSettings() => OnMicSettings?.Invoke();
    [RelayCommand] void RetryModel() => OnRetryModel?.Invoke();
    [RelayCommand] void TaskbarSettings() => OnTaskbarSettings?.Invoke();

    [RelayCommand]
    void PickThis()
    {
        OtherComputer = false;
        OnlyLibrary = false;
        ThisComputer = true;
        SetRole(AppRole.Both);
    }

    [RelayCommand]
    void PickOther()
    {
        ThisComputer = false;
        OnlyLibrary = false;
        OtherComputer = true;
        SetRole(AppRole.Laptop);
    }

    /// <summary>"This {Mac|PC} won't record: it's only the library" on the microphone step: skips straight to
    /// setting this computer up as the library, with no microphone or model to come.</summary>
    [RelayCommand]
    void PickOnlyLibrary()
    {
        OtherComputer = false;
        ThisComputer = true;
        OnlyLibrary = true;
        SetRole(AppRole.Library);
    }

    [RelayCommand]
    async Task Connect()
    {
        if (OnConnect is not null) await OnConnect();
    }

    [RelayCommand]
    async Task Find()
    {
        if (OnFind is not null) await OnFind();
    }

    [RelayCommand]
    async Task AddClass()
    {
        if (OnAddClass is not null) await OnAddClass();
    }

    [RelayCommand]
    void Back()
    {
        int i = Index - 2;
        if (i >= 0) Go(Steps[i].Step);
    }

    [RelayCommand]
    void Next()
    {
        if (CanLeave?.Invoke(Step) == false) return;
        if (IsLast)
        {
            OnFinish?.Invoke();
            return;
        }
        Go(Steps[Index].Step);
    }
}
