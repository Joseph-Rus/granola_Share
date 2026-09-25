using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StudyStash.App.ViewModels;

public enum QuickKind
{
    Header,
    Lecture,
    Passage,
    Class,
    Action,
    Source,
}

/// <summary>A row of the quick panel: a group's header, a lecture, a passage from notes, a class, an action, or (for
/// an answer) a source.</summary>
public sealed partial class QuickRow : ObservableObject
{
    public QuickKind Kind { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Right-hand text: "CS 101 · Tue 23 Sep", "12 lectures", "⌥⇧R", "18:05".</summary>
    public string Meta { get; init; } = "";
    /// <summary>A passage's line under it: "Recursion and the call stack · Key points".</summary>
    public string Sub { get; init; } = "";
    public IBrush Dot { get; init; } = Brushes.Gray;
    public string Glyph { get; init; } = "";
    public string? LectureId { get; init; }
    public double? At { get; init; }
    public string? ClassName { get; init; }
    public Action? Run { get; init; }
    [ObservableProperty] public partial bool Selected { get; set; }

    public bool IsHeader => Kind == QuickKind.Header;
    public bool IsLecture => Kind == QuickKind.Lecture;
    public bool IsPassage => Kind == QuickKind.Passage;
    public bool IsClass => Kind == QuickKind.Class;
    public bool IsAction => Kind == QuickKind.Action;
    public bool IsSource => Kind == QuickKind.Source;
    public bool Selectable => Kind != QuickKind.Header;
    public bool First { get; init; }
}

/// <summary>
/// The quick panel (⌥Space, or Alt+Shift+Space on Windows): search as you type, results grouped as Lectures,
/// Passages from notes, Classes and Actions; ⌘Return (Ctrl+Enter) asks your notes instead, and the answer comes
/// with its sources.
/// </summary>
public sealed partial class QuickModel : ObservableObject
{
    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial bool Answering { get; set; }
    [ObservableProperty] public partial string Answer { get; set; } = "";
    [ObservableProperty] public partial bool Thinking { get; set; }
    /// <summary>Nothing matched, or the library can't be reached: said in place of results.</summary>
    [ObservableProperty] public partial string? Note { get; set; }

    public ObservableCollection<QuickRow> Rows { get; } = [];

    public bool HasNote => !string.IsNullOrEmpty(Note);
    public bool Searching => !Answering;
    public string AskHint => Skin.Current == SkinKind.Mac ? "⌘↩ Ask your notes" : "Ctrl+Enter to ask";
    public string AskKey => Skin.Current == SkinKind.Mac ? "⌘↩" : "Ctrl+Enter";
    public string OpenKey => Skin.Current == SkinKind.Mac ? "↩" : "Enter";
    public string CopyKey => Skin.Current == SkinKind.Mac ? "⌘C" : "Ctrl+C";
    public string CloseKey => Skin.Current == SkinKind.Mac ? "esc" : "Esc";

    partial void OnNoteChanged(string? value) => OnPropertyChanged(nameof(HasNote));
    partial void OnAnsweringChanged(bool value) => OnPropertyChanged(nameof(Searching));

    public QuickRow? Selected => Rows.FirstOrDefault(r => r.Selected);

    /// <summary>Move the selection up or down among the rows that can be chosen.</summary>
    public void Move(int by)
    {
        var choosable = Rows.Where(r => r.Selectable).ToList();
        if (choosable.Count == 0) return;
        int at = choosable.FindIndex(r => r.Selected);
        int next = at < 0 ? 0 : Math.Clamp(at + by, 0, choosable.Count - 1);
        foreach (var r in choosable) r.Selected = false;
        choosable[next].Selected = true;
        OnPropertyChanged(nameof(Selected));
    }

    public void SelectFirst()
    {
        foreach (var r in Rows) r.Selected = false;
        if (Rows.FirstOrDefault(r => r.Selectable) is { } first) first.Selected = true;
        OnPropertyChanged(nameof(Selected));
    }

    public Action<string>? OnQuery { get; set; }
    public Func<string, Task>? OnAsk { get; set; }
    public Action<QuickRow>? OnOpen { get; set; }
    public Action? OnClose { get; set; }

    partial void OnQueryChanged(string value)
    {
        if (Answering) Answering = false;
        OnQuery?.Invoke(value);
    }

    [RelayCommand]
    async Task Ask()
    {
        if (Query.Trim().Length == 0 || OnAsk is null) return;
        await OnAsk(Query.Trim());
    }

    [RelayCommand]
    void Open(QuickRow? row)
    {
        row ??= Selected;
        if (row is null) return;
        if (row.Run is not null) row.Run();
        else OnOpen?.Invoke(row);
    }

    [RelayCommand] void Close() => OnClose?.Invoke();
}
