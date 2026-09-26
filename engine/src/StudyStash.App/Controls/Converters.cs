using System.Collections.ObjectModel;
using Avalonia.Data.Converters;
using StudyStash.App.ViewModels;

namespace StudyStash.App.Controls;

public static class Converters
{
    /// <summary>Setup's library button: make one here, or connect to one elsewhere.</summary>
    public static readonly IValueConverter CreateOrConnect = new FuncValueConverter<bool, string>(here => here ? "Create the library" : "Connect");

    /// <summary>The quick panel's rows but its actions (which the Mac draws as chips below them).</summary>
    public static readonly IValueConverter NotActions =
        new FuncValueConverter<ObservableCollection<QuickRow>?, FilteredRows?>(rows => rows is null ? null : new FilteredRows(rows, r => !r.IsAction));

    /// <summary>Only the quick panel's actions: Record, Open library and the rest.</summary>
    public static readonly IValueConverter Actions =
        new FuncValueConverter<ObservableCollection<QuickRow>?, FilteredRows?>(rows => rows is null ? null : new FilteredRows(rows, r => r.IsAction));

    /// <summary>A count that isn't nothing: show what holds the rows only when there are some.</summary>
    public static readonly IValueConverter Some = new FuncValueConverter<int, bool>(n => n > 0);

    /// <summary>The action that records (or stops recording): the Mac draws its icon filled, in the accent.</summary>
    public static readonly IValueConverter Records = new FuncValueConverter<string?, bool>(glyph => glyph is "mic" or "stop");
}
