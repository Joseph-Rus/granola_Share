using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace StudyStash.Core.Canvas;

/// <summary>What <see cref="HtmlText.Convert"/> needs to make Canvas's HTML read well: the course's Canvas address and
/// id (to make relative links absolute and to build a file's API address), and a lookup for a file's local path when
/// one is already known (so the link can point there right away instead of at Canvas).</summary>
public sealed record HtmlContext(string BaseUrl, long CourseId, Func<long, string?> Local)
{
    /// <summary>No course to resolve against: relative links and file links are left as Canvas wrote them
    /// (<see cref="HtmlText.ToMarkdown"/>'s callers that don't yet track files).</summary>
    public static readonly HtmlContext None = new("", 0, _ => null);
}

/// <summary>A file Canvas's HTML links to: which course, which file, and the API address to ask for its metadata.</summary>
public sealed record CanvasFileLink(long CourseId, long FileId, string ApiUrl);

/// <summary>Canvas's HTML (assignment instructions, pages, announcements, the syllabus) as plain Markdown that reads
/// well: screen-reader-only clutter is dropped, equation images become <c>$LaTeX$</c>, video/embed iframes become
/// links, and relative addresses are made absolute on the course's Canvas. Links to Canvas files are found and
/// rewritten to a local copy once <see cref="HtmlContext.Local"/> knows where it landed.</summary>
public static partial class HtmlText
{
    static readonly ReverseMarkdown.Converter Converter = new(new ReverseMarkdown.Config
    {
        GithubFlavored = true,
        Tags = { Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass },
        Formatting = { RemoveComments = true },
        Links = { SmartHref = true },
    });

    static readonly string[] VideoHosts = ["youtube.com", "youtu.be", "vimeo.com"];

