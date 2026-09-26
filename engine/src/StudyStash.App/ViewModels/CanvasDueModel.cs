using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StudyStash.App.Services;

namespace StudyStash.App.ViewModels;

/// <summary>One row of the Due list: an assignment, quiz or discussion, worded the way <see cref="CanvasWords"/>
/// shapes it. <see cref="Select"/> tells <see cref="CanvasDueModel"/> the student picked it; the model owns which
/// row (if any) is selected, so only one lights up at a time.</summary>
public sealed partial class DueRow : ObservableObject
{
    public required string Class { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Right { get; init; }
    /// <summary>The right side reads in the accent, semibold, for a missing item ("Missing").</summary>
    public required bool Strong { get; init; }
    public required string Sub { get; init; }
    public required IBrush Dot { get; init; }

    [ObservableProperty] public partial bool Selected { get; set; }

    /// <summary>Set by <see cref="CanvasDueModel"/> when it builds the row.</summary>
    public Action<DueRow>? OnSelectRow { get; set; }

    [RelayCommand]
    void Select() => OnSelectRow?.Invoke(this);
}

/// <summary>One of the Due list's sections (Overdue, This week, Later, No due date, Handed in).</summary>
public sealed record DueGroup(string Label, bool Accent, IReadOnlyList<DueRow> Rows);

/// <summary>
/// The Due list (design 09): the header, its groups of rows, and which row (if any) is selected. Never fetches
/// lectures or anything else the shell already has — this is Canvas's own "to hand in" list.
/// </summary>
public sealed partial class CanvasDueModel(CanvasContext context) : ObservableObject
{
    [ObservableProperty] public partial string SubHeader { get; set; } = "";
    public ObservableCollection<DueGroup> Groups { get; } = [];
    [ObservableProperty] public partial bool IsEmpty { get; set; } = true;
    [ObservableProperty] public partial DueRow? SelectedRow { get; set; }

    /// <summary>The header's own fixed title — "Due" never changes, unlike the sub-line.</summary>
    public const string Header = "Due";
    public const string EmptyText = "Nothing to hand in.";

    /// <summary>The host opens the item's detail (Assignment) when a row is picked.</summary>
    public Action<string, string>? OnSelect { get; set; }

    /// <summary>Shapes every row from the library's answer. Never asks for lectures — the shell owns those.</summary>
    public void Show(CanvasApi.DueResponse due)
    {
        var zone = context.Clock.Zone;
        var now = context.Clock.Now();
        SubHeader = CanvasWords.DueHeader(due.ToHandIn, due.Synced, zone);

        Groups.Clear();
        foreach (var g in due.Groups)
        {
            var rows = g.Items.Select(item =>
            {
                var row = new DueRow
                {
                    Class = item.Class,
                    Id = item.Id,
                    Title = item.Name,
                    Right = CanvasWords.RightLabel(item),
                    Strong = item.Missing,
                    Sub = CanvasWords.DueSub(item, zone, now),
                    Dot = context.DotOf(item.Class),
                };
                row.OnSelectRow = Select;
                return row;
            }).ToList();
            Groups.Add(new DueGroup(g.Label, g.Key == "overdue", rows));
        }
        IsEmpty = Groups.All(g => g.Rows.Count == 0);
        SelectedRow = null;
    }

    public async Task LoadAsync(CancellationToken stop = default)
    {
        if (context.Client is not { } client) return;
        if (await client.DueAsync(stop) is { } due) Show(due);
    }

    /// <summary>Selects the row for a class and id (e.g. the shell restoring a route), if it's on the list.</summary>
    public void SelectItem(string cls, string id)
    {
        var row = Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Class == cls && r.Id == id);
        if (row is not null) Select(row);
    }

    void Select(DueRow row)
    {
        if (SelectedRow is { } prior) prior.Selected = false;
        row.Selected = true;
        SelectedRow = row;
        OnSelect?.Invoke(row.Class, row.Id);
    }
}
