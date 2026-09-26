using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StudyStash.App.Services;

namespace StudyStash.App.ViewModels;

/// <summary>One rubric row: its criterion, the points it's worth (or how it was marked), and the grader's comment,
/// already quoted the way the design sets it.</summary>
public sealed record RubricRow(string Criterion, string Right, string? Comment);

/// <summary>One file on an assignment or its submission: its glyph (by file type), name and size, and how to open it
/// (downloaded into the library's cache the first time, then handed to the OS).</summary>
public sealed partial class FileChip : ObservableObject
{
    public required string Name { get; init; }
    public required string SizeText { get; init; }
    public required string Glyph { get; init; }
    /// <summary>The path the library's files API knows it by (its folder, if any, plus its name).</summary>
    public required string Path { get; init; }

    [ObservableProperty] public partial bool Opening { get; set; }

    /// <summary>Set by <see cref="AssignmentModel"/> when it builds the chip.</summary>
    public Action<FileChip>? OnOpen { get; set; }

    [RelayCommand]
    void Open() => OnOpen?.Invoke(this);
}

/// <summary>
/// One assignment's detail (design 09/10): its meta, three tiles, instructions, rubric and submission. Downloads a
/// file into the library's cache under the student's home the first time it's opened, then just opens the saved copy
/// — <see cref="LoadAsync"/> is the only place that talks to the library.
/// </summary>
public sealed partial class AssignmentModel(CanvasContext context) : ObservableObject
{
    string cls = "";
    string folder = "";

    [ObservableProperty] public partial string Meta { get; set; } = "";
    [ObservableProperty] public partial IBrush Dot { get; set; } = Brushes.Transparent;
    [ObservableProperty] public partial string Title { get; set; } = "";

    [ObservableProperty] public partial string DueValue { get; set; } = "";
    [ObservableProperty] public partial string PointsValue { get; set; } = "";
    [ObservableProperty] public partial string ThirdLabel { get; set; } = "Status";
    [ObservableProperty] public partial string ThirdValue { get; set; } = "";

    [ObservableProperty] public partial string Instructions { get; set; } = "";
    public ObservableCollection<RubricRow> Rubric { get; } = [];
    public bool HasRubric => Rubric.Count > 0;

    [ObservableProperty] public partial string SubmissionStatus { get; set; } = "";
    [ObservableProperty] public partial string SubmissionDetail { get; set; } = "";
    public bool HasSubmissionDetail => SubmissionDetail.Length > 0;
    [ObservableProperty] public partial string? CommentText { get; set; }
    [ObservableProperty] public partial string? CommentAuthor { get; set; }
    public bool HasComment => !string.IsNullOrEmpty(CommentText);
    [ObservableProperty] public partial bool ShowHandInLink { get; set; }
    public ObservableCollection<FileChip> Files { get; } = [];
    public bool HasFiles => Files.Count > 0;

    string? url;

    /// <summary>The host's global search, opened from the detail toolbar's search button.</summary>
    public Action? OnSearch { get; set; }

    /// <summary>Shapes every field from the library's answer.</summary>
    public void Show(CanvasApi.AssignmentDetail d)
    {
        var zone = context.Clock.Zone;
        var now = context.Clock.Now();
        cls = d.Class;
        folder = d.Folder ?? "";
        url = d.Url;

        Dot = context.DotOf(d.Class);
        Meta = CanvasWords.DetailHeader(d.Class, d.Kind);
        Title = d.Name;

        DueValue = d.DueAt is { } due ? CanvasWords.Full(due, zone, now) : "No due date";
        PointsValue = CanvasWords.PointsText(d.Points);
        bool graded = d.Status == "graded" || d.GradedAt is not null;
        ThirdLabel = graded ? "Score" : "Status";
        ThirdValue = graded ? CanvasWords.ScoreOrGradeText(d.Score, d.Points, d.Grade, d.GradingType) : d.Label;

        Instructions = d.Instructions ?? "";

        Rubric.Clear();
        foreach (var r in d.Rubric)
            Rubric.Add(new RubricRow(r.Criterion, CanvasWords.RubricPoints(r), r.Mark?.Comment is { Length: > 0 } c ? CanvasWords.Quote(c) : null));
        OnPropertyChanged(nameof(HasRubric));

        var copy = CanvasWords.SubmissionText(d.Submission, d.Score, d.Points, d.Grade, d.GradingType, d.Excused, d.DueAt ?? now, zone, now);
        SubmissionStatus = copy.Status;
        SubmissionDetail = copy.Detail;
        OnPropertyChanged(nameof(HasSubmissionDetail));

        var comment = d.Comments.Count > 0 ? d.Comments[^1] : null;
        CommentText = comment?.Text;
        CommentAuthor = comment?.Author;
        OnPropertyChanged(nameof(HasComment));

        ShowHandInLink = d.Submission is null && !d.Excused;

        Files.Clear();
        var source = d.Submission is { Files.Count: > 0 } sub ? sub.Files : d.Files;
        foreach (var f in source)
        {
            var chip = new FileChip { Name = f.Name, SizeText = CanvasWords.Size(f.Size), Glyph = FileGlyph(f.Format), Path = CombinePath(folder, f.Name) };
            chip.OnOpen = c => _ = OpenFileAsync(c);
            Files.Add(chip);
        }
        OnPropertyChanged(nameof(HasFiles));
    }

    static string CombinePath(string folder, string name) => folder.Length > 0 ? $"{folder}/{name}" : name;

    /// <summary>The chip's icon by the file's format: a document type the design draws its own way, code by
    /// extension, everything else a plain draft page.</summary>
    static string FileGlyph(string? format) => format?.ToLowerInvariant() switch
    {
        "pdf" => "picture_as_pdf",
        "py" or "js" or "ts" or "java" or "c" or "cpp" or "cs" or "ipynb" or "rb" or "go" => "code",
        "png" or "jpg" or "jpeg" or "gif" or "heic" or "webp" => "image",
        "ppt" or "pptx" or "key" => "slideshow",
        "xls" or "xlsx" or "csv" => "table_chart",
        "doc" or "docx" or "txt" or "md" => "description",
        _ => "draft",
    };

    public async Task LoadAsync(string classId, string id, CancellationToken stop = default)
    {
        if (context.Client is not { } client) return;
        if (await client.AssignmentAsync(classId, id, stop) is { } detail) Show(detail);
    }

    async Task OpenFileAsync(FileChip chip)
    {
        chip.Opening = true;
        try
        {
            string dest = System.IO.Path.Combine(context.Home, "cache", "canvas", cls, chip.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            if (context.Client is { } client && await client.DownloadAsync(cls, chip.Path, dest))
                context.Actions.OpenFile(dest);
        }
        finally
        {
            chip.Opening = false;
        }
    }

    [RelayCommand]
    void OpenInCanvas()
    {
        if (url is { Length: > 0 } u) context.Actions.OpenUrl(u);
    }

    [RelayCommand]
    void HandIn()
    {
        if (url is { Length: > 0 } u) context.Actions.OpenUrl(u);
    }

    [RelayCommand]
    void Search() => OnSearch?.Invoke();
}