    [GeneratedRegex("<(script|style)\\b[^>]*>.*?</\\1\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Code();

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex Gaps();

    [GeneratedRegex("[<>]")]
    private static partial Regex AngleBrackets();

    [GeneratedRegex("/files/(\\d+)(?:[/?]|$)")]
    private static partial Regex FileIdIn();

    // Stops before ) or ]: the URL is usually inside a Markdown link or image, "(https://.../files/555)".
    [GeneratedRegex("https?://[^\\s)\\]]*?/files/(\\d+)(?:[/?][^\\s)\\]]*)?")]
    private static partial Regex FileUrlInText();

    /// <summary>Plain Markdown, without the file-link plumbing (existing callers that only want readable text).</summary>
    public static string ToMarkdown(string? html) => Convert(html, HtmlContext.None).Markdown;

    /// <summary>
    /// Canvas's HTML as Markdown, plus every Canvas file it links to. Malformed markup still keeps the words: AngleSharp
    /// parses almost anything, and a Markdown conversion that still fails falls back to stripping tags.
    /// </summary>
    public static (string Markdown, List<CanvasFileLink> Files) Convert(string? html, HtmlContext ctx)
    {
        if (string.IsNullOrWhiteSpace(html)) return ("", []);
        var files = new Dictionary<long, CanvasFileLink>();
        string prepared;
        try
        {
            prepared = Prepare(html, ctx, files);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NullReferenceException or IndexOutOfRangeException)
        {
            files.Clear();
            prepared = html;
        }
        return (ConvertCore(prepared), files.Values.ToList());
    }

    static string ConvertCore(string html)
    {
        string md;
        try
        {
            md = Converter.Convert(Code().Replace(html, ""));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            md = AngleBrackets().IsMatch(html) ? Tags().Replace(html, " ") : html; // markup too broken to convert: keep the words
        }
        return Py.Strip(Gaps().Replace(md.ReplaceLineEndings("\n"), "\n\n"));
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    /// <summary>An AngleSharp pass before ReverseMarkdown: drops clutter Canvas adds for screen readers and icons,
    /// turns equation images and iframes into text ReverseMarkdown can carry over, and makes every link and image
    /// address absolute so it still works outside Canvas. A link to a Canvas file is remembered, and rewritten to its
    /// local copy when <paramref name="ctx"/> already knows where that landed.</summary>
    static string Prepare(string html, HtmlContext ctx, Dictionary<long, CanvasFileLink> files)
    {
        var document = new HtmlParser().ParseDocument($"<div id=\"study-stash-root\">{html}</div>");
        var root = document.GetElementById("study-stash-root")!;

        foreach (var gone in root.QuerySelectorAll("script, style, link, .screenreader-only, .hidden-readable, .ui-icon").ToList())
            gone.Remove();

        foreach (var img in root.QuerySelectorAll("img.equation_image").ToList())
        {
            string? alt = img.GetAttribute("alt");
            string latex = img.GetAttribute("data-equation-content") is { Length: > 0 } eq ? eq
                : alt is { } text && text.StartsWith("LaTeX: ", StringComparison.Ordinal) ? text["LaTeX: ".Length..]
                : alt ?? "";
            img.ReplaceWith(document.CreateTextNode($"${latex}$"));
        }

        foreach (var frame in root.QuerySelectorAll("iframe[src]").ToList())
        {
            string src = Absolute(frame.GetAttribute("src") ?? "", ctx);
            string host = Uri.TryCreate(src, UriKind.Absolute, out var u) ? u.Host : src;
            string label = VideoHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))
                ? $"Video: {host}" : $"Embedded: {host}";
            var a = document.CreateElement("a");
            a.SetAttribute("href", src);
            a.TextContent = label;
            var p = document.CreateElement("p");
            p.AppendChild(a);
            frame.ReplaceWith(p);
        }

        if (ctx.BaseUrl.Length > 0)
        {
            foreach (var a in root.QuerySelectorAll("a[href]").ToList()) FixLink(a, "href", ctx, files);
            foreach (var img in root.QuerySelectorAll("img[src]").ToList()) FixLink(img, "src", ctx, files);
        }

        return root.InnerHtml;
    }

    /// <summary>A relative address, made absolute on <see cref="HtmlContext.BaseUrl"/>; an http(s) address already
    /// absolute, or one with no base to resolve against, is left alone. (<c>UriKind.Absolute</c> alone isn't enough:
    /// a root address like "/courses/4201/…" parses as an absolute <c>file:</c> URI on some platforms.)</summary>
    static string Absolute(string address, HtmlContext ctx)
    {
        if (address.Length == 0 || ctx.BaseUrl.Length == 0) return address;
        if (Uri.TryCreate(address, UriKind.Absolute, out var direct) && direct.Scheme is "http" or "https") return address;
        return Uri.TryCreate(new Uri(ctx.BaseUrl + "/"), address, out var abs) ? abs.ToString() : address;
    }

    static void FixLink(IElement el, string attr, HtmlContext ctx, Dictionary<long, CanvasFileLink> files)
    {
        string raw = el.GetAttribute(attr) ?? "";
        if (raw.Length == 0) return;
        string abs = Absolute(raw, ctx);
        el.SetAttribute(attr, abs);
        string endpoint = el.GetAttribute("data-api-endpoint") is { Length: > 0 } e ? Absolute(e, ctx) : "";
        var m = FileIdIn().Match(endpoint.Length > 0 ? endpoint : abs);
        if (!m.Success || !long.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out long id)) return;
        string api = endpoint.Length > 0 ? endpoint : $"{ctx.BaseUrl}/api/v1/courses/{ctx.CourseId}/files/{id}";
        files[id] = new CanvasFileLink(ctx.CourseId, id, api);
        if (ctx.Local(id) is { Length: > 0 } local) el.SetAttribute(attr, local);
    }

    /// <summary>Fix up any Canvas file links a Markdown text still points at Canvas for, now that more of
    /// <paramref name="local"/> is known (called again once a sync's downloads have finished).</summary>
    public static string ResolveLinks(string markdown, Func<long, string?> local) =>
        markdown.Length == 0 ? markdown : FileUrlInText().Replace(markdown,
            m => long.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out long id) && local(id) is { Length: > 0 } path ? path : m.Value);
}
