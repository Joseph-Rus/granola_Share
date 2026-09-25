using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StudyStash.App.ViewModels;

/// <summary>A line of the live transcript: when it was said, and what.</summary>
public sealed class HeardLine
{
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>The newest line reads in the full text color; older ones fade to secondary.</summary>
    public bool Latest { get; init; }
}

/// <summary>A moment an answer points to: "18:05", to play the recording from there.</summary>
public sealed class SourceChip
{
    public string Label { get; init; } = "";
    public double At { get; init; }
    public string? LectureId { get; init; }
}

/// <summary>One message in the recorder's chat: your question, or its answer with the moments it comes from.</summary>
public sealed partial class ChatMessage : ObservableObject
{
    public bool Mine { get; init; }
    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial bool Thinking { get; set; }
    public ObservableCollection<SourceChip> Sources { get; } = [];
    public bool HasSources => Sources.Count > 0;

    public ChatMessage() => Sources.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSources));
}

/// <summary>
/// The floating recorder: a pill with the time, the waveform, the class, Pause and Stop; expanded, the transcript
/// as it comes in, fading upward, and a chat about the lecture so far.
/// </summary>
public sealed partial class RecorderModel : ObservableObject
{
    [ObservableProperty] public partial string ClassName { get; set; } = "";
    [ObservableProperty] public partial IBrush ClassDot { get; set; } = Brushes.Gray;
    [ObservableProperty] public partial string Elapsed { get; set; } = "00:00";
    [ObservableProperty] public partial bool IsPaused { get; set; }
    [ObservableProperty] public partial IReadOnlyList<double>? Levels { get; set; }
    [ObservableProperty] public partial bool Expanded { get; set; }
    [ObservableProperty] public partial string Question { get; set; } = "";
    /// <summary>Whisper hasn't anything yet: what the transcript area says instead.</summary>
    [ObservableProperty] public partial string Waiting { get; set; } = "What's said shows here a few seconds after it's said.";

    public ObservableCollection<HeardLine> Lines { get; } = [];
    public ObservableCollection<ChatMessage> Chat { get; } = [];

    public bool IsRunning => !IsPaused;
    public string PauseGlyph => IsPaused ? "play_arrow" : "pause";
    public string PauseTip => IsPaused ? "Resume" : "Pause";
    public bool NoLines => Lines.Count == 0;
    public string DisplayClass => ClassName.Length > 0 ? ClassName : "Lecture";

    public RecorderModel()
    {
        Lines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoLines));
    }

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(PauseGlyph));
        OnPropertyChanged(nameof(PauseTip));
    }

    partial void OnClassNameChanged(string value) => OnPropertyChanged(nameof(DisplayClass));

    public Action? OnPause { get; set; }
    public Action? OnStop { get; set; }
    public Action<bool>? OnExpand { get; set; }
    public Func<string, Task>? OnAsk { get; set; }
    public Action<SourceChip>? OnPlay { get; set; }

    [RelayCommand] void Pause() => OnPause?.Invoke();
    [RelayCommand] void Stop() => OnStop?.Invoke();

    [RelayCommand]
    void Toggle()
    {
        Expanded = !Expanded;
        OnExpand?.Invoke(Expanded);
    }

    [RelayCommand]
    async Task Ask()
    {
        string q = Question.Trim();
        if (q.Length == 0 || OnAsk is null) return;
        Question = "";
        await OnAsk(q);
    }

    [RelayCommand] void Play(SourceChip chip) => OnPlay?.Invoke(chip);
}
