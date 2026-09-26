using Avalonia;
using Avalonia.Data.Converters;

namespace StudyStash.App.Controls;

public static class Converters
{
    /// <summary>Setup's library button: make one here, or connect to one elsewhere.</summary>
    public static readonly IValueConverter CreateOrConnect = new FuncValueConverter<bool, string>(here => here ? "Create the library" : "Connect");

    /// <summary>Windows card lists: no gap above the first card, a gap above the rest.</summary>
    public static readonly IValueConverter FirstCardMargin = new FuncValueConverter<bool, Thickness>(first => first ? new Thickness(0) : new Thickness(0, 8, 0, 0));

    /// <summary>Windows toggle label: "On" or "Off".</summary>
    public static readonly IValueConverter OnOff = new FuncValueConverter<bool, string>(on => on ? "On" : "Off");
}
