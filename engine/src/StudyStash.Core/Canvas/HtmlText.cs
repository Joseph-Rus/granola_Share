using System.Text.RegularExpressions;

namespace StudyStash.Core.Canvas;

/// <summary>Canvas's HTML (assignment instructions, pages, announcements) as plain Markdown.</summary>
public static partial class HtmlText
{
    static readonly ReverseMarkdown.Converter Converter = new(new ReverseMarkdown.Config
    {
        GithubFlavored = true,
        Tags = { Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass },
        Formatting = { RemoveComments = true },
        Links = { SmartHref = true },
    });

    [GeneratedRegex("<(script|style)\\b[^>]*>.*?</\\1\\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Code();

    [GeneratedRegex("\\n{3,}")]
    private static partial Regex Gaps();

    public static string ToMarkdown(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        string md;
        try
        {
            md = Converter.Convert(Code().Replace(html, ""));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException)
        {
            md = Tags().Replace(html, " "); // markup too broken to convert: keep the words
        }
        return Py.Strip(Gaps().Replace(md.ReplaceLineEndings("\n"), "\n\n"));
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
