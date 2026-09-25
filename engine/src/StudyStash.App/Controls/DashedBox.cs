using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StudyStash.App.Controls;

/// <summary>A dashed rounded square: where the tray icon goes, in setup's taskbar picture.</summary>
public sealed class DashedBox : Control
{
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<DashedBox, IBrush?>(nameof(Stroke));

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    static DashedBox() => AffectsRender<DashedBox>(StrokeProperty);

    public override void Render(DrawingContext context)
    {
        var pen = new Pen(Stroke ?? Brushes.Gray, 1.5, new DashStyle([3, 2], 0));
        context.DrawRectangle(null, pen, new RoundedRect(new Rect(Bounds.Size).Deflate(0.75), 4));
    }
}
