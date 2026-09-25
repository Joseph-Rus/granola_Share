using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StudyStash.App.Controls;

/// <summary>One of the design's icons (<see cref="IconPaths"/>), drawn in the text color at any size:
/// <c>&lt;c:Icon Name="search" Size="17"/&gt;</c>, and <c>Filled="True"</c> for the solid ones (pause, stop, play).</summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string> GlyphProperty = AvaloniaProperty.Register<Icon, string>(nameof(Glyph), "");
    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<Icon, bool>(nameof(Filled));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Icon, double>(nameof(Size), 18);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextBlock.ForegroundProperty.AddOwner<Icon>();

    static readonly Dictionary<string, Geometry> Cache = [];

    static Icon() => AffectsRender<Icon>(GlyphProperty, FilledProperty, ForegroundProperty);

    public string Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public bool Filled
    {
        get => GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SizeProperty) InvalidateMeasure();
    }

    public static Geometry? Find(string name, bool filled)
    {
        string key = filled && IconPaths.All.ContainsKey(name + "#fill") ? name + "#fill" : name;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var g)) return g;
            if (!IconPaths.All.TryGetValue(key, out string? path)) return null;
            return Cache[key] = Geometry.Parse(path);
        }
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Find(Glyph, Filled) is not { } g) return;
        double scale = Size / 24;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - Size) / 2, (Bounds.Height - Size) / 2)))
            context.DrawGeometry(Foreground ?? Brushes.Black, null, g);
    }
}
