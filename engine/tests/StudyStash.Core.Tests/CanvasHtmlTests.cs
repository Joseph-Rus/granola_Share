using StudyStash.Core.Canvas;

namespace StudyStash.Core.Tests;

/// <summary>Canvas's HTML as Markdown, rule by rule: what <see cref="HtmlText.Convert"/> drops, rewrites and keeps.</summary>
public class CanvasHtmlTests
{
    static HtmlContext Ctx(Func<long, string?>? local = null) => new("https://canvas.test", 4201, local ?? (_ => null));

    [Fact]
    public void A_screen_reader_only_span_on_an_external_link_is_dropped()
    {
        var (md, _) = HtmlText.Convert(
            """<p>Read the <a href="https://example.com/x">course policy</a><span class="screenreader-only"> (Links to an external site.)</span>.</p>""",
            Ctx());
        Assert.Contains("course policy", md);
        Assert.DoesNotContain("Links to an external site", md);
    }

    [Fact]
    public void An_equation_image_becomes_LaTeX_from_its_content_attribute()
    {
        var (md, _) = HtmlText.Convert(
            """<p>Solve <img class="equation_image" data-equation-content="x^2 + 1" src="https://canvas.test/equation_images/x.gif" alt="LaTeX: x^2 + 1"> for x.</p>""",
            Ctx());
        Assert.Contains("$x^2 + 1$", md);
    }

    [Fact]
    public void An_equation_image_without_the_content_attribute_falls_back_to_its_alt_text()
    {
        var (md, _) = HtmlText.Convert("""<img class="equation_image" src="https://canvas.test/equation_images/y.gif" alt="LaTeX: y = mx + b">""", Ctx());
        Assert.Contains("$y = mx + b$", md);
    }

    [Fact]
    public void A_youtube_iframe_becomes_a_video_link()
    {
        var (md, _) = HtmlText.Convert("""<iframe src="https://www.youtube.com/embed/abc123" allowfullscreen></iframe>""", Ctx());
        Assert.Contains("[Video: www.youtube.com](https://www.youtube.com/embed/abc123)", md);
    }

    [Fact]
    public void Some_other_iframe_becomes_an_embedded_link()
    {
        var (md, _) = HtmlText.Convert("""<iframe src="https://canvas.test/media/xyz"></iframe>""", Ctx());
        Assert.Contains("[Embedded: canvas.test](https://canvas.test/media/xyz)", md);
    }

    [Fact]
    public void A_relative_link_is_made_absolute_on_the_course_s_canvas()
    {
        var (md, _) = HtmlText.Convert("""<p>See the <a href="/courses/4201/assignments/9001">first lab</a>.</p>""", Ctx());
        Assert.Contains("(https://canvas.test/courses/4201/assignments/9001)", md);
    }

    [Fact]
    public void A_link_with_no_base_to_resolve_against_is_left_relative()
    {
        string md = HtmlText.ToMarkdown("""<a href="/courses/4201/assignments/9001">first lab</a>""");
        Assert.Contains("(/courses/4201/assignments/9001)", md);
    }

    [Fact]
    public void A_canvas_file_link_is_collected_and_rewritten_once_its_local_copy_is_known()
    {
        var (md, files) = HtmlText.Convert("""<p>See the <a href="/courses/4201/files/555">slides</a>.</p>""", Ctx(id => id == 555 ? "files/recursion-slides.pdf" : null));
        var link = Assert.Single(files);
        Assert.Equal((4201L, 555L), (link.CourseId, link.FileId));
        Assert.Equal("https://canvas.test/api/v1/courses/4201/files/555", link.ApiUrl);
        Assert.Contains("(files/recursion-slides.pdf)", md);
    }

    [Fact]
    public void A_canvas_file_link_not_yet_known_stays_on_canvas_and_is_still_collected()
    {
        var (md, files) = HtmlText.Convert("""<img src="/courses/4201/files/556/preview">""", Ctx());
        Assert.Equal(556L, Assert.Single(files).FileId);
        Assert.Contains("https://canvas.test/courses/4201/files/556/preview", md);
    }

    [Fact]
    public void A_data_api_endpoint_names_the_file_when_the_href_does_not_look_like_one()
    {
        var (md, files) = HtmlText.Convert(
            """<a href="https://canvas.test/courses/4201/module_item_redirect/3021" data-api-endpoint="https://canvas.test/api/v1/courses/4201/files/555" data-api-returntype="File">recursion-slides.pdf</a>""",
            Ctx());
        var link = Assert.Single(files);
        Assert.Equal(555L, link.FileId);
        Assert.Contains("recursion-slides.pdf", md);
    }

    [Fact]
    public void A_table_survives()
    {
        var (md, _) = HtmlText.Convert("<table><tr><th>Part</th><th>Points</th></tr><tr><td>Traces</td><td>10</td></tr></table>", Ctx());
        Assert.Contains("Part", md);
        Assert.Contains("Points", md);
        Assert.Contains("Traces", md);
        Assert.Contains("10", md);
        Assert.Contains("|", md);
    }

    [Fact]
    public void A_preformatted_block_keeps_its_code()
    {
        var (md, _) = HtmlText.Convert("<pre>def fact(n):\n    return 1 if n &lt;= 1 else n * fact(n - 1)</pre>", Ctx());
        Assert.Contains("def fact(n):", md);
        Assert.Contains("return 1 if n <= 1 else n * fact(n - 1)", md);
    }

    [Fact]
    public void Broken_markup_still_keeps_the_words()
    {
        var (md, _) = HtmlText.Convert("<p>Hand in <b>one PDF</b <p>with both traces and <i>show your work", Ctx());
        Assert.Contains("Hand in", md);
        Assert.Contains("one PDF", md);
        Assert.Contains("with both traces", md);
        Assert.Contains("show your work", md);
    }

    [Fact]
    public void Empty_or_missing_html_is_just_empty()
    {
        Assert.Equal("", HtmlText.ToMarkdown(null));
        Assert.Equal("", HtmlText.ToMarkdown(""));
        Assert.Equal("", HtmlText.ToMarkdown("   "));
    }
}
