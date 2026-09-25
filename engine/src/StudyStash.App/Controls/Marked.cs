using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace StudyStash.App.Controls;

/// <summary>Text with what you searched for marked: the quick panel's passages ("…each frame on the call stack…").</summary>
public sealed class Marked : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<Marked, string?>(nameof(Source));
    public static readonly StyledProperty<string?> TermsProperty = AvaloniaProperty.Register<Marked, string?>(nameof(Terms));
    public static readonly StyledProperty<IBrush?> MarkProperty = AvaloniaProperty.Register<Marked, IBrush?>(nameof(Mark));
    public static readonly StyledProperty<FontWeight> MarkWeightProperty = AvaloniaProperty.Register<Marked, FontWeight>(nameof(MarkWeight), FontWeight.Normal);

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == TermsProperty || change.Property == MarkProperty || change.Property == MarkWeightProperty) Rebuild();
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
        string text = Source ?? "";
        int at = 0;
        foreach (var (start, length) in Find(text, Terms ?? ""))
        {
            if (start > at) Inlines.Add(new Run(text[at..start]));
            Inlines.Add(new Run(text.Substring(start, length)) { Background = Mark, FontWeight = MarkWeight == FontWeight.Normal ? FontWeight : MarkWeight });
            at = start + length;
        }
        if (at < text.Length) Inlines.Add(new Run(text[at..]));
    }
}
