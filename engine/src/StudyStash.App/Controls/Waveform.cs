using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StudyStash.App.Controls;

/// <summary>The recording's loudness as bars, 3 px wide with 2 px between, centered and rounded: the recorder's and the
/// panel's waveform. <see cref="Levels"/> is 0 to 1, oldest first; the newest bars are drawn.</summary>
public sealed class Waveform : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> LevelsProperty = AvaloniaProperty.Register<Waveform, IReadOnlyList<double>?>(nameof(Levels));
    public static readonly StyledProperty<int> BarsProperty = AvaloniaProperty.Register<Waveform, int>(nameof(Bars), 22);
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextBlock.ForegroundProperty.AddOwner<Waveform>();
    public static readonly StyledProperty<double> MinBarProperty = AvaloniaProperty.Register<Waveform, double>(nameof(MinBar), 4);

    static Waveform() => AffectsRender<Waveform>(LevelsProperty, BarsProperty, ForegroundProperty);

    public IReadOnlyList<double>? Levels
    {
        get => GetValue(LevelsProperty);
        set => SetValue(LevelsProperty, value);
    }

    public int Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double MinBar
    {
        get => GetValue(MinBarProperty);
        set => SetValue(MinBarProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? Bars * 5 - 2 : Math.Min(availableSize.Width, Bars * 5 - 2), double.IsInfinity(availableSize.Height) ? 28 : availableSize.Height);

    public override void Render(DrawingContext context)
    {
        var levels = Levels;
        var brush = Foreground ?? Brushes.Gray;
        double h = Bounds.Height;
        for (int i = 0; i < Bars; i++)
        {
            double level = levels is { Count: > 0 } ? levels[Math.Max(0, levels.Count - Bars + i)] : 0;
            double bar = Math.Max(MinBar, Math.Min(h, level * h));
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(i * 5, (h - bar) / 2, 3, bar), 2));
        }
    }
}
