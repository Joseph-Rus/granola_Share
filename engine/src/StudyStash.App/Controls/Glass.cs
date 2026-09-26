using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace StudyStash.App.Controls;

/// <summary>
/// The Mac's Liquid Glass: a rounded pane of tinted glass (the design's <c>--glass</c>) with its edge highlights and
/// shadow (<c>GlassShadow</c>), the dropdown, the recorder and the quick panel sit on. Views set only
/// <see cref="CornerRadius"/> and <see cref="Decorator.Padding"/> (and <see cref="BoxShadow"/> for the side panel and
/// toolbar kinds); the fill, shadow and filter come from Styles.axaml, so a window can swap the fill for a solid one.
/// With <see cref="BlurBackdropProperty"/> on (the screenshots), what's behind it is blurred, saturated and brightened
/// the way the design's <c>backdrop-filter</c> does. The content isn't clipped to the corners: wrap what must follow
/// them in a Border with the same CornerRadius and ClipToBounds.
/// </summary>
public sealed class Glass : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty = Border.BackgroundProperty.AddOwner<Glass>();
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = Border.CornerRadiusProperty.AddOwner<Glass>();
    public static readonly StyledProperty<BoxShadows> BoxShadowProperty = Border.BoxShadowProperty.AddOwner<Glass>();
    public static readonly StyledProperty<GlassFilter?> FilterProperty = AvaloniaProperty.Register<Glass, GlassFilter?>(nameof(Filter));

    /// <summary>
    /// Blur what's behind the glass. Only for pictures drawn in one pass (the screenshots): a window repaints just
    /// what changed, and a blur there would read last frame's glass back through itself, so the app never turns it on
    /// (floating windows get the system's blur instead). Set it on a window and every Glass in it follows.
    /// </summary>
    public static readonly AttachedProperty<bool> BlurBackdropProperty =
        AvaloniaProperty.RegisterAttached<Glass, Visual, bool>("BlurBackdrop", inherits: true);

    static Glass() => AffectsRender<Glass>(BackgroundProperty, CornerRadiusProperty, BoxShadowProperty, FilterProperty, BlurBackdropProperty);

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public BoxShadows BoxShadow
    {
        get => GetValue(BoxShadowProperty);
        set => SetValue(BoxShadowProperty, value);
    }

    public GlassFilter? Filter
    {
        get => GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    public static bool GetBlurBackdrop(Visual visual) => visual.GetValue(BlurBackdropProperty);

    public static void SetBlurBackdrop(Visual visual, bool value) => visual.SetValue(BlurBackdropProperty, value);

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        // The backdrop first: the fill and the edge highlights go over it, the way the glass lies on the blur.
        if (GetBlurBackdrop(this) && Filter is { } filter)
            context.Custom(new Backdrop(rect, CornerRadius, filter));
        context.DrawRectangle(Background, null, new RoundedRect(rect, CornerRadius), BoxShadow);
    }

    /// <summary>What's already drawn under the glass's shape, blurred, saturated and brightened in place.</summary>
    sealed class Backdrop(Rect rect, CornerRadius radius, GlassFilter filter) : ICustomDrawOperation
    {
        public Rect Bounds => rect;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            using var shape = new SKRoundRect();
            shape.SetRectRadii(new SKRect((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom),
            [
                Corner(radius.TopLeft), Corner(radius.TopRight), Corner(radius.BottomRight), Corner(radius.BottomLeft),
            ]);
            float sigma = (float)filter.Blur;
            using var blur = SKImageFilter.CreateBlur(sigma, sigma);
            using var colour = SKColorFilter.CreateColorMatrix(Matrix(filter.Saturate, filter.Brightness));
            using var backdrop = SKImageFilter.CreateColorFilter(colour, blur);
            canvas.Save();
            canvas.ClipRoundRect(shape, SKClipOperation.Intersect, antialias: true);
            canvas.SaveLayer(new SKCanvasSaveLayerRec { Backdrop = backdrop });
            canvas.Restore();
            canvas.Restore();
        }

        static SKPoint Corner(double r) => new((float)r, (float)r);

        /// <summary>CSS <c>saturate(s) brightness(b)</c> as one colour matrix: the filter spec's saturate (Rec. 709
        /// weights), then every channel times b.</summary>
        static float[] Matrix(double s, double b)
        {
            const double r = 0.213, g = 0.715, bl = 0.072;
            float F(double v) => (float)(v * b);
            return
            [
                F(r + (1 - r) * s), F(g - g * s), F(bl - bl * s), 0, 0,
                F(r - r * s), F(g + (1 - g) * s), F(bl - bl * s), 0, 0,
                F(r - r * s), F(g - g * s), F(bl + (1 - bl) * s), 0, 0,
                0, 0, 0, 1, 0,
            ];
        }
    }
}
