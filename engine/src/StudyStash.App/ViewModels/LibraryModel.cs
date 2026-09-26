using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StudyStash.App.ViewModels;

/// <summary>A class in the sidebar: its dot, name and how many lectures (Unsorted has an inbox instead of a dot).</summary>
public sealed partial class ClassItem : ObservableObject
{
    public string Name { get; init; } = "";
    public IBrush Dot { get; init; } = Brushes.Gray;
    public bool IsUnsorted { get; init; }
    /// <summary>Not a class: what's due soon in every class, from Canvas.</summary>
    public bool IsDue { get; init; }
    [ObservableProperty] public partial int Count { get; set; }
    [ObservableProperty] public partial bool Selected { get; set; }
    public bool HasDot => !IsUnsorted;
}

/// <summary>A lecture in the middle column: title, "Tue 23 Sep · 1 h 12 min", and the lecture in a sentence.</summary>
public sealed partial class LectureCard : ObservableObject
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public string Summary { get; init; } = "";
    public bool Last { get; init; }
    [ObservableProperty] public partial bool Selected { get; set; }
    public bool HasSummary => Summary.Length > 0;
}

/// <summary>Lectures under a heading: "This week", "Last week", "September".</summary>
public sealed class LectureGroup
{
    public string Label { get; init; } = "";
    public bool First { get; init; }
    public ObservableCollection<LectureCard> Items { get; } = [];
}

/// <summary>The lecture open on the right: its notes (Markdown) or its transcript.</summary>
public sealed partial class NoteModel : ObservableObject
{
    public string Id { get; init; } = "";
    public string ClassName { get; init; } = "";
    public IBrush Dot { get; init; } = Brushes.Gray;
    /// <summary>"CS 101 · Tuesday 23 September · 1 h 12 min".</summary>
    public string Meta { get; init; } = "";
    public string Title { get; init; } = "";
    public string Markdown { get; init; } = "";
    /// <summary>The notes aren't written yet (the library is on it), or couldn't be.</summary>
    public string? Pending { get; init; }
    public ObservableCollection<HeardLine> Transcript { get; } = [];
    [ObservableProperty] public partial bool ShowTranscript { get; set; }

    public bool ShowNotes => !ShowTranscript;
    public bool HasPending => !string.IsNullOrEmpty(Pending);
    public bool NoTranscript => Transcript.Count == 0;

    partial void OnShowTranscriptChanged(bool value) => OnPropertyChanged(nameof(ShowNotes));

    [RelayCommand] void Notes() => ShowTranscript = false;
    [RelayCommand] void Transcripts() => ShowTranscript = true;
}

/// <summary>
/// The full app: classes on the left, a class's lectures by week in the middle, the lecture on the right, and an
/// ask bar floating over it.
/// </summary>
public sealed partial class LibraryModel : ObservableObject
{
    public ObservableCollection<ClassItem> Classes { get; } = [];
    /// <summary>What's due soon, if Canvas has anything: the sidebar draws it above "Classes".</summary>
    public ClassItem? Due => Classes.FirstOrDefault(c => c.IsDue);
    public bool HasDue => Due is not null;
    [ObservableProperty] public partial ClassItem? Unsorted { get; set; }
    public ObservableCollection<LectureGroup> Groups { get; } = [];
    [ObservableProperty] public partial string ClassTitle { get; set; } = "";
    [ObservableProperty] public partial string ClassCount { get; set; } = "";
    [ObservableProperty] public partial NoteModel? Note { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool StatusGood { get; set; } = true;
    [ObservableProperty] public partial string Question { get; set; } = "";
    [ObservableProperty] public partial string Scope { get; set; } = "This lecture";
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    /// <summary>Draw the window's own buttons (screenshots; a real window has the system's).</summary>
    [ObservableProperty] public partial bool DrawChrome { get; set; }
    /// <summary>The class has no lectures yet, or nothing is chosen: what the middle column says.</summary>
    [ObservableProperty] public partial string? Empty { get; set; }

    /// <summary>The answer to what was asked in the ask bar, shown above it; null hides it.</summary>
    [ObservableProperty] public partial string? Answer { get; set; }
    [ObservableProperty] public partial bool Thinking { get; set; }
    [ObservableProperty] public partial string AskedQuestion { get; set; } = "";
    public ObservableCollection<SourceChip> Sources { get; } = [];

    public bool HasAnswer => Answer is not null || Thinking;
    public bool HasSources => Sources.Count > 0;

    public LibraryModel()
    {
        Sources.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSources));
        Classes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Due));
            OnPropertyChanged(nameof(HasDue));
        };
    }

    partial void OnAnswerChanged(string? value) => OnPropertyChanged(nameof(HasAnswer));
    partial void OnThinkingChanged(bool value) => OnPropertyChanged(nameof(HasAnswer));

    public Action<SourceChip>? OnSource { get; set; }

    [RelayCommand] void OpenSource(SourceChip chip) => OnSource?.Invoke(chip);

    /// <summary>The answer was closed: the next question starts a new conversation.</summary>
    public Action? OnCloseAnswer { get; set; }

    [RelayCommand]
    void CloseAnswer()
    {
        OnCloseAnswer?.Invoke();
        Answer = null;
        Thinking = false;
        Sources.Clear();
    }

    public bool HasNote => Note is not null;
    public bool NoNote => Note is null;
    public bool HasEmpty => !string.IsNullOrEmpty(Empty);

    partial void OnNoteChanged(NoteModel? value)
    {
        OnPropertyChanged(nameof(HasNote));
        OnPropertyChanged(nameof(NoNote));
    }

    partial void OnEmptyChanged(string? value) => OnPropertyChanged(nameof(HasEmpty));

    public Action<ClassItem>? OnClass { get; set; }
    public Action<LectureCard>? OnLecture { get; set; }
    public Func<string, string, Task>? OnAsk { get; set; }
    public Action? OnMove { get; set; }
    public Action? OnExport { get; set; }
    public Action? OnSearch { get; set; }
    public Action? OnSettings { get; set; }
    public Action? OnScope { get; set; }
    public Action? OnMore { get; set; }

    [RelayCommand] void PickClass(ClassItem c) => OnClass?.Invoke(c);
    [RelayCommand] void PickLecture(LectureCard l) => OnLecture?.Invoke(l);
    [RelayCommand] void Move() => OnMove?.Invoke();
    [RelayCommand] void Export() => OnExport?.Invoke();
    [RelayCommand] void Search() => OnSearch?.Invoke();
    [RelayCommand] void Settings() => OnSettings?.Invoke();
    [RelayCommand] void PickScope() => OnScope?.Invoke();
    [RelayCommand] void More() => OnMore?.Invoke();

    [RelayCommand]
    async Task Ask()
    {
        string q = Question.Trim();
        if (q.Length == 0 || OnAsk is null) return;
        Question = "";
        await OnAsk(q, Scope);
    }
}
