using Avalonia.Data.Converters;

namespace StudyStash.App.Controls;

public static class Converters
{
    /// <summary>Setup's library button: make one here, or connect to one elsewhere.</summary>
    public static readonly IValueConverter CreateOrConnect = new FuncValueConverter<bool, string>(here => here ? "Create the library" : "Connect");
}
