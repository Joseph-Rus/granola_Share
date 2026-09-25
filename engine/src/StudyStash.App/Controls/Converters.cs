using Avalonia.Data.Converters;
using Avalonia.Media;

namespace StudyStash.App.Controls;

public static class Converters
{
    /// <summary>A status dot: green when all is well, amber when something needs you.</summary>
    public static readonly IValueConverter GoodOrWarn = new FuncValueConverter<bool, IBrush>(ok => ok ? Skin.Good : Skin.Warn);

    /// <summary>Setup's library button: make one here, or connect to one elsewhere.</summary>
    public static readonly IValueConverter CreateOrConnect = new FuncValueConverter<bool, string>(here => here ? "Create the library" : "Connect");

    /// <summary>A result line: green when it worked, amber when it didn't.</summary>
    public static readonly IValueConverter OkText = new FuncValueConverter<bool, IBrush>(ok => ok ? Skin.Good : Skin.Warn);
}
