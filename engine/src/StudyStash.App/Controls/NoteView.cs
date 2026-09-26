using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;

namespace StudyStash.App.Controls;

/// <summary>
/// A lecture's notes, from their Markdown, in the design's type: section headings (SF Pro Display 17, or Segoe UI
/// Display 20), reading text (New York 16/1.6 on a Mac, Segoe UI 15/1.6 on Windows), bullets and numbered questions
/// with their markers in the tertiary color, and the Definitions section as two columns with a rule above each term.
/// </summary>
public sealed partial class NoteView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<NoteView, string?>(nameof(Markdown));

    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseEmphasisExtras().Build();

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty) Build();
    }

    bool Mac => Skin.Current == SkinKind.Mac;
    double BodySize => Mac ? 16 : 15;

    TextBlock Text(string resourceFont, double size, double lineHeight)
    {
        var t = new TextBlock { FontSize = size, LineHeight = size * lineHeight, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.FontFamilyProperty, t.GetResourceObservable(resourceFont));
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("Fg"));
        return t;
    }

    TextBlock Body() => Text(Mac ? "SerifFont" : "TextFont", BodySize, 1.6);

    TextBlock Secondary(double size)
    {
        var t = Text("TextFont", size, 1.5);
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("Fg2"));
        return t;
    }

    TextBlock Marker(string text)
    {
        var t = Text(Mac ? "SerifFont" : "TextFont", BodySize, 1.6);
        t.Text = text;
        t.TextWrapping = TextWrapping.NoWrap;
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("Fg3"));
        return t;
    }

    void Build()
    {
        Children.Clear();
        Spacing = Mac ? 14 : 12;
        var doc = Markdig.Markdown.Parse(Markdown ?? "", Pipeline);
        bool first = true;
        string section = "";
        foreach (MdBlock block in doc)
        {
            switch (block)
            {
                case HeadingBlock h:
                    section = Plain(h.Inline);
                    var head = Text("DisplayFont", Mac ? 17 : 20, 1.25);
                    head.FontWeight = FontWeight.SemiBold;
                    Fill(head, h.Inline);
                    head.Margin = new Thickness(0, first ? (Mac ? 18 : 16) : (Mac ? 14 : 12), 0, 0);
                    Children.Add(head);
                    break;
                case ParagraphBlock p:
                    var body = Body();
                    Fill(body, p.Inline);
                    Children.Add(body);
                    break;
                case ListBlock list when IsDefinitions(section, list):
                    Children.Add(Definitions(list));
                    break;
                case ListBlock list:
                    Children.Add(List(list));
                    break;
                case FencedCodeBlock or CodeBlock:
                    Children.Add(Code(((LeafBlock)block).Lines.ToString()));
                    break;
                case QuoteBlock q:
                    var quote = Body();
                    quote.Text = string.Join(" ", q.Descendants<ParagraphBlock>().Select(x => Plain(x.Inline)));
                    quote.Margin = new Thickness(14, 0, 0, 0);
                    quote.Bind(TextBlock.ForegroundProperty, quote.GetResourceObservable("Fg2"));
                    Children.Add(quote);
                    break;
                case ThematicBreakBlock:
                    var rule = new Border { Height = 1, Margin = new Thickness(0, 6) };
                    rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable("Sep"));
                    Children.Add(rule);
                    break;
            }
            if (block is not HeadingBlock) first = false;
            else if (!first) first = false;
        }
    }

    [GeneratedRegex(@"^\s*\*\*[^*]+\*\*\s*[:—–-]")]
    private static partial Regex TermFirst();

    /// <summary>The Definitions section, or any list whose every item starts "**term**: …".</summary>
    static bool IsDefinitions(string section, ListBlock list) =>
        !list.IsOrdered && list.Count > 0 && (section.Equals("Definitions", StringComparison.OrdinalIgnoreCase)
            || list.OfType<ListItemBlock>().All(i => i.FirstOrDefault() is ParagraphBlock p && TermFirst().IsMatch(Source(p))))
        && list.OfType<ListItemBlock>().All(i => i.FirstOrDefault() is ParagraphBlock p && p.Inline?.FirstChild is EmphasisInline { DelimiterCount: 2 });

    static string Source(ParagraphBlock p) => p.Inline is null ? "" : string.Concat(p.Inline.Descendants<LiteralInline>().Select(l => l.Content.ToString()));

    Control Definitions(ListBlock list)
    {
        var rows = new StackPanel();
        foreach (var item in list.OfType<ListItemBlock>())
        {
            if (item.FirstOrDefault() is not ParagraphBlock p || p.Inline?.FirstChild is not EmphasisInline term) continue;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,16,*"), Margin = new Thickness(0) };
            var cell = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10), Child = row };
            cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("Sep"));
            var name = Text("TextFont", 14, 1.5);
            name.FontWeight = FontWeight.SemiBold;
            name.Text = Plain(term);
            var meaning = Secondary(14);
            Fill(meaning, p.Inline, skip: term, trimLead: true);
            Grid.SetColumn(meaning, 2);
            row.Children.Add(name);
            row.Children.Add(meaning);
            rows.Children.Add(cell);
        }
        return rows;
    }

    Control List(ListBlock list)
    {
        var items = new StackPanel { Spacing = 6 };
        int n = int.TryParse(list.OrderedStart, out int start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,12,*") };
            row.Children.Add(Marker(list.IsOrdered ? $"{n++}" : "•"));
            var content = new StackPanel { Spacing = 6 };
            foreach (var inner in item)
            {
                if (inner is ParagraphBlock p)
                {
                    var t = Body();
                    Fill(t, p.Inline);
                    content.Children.Add(t);
                }
                else if (inner is ListBlock nested)
                {
                    content.Children.Add(List(nested));
                }
            }
            Grid.SetColumn(content, 2);
            row.Children.Add(content);
            items.Children.Add(row);
        }
        return items;
    }

    Control Code(string text)
    {
        var t = new TextBlock { Text = text.TrimEnd(), FontFamily = new FontFamily("SF Mono, Menlo, Cascadia Mono, Consolas, monospace"), FontSize = 13, LineHeight = 19.5, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("Fg"));
        var box = new Border { Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(Mac ? 8 : 4), Child = t };
        box.Bind(Border.BackgroundProperty, box.GetResourceObservable("Fill2"));
        return box;
    }

    static string Plain(ContainerInline? inline) => inline is null ? "" : string.Concat(inline.Descendants<LiteralInline>().Select(l => l.Content.ToString()))
        + string.Concat(inline.Descendants<CodeInline>().Select(c => c.Content));

    /// <summary>A paragraph's text, with bold, italics, code and math kept.</summary>
    static void Fill(TextBlock t, ContainerInline? inline, MdInline? skip = null, bool trimLead = false)
    {
        t.Inlines ??= [];
        if (inline is null) return;
        bool lead = trimLead;
        foreach (var run in Runs(inline, skip, null, null))
        {
            if (lead)
            {
                string trimmed = run.Text?.TrimStart(' ', ':', '—', '–', '-') ?? "";
                if (trimmed.Length == 0) continue;
                run.Text = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
                lead = false;
            }
            t.Inlines.Add(run);
        }
    }

    /// <summary>The text as runs; weight and style are set only where the Markdown changes them, so a heading's
    /// runs stay as heavy as the heading.</summary>
    static IEnumerable<Run> Runs(ContainerInline container, MdInline? skip, FontWeight? weight, FontStyle? style)
    {
        for (var i = container.FirstChild; i is not null; i = i.NextSibling)
        {
            if (ReferenceEquals(i, skip)) continue;
            switch (i)
            {
                case LiteralInline l:
                    var run = new Run(Math(l.Content.ToString()));
                    if (weight is { } w) run.FontWeight = w;
                    if (style is { } st) run.FontStyle = st;
                    yield return run;
                    break;
                case EmphasisInline e:
                    foreach (var r in Runs(e, skip, e.DelimiterCount >= 2 ? FontWeight.SemiBold : weight, e.DelimiterCount == 1 ? FontStyle.Italic : style)) yield return r;
                    break;
                case CodeInline c:
                    yield return new Run(c.Content) { FontFamily = new FontFamily("SF Mono, Menlo, Cascadia Mono, Consolas, monospace"), FontSize = 14 };
                    break;
                case LineBreakInline:
                    yield return new Run(" ");
                    break;
                case LinkInline link:
                    foreach (var r in Runs(link, skip, weight, style)) yield return r;
                    break;
                case ContainerInline other:
                    foreach (var r in Runs(other, skip, weight, style)) yield return r;
                    break;
            }
        }
    }

    [GeneratedRegex(@"\$([^$]+)\$")]
    private static partial Regex InlineMath();

    /// <summary>$…$ math, readable without a math renderer: the dollar signs go, a few commands become symbols.</summary>
    static string Math(string text) => InlineMath().Replace(text, m => m.Groups[1].Value
        .Replace(@"\times", "×").Replace(@"\cdot", "·").Replace(@"\le", "≤").Replace(@"\ge", "≥").Replace(@"\ne", "≠")
        .Replace(@"\to", "→").Replace(@"\infty", "∞").Replace(@"\pi", "π").Replace(@"\sum", "Σ").Replace(@"\int", "∫")
        .Replace(@"\sqrt", "√").Replace(@"\Delta", "Δ").Replace(@"\alpha", "α").Replace(@"\beta", "β").Replace(@"\theta", "θ")
        .Replace("{", "").Replace("}", "").Replace(@"\", ""));
}
