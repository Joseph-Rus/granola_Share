using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace StudyStash.App;

public partial class App : Application
{
    Avalonia.Styling.Style? tracking;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Resources.MergedDictionaries.Add(Skin.Build(Skin.Current));
        UseSkin(Skin.Current);
    }

    /// <summary>Switch looks while running (screenshots of both, or trying the other one).</summary>
    public void UseSkin(SkinKind kind)
    {
        Skin.Current = kind;
        Resources.MergedDictionaries[0] = Skin.Build(kind);
        if (tracking is not null) Styles.Remove(tracking);
        tracking = kind == SkinKind.Mac ? Controls.Typography.MacStyle() : null;
        if (tracking is not null) Styles.Add(tracking);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) Shell.Start(this, desktop);
        base.OnFrameworkInitializationCompleted();
    }
}
