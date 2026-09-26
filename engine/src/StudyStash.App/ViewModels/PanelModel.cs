using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StudyStash.Core;

namespace StudyStash.App.ViewModels;

/// <summary>A lecture in a list: its class dot, title, where it is ("Transcribing 42%", "Filed in CS 101"), and when.</summary>
public sealed partial class LectureItem : ObservableObject
{
    public string Id { get; init; } = "";
    /// <summary>Where it is right now, so a click on it does the right thing: opens it, retries it, or just says
    /// where it is.</summary>
    public LectureState State { get; init; }
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Detail { get; set; } = "";
    [ObservableProperty] public partial string Time { get; set; } = "";
    [ObservableProperty] public partial IBrush Dot { get; set; } = Brushes.Gray;
    /// <summary>0 to 1 while Whisper works through it; null hides the bar.</summary>
    [ObservableProperty] public partial double? Progress { get; set; }
    /// <summary>The library is writing its notes (a spinner on Windows).</summary>
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial bool Selected { get; set; }
    [ObservableProperty] public partial bool Problem { get; set; }

    public bool HasProgress => Progress is not null;
    public double ProgressWidth => (Progress ?? 0) * 140;
    public double ProgressWidthWin => (Progress ?? 0) * 160;

    partial void OnProgressChanged(double? value)
    {
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressWidth));
        OnPropertyChanged(nameof(ProgressWidthWin));
    }
}

/// <summary>
/// The menu bar dropdown (Mac) or tray flyout (Windows): Record for the class on now, or the lecture recording
/// right now; the latest lectures and where each is; search; and whether the library and the model are ready.
/// </summary>
public sealed partial class PanelModel : ObservableObject
{
    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial bool IsPaused { get; set; }
    /// <summary>The class Record will file under ("" lets the library sort it).</summary>
    [ObservableProperty] public partial string ClassName { get; set; } = "";
    [ObservableProperty] public partial IBrush ClassDot { get; set; } = Brushes.Gray;
    /// <summary>"From your timetable · Tue 10:00–11:15", or null.</summary>
    [ObservableProperty] public partial string? Hint { get; set; }
    [ObservableProperty] public partial string Elapsed { get; set; } = "00:00";
    [ObservableProperty] public partial IReadOnlyList<double>? Levels { get; set; }
    /// <summary>The last thing Whisper heard, in quotes.</summary>
    [ObservableProperty] public partial string LastLine { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool StatusGood { get; set; } = true;
    /// <summary>Record can't start yet (no model, no microphone): why, in place of the timetable hint.</summary>
    [ObservableProperty] public partial bool CanRecord { get; set; } = true;

    /// <summary>What's wrong right now (from <see cref="Services.Problems"/>), for the dropdown's own words and a
    /// button to fix it. Null while all is well.</summary>
    [ObservableProperty] public partial string? ProblemTitle { get; set; }
    [ObservableProperty] public partial string? ProblemDetail { get; set; }
    [ObservableProperty] public partial string? ProblemAction { get; set; }
    public bool HasProblem => ProblemTitle is not null;

    public ObservableCollection<LectureItem> Recent { get; } = [];

    public string RecordLabel => ClassName.Length > 0 ? $"Record · {ClassName}" : "Record";
    public string RecordingLabel => (IsPaused ? "Paused · " : "Recording · ") + (ClassName.Length > 0 ? ClassName : "Lecture");
    public bool HasHint => !string.IsNullOrEmpty(Hint);
    public bool HasLastLine => LastLine.Length > 0;
    public bool HasRecent => Recent.Count > 0;
    public string PauseLabel => IsPaused ? "Resume" : "Pause";
    public string PauseGlyph => IsPaused ? "play_arrow" : "pause";
    public bool IsIdle => !IsRecording;

    public string RecordShortcut => Skin.Current == SkinKind.Mac ? "⌥⇧R" : "Ctrl+Alt+R";
    public string SearchShortcut => Skin.Current == SkinKind.Mac ? "⌥Space" : "Alt+Shift+Space";

    public PanelModel() => Recent.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecent));

    partial void OnClassNameChanged(string value)
    {
        OnPropertyChanged(nameof(RecordLabel));
        OnPropertyChanged(nameof(RecordingLabel));
    }

    partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(RecordingLabel));
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(PauseGlyph));
    }

    partial void OnHintChanged(string? value) => OnPropertyChanged(nameof(HasHint));
    partial void OnLastLineChanged(string value) => OnPropertyChanged(nameof(HasLastLine));
    partial void OnProblemTitleChanged(string? value) => OnPropertyChanged(nameof(HasProblem));

    // What the buttons do: the shell fills these in.
    public Action? OnRecord { get; set; }
    public Action? OnSwitchClass { get; set; }
    public Action? OnPause { get; set; }
    public Action? OnStop { get; set; }
    public Action? OnShowRecorder { get; set; }
    public Action? OnSearch { get; set; }
    public Action? OnOpenApp { get; set; }
    public Action<LectureItem>? OnOpenLecture { get; set; }
    public Action? OnFixProblem { get; set; }

    [RelayCommand] void Record() => OnRecord?.Invoke();
    [RelayCommand] void FixProblem() => OnFixProblem?.Invoke();
    [RelayCommand] void SwitchClass() => OnSwitchClass?.Invoke();
    [RelayCommand] void Pause() => OnPause?.Invoke();
    [RelayCommand] void Stop() => OnStop?.Invoke();
    [RelayCommand] void ShowRecorder() => OnShowRecorder?.Invoke();
    [RelayCommand] void Search() => OnSearch?.Invoke();
    [RelayCommand] void OpenApp() => OnOpenApp?.Invoke();
    [RelayCommand] void OpenLecture(LectureItem item) => OnOpenLecture?.Invoke(item);
}
