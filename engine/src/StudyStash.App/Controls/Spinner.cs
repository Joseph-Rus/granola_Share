using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace StudyStash.App.Controls;

/// <summary>Busy: a quarter arc going round (Fluent's ProgressRing, small), in the accent.</summary>
public sealed class Spinner : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextBlock.ForegroundProperty.AddOwner<Spinner>();
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<Spinner, double>(nameof(Thickness), 2);

    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    double angle;

    public Spinner()
    {
        timer.Tick += (_, _) =>
        {
            angle = (angle + 12) % 360;
            InvalidateVisual();
        };
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Environment.GetEnvironmentVariable("STUDYSTASH_STILL") != "1") timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        double r = Math.Min(Bounds.Width, Bounds.Height) / 2 - Thickness / 2;
        if (r <= 0) return;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double a0 = (angle - 90) * Math.PI / 180, a1 = a0 + Math.PI / 2;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0)), false);
            ctx.ArcTo(new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1)), new Size(r, r), 0, false, SweepDirection.Clockwise);
        }
        context.DrawGeometry(null, new Pen(Foreground ?? Brushes.Gray, Thickness, lineCap: PenLineCap.Round), g);
    }
}
