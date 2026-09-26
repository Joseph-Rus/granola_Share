using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using StudyStash.Core;

namespace StudyStash.Library;

/// <summary>What every library page needs to know: the library, its classes, and who is looking.</summary>
public sealed class PageContext
{
    public required string PoolName { get; init; }
    public required List<(string Name, int Count)> Classes { get; init; } // the store's, plus configured classes with 0
    public int Total { get; init; }
    public int Processing { get; init; }
    public bool Admin { get; init; }
    public string? Current { get; init; }
    public bool Password { get; init; }
    public string Version { get; init; } = Engine.Version;
    public (string Href, string Label)? Back { get; set; }
    public required string Nonce { get; init; }
    public string Q { get; set; } = "";
    /// <summary>With Canvas on: how many assignments are due within a week (the sidebar's Due). Null hides it.</summary>
    public int? Due { get; init; }
    /// <summary>An AI is set up for chatting: the sidebar has Chat.</summary>
    public bool Chat { get; init; }
    /// <summary>Things captured and not filed yet (the sidebar's Capture). Null hides it.</summary>
    public int? Inbox { get; init; }
}

/// <summary>
/// HTML for the library's pages, as the Python engine writes it (ui.py): one stylesheet, a page shell, and the small
/// pieces pages share. The look is Apple's: the system font, grouped rounded lists, a Notes-style sidebar, and a
/// tag color per class from Apple's palette.
/// </summary>
public static partial class Ui
{
    public const int TagColors = 12; // --c0 … --c11: Apple's red, orange, yellow, green, mint, teal, cyan, blue, indigo, purple, pink, brown

