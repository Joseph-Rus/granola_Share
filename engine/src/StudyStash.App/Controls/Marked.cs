using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace StudyStash.App.Controls;

/// <summary>
/// Text with what you searched for marked: the quick panel's passages ("…each frame on the call stack…"). With a
/// <see cref="MarkRadius"/> or <see cref="MarkPadding"/> the mark is a rounded highlight a little wider than the
/// words, room made for it in the line, the way the design's highlighted span is; otherwise it's a plain background.
/// </summary>
public sealed class Marked : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<Marked, string?>(nameof(Source));
    public static readonly StyledProperty<string?> TermsProperty = AvaloniaProperty.Register<Marked, string?>(nameof(Terms));
    public static readonly StyledProperty<IBrush?> MarkProperty = AvaloniaProperty.Register<Marked, IBrush?>(nameof(Mark));
    public static readonly StyledProperty<FontWeight> MarkWeightProperty = AvaloniaProperty.Register<Marked, FontWeight>(nameof(MarkWeight), FontWeight.Normal);
    public static readonly StyledProperty<double> MarkRadiusProperty = AvaloniaProperty.Register<Marked, double>(nameof(MarkRadius));
    public static readonly StyledProperty<double> MarkPaddingProperty = AvaloniaProperty.Register<Marked, double>(nameof(MarkPadding));

    /// <summary>Where each rounded mark lies in the laid-out text (its padding included).</summary>
    readonly List<(int Start, int Length)> marks = [];

    static Marked() => AffectsRender<Marked>(MarkProperty, MarkRadiusProperty);

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? Terms
    {
        get => GetValue(TermsProperty);
        set => SetValue(TermsProperty, value);
    }

    public IBrush? Mark
    {
        get => GetValue(MarkProperty);
        set => SetValue(MarkProperty, value);
    }

    public FontWeight MarkWeight
    {
        get => GetValue(MarkWeightProperty);
        set => SetValue(MarkWeightProperty, value);
    }

    /// <summary>The mark's corner radius (the Mac's is 4).</summary>
    public double MarkRadius
    {
        get => GetValue(MarkRadiusProperty);
        set => SetValue(MarkRadiusProperty, value);
    }

    /// <summary>How far the mark reaches past the words on each side, pushing the text beside it along (the Mac's is 3).</summary>
    public double MarkPadding
    {
        get => GetValue(MarkPaddingProperty);
        set => SetValue(MarkPaddingProperty, value);
    }

    bool Rounded => MarkRadius > 0 || MarkPadding > 0;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == TermsProperty || change.Property == MarkProperty || change.Property == MarkWeightProperty
            || change.Property == MarkRadiusProperty || change.Property == MarkPaddingProperty)
            Rebuild();
    }

    /// <summary>Where the words fall in the text: the whole phrase first, else each word of it.</summary>
    public static List<(int Start, int Length)> Find(string text, string terms)
    {
        var found = new List<(int, int)>();
        string phrase = terms.Trim();
        if (phrase.Length == 0) return found;
        var words = new List<string> { phrase };
        if (text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) < 0)
            words = [.. phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1)];
        foreach (string w in words)
        {
            for (int at = text.IndexOf(w, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(w, at + w.Length, StringComparison.OrdinalIgnoreCase))
                if (!found.Any(f => at < f.Item1 + f.Item2 && f.Item1 < at + w.Length)) found.Add((at, w.Length));
        }
        return [.. found.OrderBy(f => f.Item1)];
    }

    void Rebuild()
    {
        Inlines?.Clear();
        Inlines ??= [];
        marks.Clear();
        string text = Source ?? "";
        bool rounded = Rounded;
        // Laid out, a run takes as many places as it has characters and a spacer one.
        int at = 0, laid = 0;
        foreach (var (start, length) in Find(text, Terms ?? ""))
        {
            if (start > at)
            {
                Inlines.Add(new Run(text[at..start]));
                laid += start - at;
            }
            var run = new Run(text.Substring(start, length)) { FontWeight = MarkWeight == FontWeight.Normal ? FontWeight : MarkWeight };
            if (!rounded) run.Background = Mark;
            else
            {
                int from = laid;
                if (MarkPadding > 0)
                {
                    Inlines.Add(Spacer());
                    laid++;
                }
                Inlines.Add(run);
                laid += length;
                if (MarkPadding > 0)
                {
                    Inlines.Add(Spacer());
                    laid++;
                }
                marks.Add((from, laid - from));
                at = start + length;
                continue;
            }
            Inlines.Add(run);
            laid += length;
            at = start + length;
        }
        if (at < text.Length) Inlines.Add(new Run(text[at..]));
        InvalidateVisual();
    }

    /// <summary>The room the mark's padding takes: nothing to see, and no height, so the line stays as tall.</summary>
    InlineUIContainer Spacer() => new(new Control { Width = MarkPadding, Height = 0, IsHitTestVisible = false });

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        if (Rounded && Mark is { } brush && marks.Count > 0)
        {
            // Behind the words, as tall as the font's own ascent and descent (as a browser draws a span's background),
            // not the whole line.
            var metrics = new Typeface(FontFamily, FontStyle, FontWeight).GlyphTypeface.Metrics;
            double height = (metrics.Descent - metrics.Ascent) * FontSize / metrics.DesignEmHeight;
            foreach (var (start, length) in marks)
                foreach (var r in TextLayout.HitTestTextRange(start, length))
                {
                    var box = new Rect(r.X, r.Y + (r.Height - height) / 2, r.Width, height).Translate(origin);
                    context.DrawRectangle(brush, null, new RoundedRect(box, MarkRadius));
                }
        }
        base.RenderTextLayout(context, origin);
    }
}
