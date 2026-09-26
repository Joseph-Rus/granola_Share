using Avalonia;
using Avalonia.Data.Converters;

namespace StudyStash.App.Controls;

public static class Converters
{
    /// <summary>Setup's library button: make one here, or connect to one elsewhere.</summary>
    public static readonly IValueConverter CreateOrConnect = new FuncValueConverter<bool, string>(here => here ? "Create the library" : "Connect");

    /// <summary>Windows card lists: the first card in a list sits flush; the rest get a gap above them.</summary>
    public static readonly IValueConverter FirstCardMargin = new FuncValueConverter<bool, Thickness>(first => first ? new Thickness(0) : new Thickness(0, 8, 0, 0));

    /// <summary>Windows toggle label: "On" or "Off" next to the switch.</summary>
    public static readonly IValueConverter OnOff = new FuncValueConverter<bool, string>(on => on ? "On" : "Off");

    /// <summary>A row that only shows when it has something to say: an engine's subtitle, a menu's footer.</summary>
    public static readonly IValueConverter NotEmpty = new FuncValueConverter<string?, bool>(s => !string.IsNullOrEmpty(s));
}