    /// <summary>html.escape(s, quote=True).</summary>
    public static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#x27;",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    /// <summary>urllib.parse.quote: letters, digits, "_.-~" and `safe` stay; the rest is %XX of its UTF-8 bytes.</summary>
    public static string Quote(string s, string safe = "/")
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            char c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~' || (b < 128 && safe.Contains(c))) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>A stable tag color per class (0-11); Unsorted gets none, so it's gray.</summary>
    public static int? Hue(string? name)
    {
        if (string.IsNullOrEmpty(name) || name == Configs.Unsorted) return null;
        string hex = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(name)));
        return Convert.ToInt32(hex[..4], 16) % TagColors;
    }

    public static string HueStyle(string? name) => Hue(name) is int h ? $"--tag:var(--c{h})" : "--tag:var(--gray)";

    public static string ClassUrl(string name) => "/class/" + Quote(name, "");

    // --- dates -----------------------------------------------------------------------------------------------

    static readonly string[] IsoFormats =
        ["yyyy-MM-dd", "yyyyMMdd", "yyyy-MM-dd'T'HH", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss"];

    [GeneratedRegex(@"(?:Z|[+-]\d\d(?::?\d\d)?)$")]
    private static partial Regex ZoneSuffix();

    /// <summary>datetime.fromisoformat(value[:19]), or null.</summary>
    static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        string head = Py.Head(value, 19);
        if (head.Length > 10) head = head[..10] + ZoneSuffix().Replace(head[10..], ""); // a zone only ever follows a time
        return DateTime.TryParseExact(head, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>"Sep 14".</summary>
    public static string ShortDate(string? value) =>
        ParseDate(value) is DateTime d ? d.ToString("MMM", CultureInfo.InvariantCulture) + " " + d.Day : Py.Head(value ?? "", 10);

    /// <summary>"Mon Sep 14, 2026", and ", 10:00 AM" when the date has a time.</summary>
    public static string LongDate(string? value)
    {
        if (ParseDate(value) is not DateTime d) return value ?? "";
        string output = $"{d.ToString("ddd MMM", CultureInfo.InvariantCulture)} {d.Day}, {d.Year}";
        if ((value ?? "").Length > 10)
            output += $", {(d.Hour % 12 == 0 ? 12 : d.Hour % 12)}:{d:mm} {(d.Hour < 12 ? "AM" : "PM")}";
        return output;
    }

    // --- Markdown ----------------------------------------------------------------------------------------

    [GeneratedRegex(@"\$\$(.+?)\$\$|(?<![\\$\w])\$(?=\S)([^$\n]+?)(?<=\S)\$(?![\w$])", RegexOptions.Singleline)]
    private static partial Regex MathSpan();

    [GeneratedRegex("(href|src)=\"\\s*(?:javascript|vbscript|data):[^\"]*\"", RegexOptions.IgnoreCase)]
    private static partial Regex BadUrl();

    [GeneratedRegex("\uE000M(\\d+)\uE001")]
    private static partial Regex MathMark();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex HasScheme();

    // Python-Markdown's "extra" (tables, footnotes, definition lists, abbreviations, fenced code), with raw HTML off.
    static readonly MarkdownPipeline Markdown = new MarkdownPipelineBuilder()
        .UsePipeTables().UseFootnotes().UseDefinitionLists().UseAbbreviations().DisableHtml().Build();

    static bool SafeUrl(string? url)
    {
        string u = Py.Strip(url);
        if (!HasScheme().IsMatch(u)) return true; // relative, #anchor, or empty
        return u.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Markdown to HTML, with raw HTML off and math left as it is for KaTeX. Note text comes from the laptop and the
    /// model's output: it may never inject HTML or scripts.
    /// </summary>
    public static string RenderMd(string? text)
    {
        var maths = new List<string>();
        string stashed = MathSpan().Replace(text ?? "", m =>
        {
            maths.Add(m.Value);
            return $"\uE000M{maths.Count - 1}\uE001";
        });
        var doc = Markdig.Markdown.Parse(stashed, Markdown);
        foreach (var link in doc.Descendants<LinkInline>())
            if (!SafeUrl(link.Url)) link.Url = "#";
        foreach (var link in doc.Descendants<AutolinkInline>())
            if (!link.IsEmail && !SafeUrl(link.Url)) link.Url = "#";
        string html = BadUrl().Replace(doc.ToHtml(Markdown), "$1=\"#\"");
        return MathMark().Replace(html, m => Esc(maths[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]));
    }

    // --- the page shell ----------------------------------------------------------------------------------------

    public static string Head(string title, string nonce, bool math = false)
    {
        string extra = math ? $"<link rel=\"stylesheet\" href=\"{PageText.Katex}/katex.min.css\">" : "";
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">"
            + $"<meta name=\"color-scheme\" content=\"light dark\">{PageText.IconLinks}"
            + $"<title>{Esc(title)}</title>{extra}<style nonce=\"{nonce}\">{PageText.Css}</style></head>";
    }

    public static string Scripts(string nonce, bool math = false)
    {
        string output = $"<script nonce=\"{nonce}\">{PageText.Js}</script>";
        if (math)
            output += $"<script nonce=\"{nonce}\" src=\"{PageText.Katex}/katex.min.js\"></script>"
                + $"<script nonce=\"{nonce}\" src=\"{PageText.Katex}/contrib/auto-render.min.js\"></script>"
                + $"<script nonce=\"{nonce}\">{PageText.MathJs}</script>";
        return output;
    }

    static string Plural(int n, string one, string many) => n != 1 ? many : one;

    public static string Sidebar(PageContext ctx)
    {
        string Item(string href, string label, int? n = null, string? key = null, string? style = null)
        {
            string current = key is not null && key == ctx.Current ? " aria-current=\"page\"" : "";
            string dot = style is not null ? $"<span class=\"dot\" style=\"{style}\"></span>" : "";
            string count = n is int c ? $"<span class=\"n\">{c}</span>" : "";
            return $"<a href=\"{href}\"{current}>{dot}<span class=\"name\">{Esc(label)}</span>{count}</a>";
        }

        var items = new List<string> { Item("/", "Recent", key: "home") };
        if (ctx.Chat) items.Add(Item("/chat", "Chat", key: "chat"));
        if (ctx.Inbox is int inbox) items.Add(Item("/inbox", "Capture", inbox > 0 ? inbox : null, "inbox"));
        if (ctx.Due is int due) items.Add(Item("/due", "Due", due, "due"));
        if (ctx.Processing > 0) items.Add(Item("/#queue", "Being written", ctx.Processing, "queue"));
        items.Add("<div class=\"nav-head\">Classes</div>");
        foreach (var (name, n) in ctx.Classes)
            if (name != Configs.Unsorted) items.Add(Item(ClassUrl(name), name, n, $"class:{name}", HueStyle(name)));
        int unsorted = ctx.Classes.Where(c => c.Name == Configs.Unsorted).Select(c => c.Count).LastOrDefault();
        items.Add(Item("/unsorted", "Unsorted", unsorted, "unsorted", HueStyle(Configs.Unsorted)));
        var foot = new List<string>();
        if (ctx.Admin) foot.Add(Item("/settings", "Settings", key: "settings"));
        if (ctx.Password) foot.Add(Item("/logout", "Log out"));
        return $"<aside class=\"side\"><a class=\"brand\" href=\"/\">{Esc(ctx.PoolName)}"
            + $"<small>{ctx.Total} lecture{Plural(ctx.Total, "", "s")}</small></a>"
            + "<form class=\"search\" action=\"/search\" role=\"search\"><input type=\"search\" name=\"q\" "
            + $"value=\"{Esc(ctx.Q)}\" placeholder=\"Search\" aria-label=\"Search lectures\"></form>"
            + $"<nav class=\"nav\" aria-label=\"Classes\">{string.Concat(items)}</nav>"
            + $"<div class=\"side-foot nav\">{string.Concat(foot)}<span class=\"ver\">Version {Esc(ctx.Version)}</span></div>"
            + "</aside>";
    }

    /// <summary>Phones: the sidebar folds away; a back button and the page's name take its place.</summary>
    public static string Topbar(PageContext ctx)
    {
        string left = ctx.Back is var (href, label) ? $"<a class=\"back\" style=\"margin:0\" href=\"{href}\">{Esc(label)}</a>"
            : "<span></span>"; // home: the large title below names the library, as on iOS
        string right = ctx.Admin && ctx.Current != "settings" ? "<a href=\"/settings\">Settings</a>" : "";
        return $"<header class=\"topbar\">{left}{right}</header>";
    }

    public static string Page(string title, string body, PageContext ctx, bool math = false) =>
        Head(title != ctx.PoolName ? $"{title} \u00b7 {ctx.PoolName}" : title, ctx.Nonce, math)
        + $"<body><div class=\"app\">{Sidebar(ctx)}{Topbar(ctx)}<main class=\"main\"><div class=\"wrap\">{body}</div></main></div>"
        + Scripts(ctx.Nonce, math) + "</body></html>";

    public static string BarePage(string title, string body, string nonce) =>
        Head(title, nonce) + $"<body>{body}{Scripts(nonce)}</body></html>";

    // --- the pieces pages share ---------------------------------------------------------------------------------

    public static string SearchBox(string q = "") =>
        $"<form class=\"search\" action=\"/search\" role=\"search\"><input type=\"search\" name=\"q\" value=\"{Esc(q)}\" "
        + "placeholder=\"Search titles, summaries, and transcripts\" aria-label=\"Search lectures\"></form>";

    static List<string> Topics(string? json) =>
        (JsonNode.Parse(string.IsNullOrEmpty(json) ? "[]" : json) as JsonArray ?? new JsonArray()).Select(Py.Str).ToList();

    public static string NoteRow(NoteRow r, string snippet = "")
    {
        string topics = string.Join(", ", Topics(r.Topics).Take(3));
        var about = new List<string>
        {
            $"<time datetime=\"{Esc(Py.Head(r.Date ?? "", 10))}\">{Esc(ShortDate(r.Date))}</time>",
            $"<span class=\"tag\" style=\"{HueStyle(r.ClassName)}\">{Esc(r.ClassName)}</span>",
        };
        if (topics.Length > 0) about.Add($"<span>{Esc(topics)}</span>");
        string snip = snippet.Length > 0 ? $"<div class=\"snippet\">{snippet}</div>" : "";
        string title = !string.IsNullOrEmpty(r.LectureTitle) ? r.LectureTitle : r.Title ?? "";
        return $"<a class=\"row chev\" href=\"/note/{Quote(r.Id, "")}\"><div class=\"grow\">"
            + $"<div class=\"title\">{Esc(title)}</div>"
            + $"<div class=\"subtitle\">{string.Concat(about)}</div>{snip}</div></a>";
    }

    public static string NoteList(IReadOnlyList<NoteRow> rows, string emptyTitle, string emptyText, IReadOnlyDictionary<string, string>? snippets = null)
    {
        if (rows.Count == 0) return $"<div class=\"empty\"><strong>{Esc(emptyTitle)}</strong>{Esc(emptyText)}</div>";
        return "<div class=\"group\">" + string.Concat(rows.Select(r => NoteRow(r, snippets?.GetValueOrDefault(r.Id) ?? ""))) + "</div>";
    }

    [GeneratedRegex("[#*_`>|]")]
    private static partial Regex MarkdownMarks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>A bit of text around the first match, with the match highlighted.</summary>
    public static string Snippet(string? text, string q, int width = 90)
    {
        text ??= "";
        int i = text.ToLowerInvariant().IndexOf(q.ToLowerInvariant(), StringComparison.Ordinal);
        if (i < 0 || q.Length == 0) return "";
        int start = Math.Max(0, i - width), end = Math.Min(text.Length, i + q.Length + width);
        string Clean(string s) => Spaces().Replace(MarkdownMarks().Replace(s, ""), " ");
        return (start > 0 ? "\u2026" : "") + Esc(Clean(text[start..i])) + $"<mark>{Esc(text.Substring(i, q.Length))}</mark>"
            + Esc(Clean(text[(i + q.Length)..end])) + (end < text.Length ? "\u2026" : "");
    }
}
