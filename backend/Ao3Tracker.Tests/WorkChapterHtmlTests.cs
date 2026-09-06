using Ao3Tracker.Api.Services.Html;

namespace Ao3Tracker.Tests;

/// <summary>
/// The chapter sanitizer behind the in-app reader.
///
/// A chapter is the whole of an author's text as AO3 published it, arriving in a file this app
/// stored. It is rendered inside a logged-in session on this app's own origin, so the rule is the
/// summary's — an allowlist of elements — plus exactly three checked attributes a chapter cannot
/// read without: a link's address, an image's source and caption, and an id for a footnote to
/// point at. Everything else on an element is dropped whatever it is.
/// </summary>
public class WorkChapterHtmlTests
{
    [Fact]
    public void Keeps_what_a_chapter_is_made_of()
    {
        var safe = WorkChapterHtml.Sanitize(
            "<h2>Chapter 1</h2><div><p>They <em>meet</em>.</p><hr><blockquote>a line</blockquote></div>");

        Assert.Equal(
            "<h2>Chapter 1</h2><div><p>They <em>meet</em>.</p><hr><blockquote>a line</blockquote></div>",
            safe);
    }

    [Fact]
    public void Takes_a_whole_xhtml_document_and_keeps_its_body()
    {
        var safe = WorkChapterHtml.Sanitize(EpubFixture.Xhtml("Chapter 1", "<p>words</p>"));

        Assert.Equal("<p>words</p>", safe);
    }

    [Fact]
    public void Keeps_an_absolute_web_link_and_sends_it_to_a_new_tab()
    {
        var safe = WorkChapterHtml.Sanitize("<p><a href=\"https://example.com/art?x=1&amp;y=2\">art</a></p>");

        Assert.Equal(
            "<p><a href=\"https://example.com/art?x=1&amp;y=2\" target=\"_blank\" rel=\"noopener noreferrer\">art</a></p>",
            safe);
    }

    [Fact]
    public void Keeps_a_footnote_link_and_the_id_it_points_at()
    {
        var safe = WorkChapterHtml.Sanitize(
            "<p>text<sup><a href=\"#note1\">1</a></sup></p><p id=\"note1\">the note</p>");

        Assert.Equal("<p>text<sup><a href=\"#note1\">1</a></sup></p><p id=\"note1\">the note</p>", safe);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData(" javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("vbscript:x")]
    [InlineData("/works/1")]
    [InlineData("chapter2.xhtml")]
    [InlineData("#not a token")]
    [InlineData("#")]
    public void Drops_a_link_address_that_is_not_a_web_address_or_a_footnote(string href)
    {
        var safe = WorkChapterHtml.Sanitize($"<p><a href=\"{href}\">here</a></p>");

        // The anchor stays as an element — its words are the author's — but points nowhere.
        Assert.Equal("<p><a>here</a></p>", safe);
    }

    [Fact]
    public void Keeps_an_image_with_a_web_source_and_its_caption()
    {
        var safe = WorkChapterHtml.Sanitize(
            "<p><img src=\"https://i.example.com/a.png\" alt=\"a &quot;drawing&quot;\" width=\"9\" onerror=\"x()\"></p>");

        Assert.Equal("<p><img src=\"https://i.example.com/a.png\" alt=\"a &quot;drawing&quot;\"></p>", safe);
    }

    [Theory]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("images/a.png")]
    [InlineData("")]
    public void Drops_an_image_with_no_web_source(string src)
    {
        var safe = WorkChapterHtml.Sanitize($"<p>before<img src=\"{src}\" alt=\"x\">after</p>");

        Assert.Equal("<p>beforeafter</p>", safe);
    }

    [Fact]
    public void An_image_alone_is_a_chapter_and_not_an_empty_one()
    {
        var safe = WorkChapterHtml.Sanitize("<div><img src=\"https://i.example.com/a.png\"></div>");

        Assert.Equal("<div><img src=\"https://i.example.com/a.png\" alt=\"\"></div>", safe);
    }

    [Fact]
    public void Drops_every_other_attribute()
    {
        var safe = WorkChapterHtml.Sanitize(
            "<p style=\"position:fixed\" class=\"x\" onclick=\"y()\" data-a=\"b\" title=\"t\">words</p>"
            + "<span style=\"color:red\">red</span>");

        Assert.Equal("<p>words</p><span>red</span>", safe);
    }

    [Theory]
    [InlineData("not a token")]
    [InlineData("a/b")]
    [InlineData("")]
    public void Drops_an_id_that_is_not_a_plain_token(string id)
    {
        var safe = WorkChapterHtml.Sanitize($"<p id=\"{id}\">words</p>");

        Assert.Equal("<p>words</p>", safe);
    }

    [Fact]
    public void Drops_script_and_style_with_everything_inside_them()
    {
        var safe = WorkChapterHtml.Sanitize(
            "<style>p{display:none}</style><p>hi</p><script>alert('x')</script><iframe src=\"x\"></iframe>");

        Assert.Equal("<p>hi</p>", safe);
    }

    [Fact]
    public void Unwraps_an_element_a_chapter_has_no_use_for()
    {
        var safe = WorkChapterHtml.Sanitize("<p><font color=\"red\">red</font> <marquee>go</marquee></p>");

        Assert.Equal("<p>red go</p>", safe);
    }

    [Fact]
    public void Keeps_a_table()
    {
        var safe = WorkChapterHtml.Sanitize(
            "<table border=\"1\"><thead><tr><th>a</th></tr></thead><tbody><tr><td>b</td></tr></tbody></table>");

        Assert.Equal("<table><thead><tr><th>a</th></tr></thead><tbody><tr><td>b</td></tr></tbody></table>", safe);
    }

    [Fact]
    public void Survives_markup_nested_deeper_than_anyone_means_to_read()
    {
        var deep = string.Concat(Enumerable.Repeat("<div>", 5_000))
            + "still here"
            + string.Concat(Enumerable.Repeat("</div>", 5_000));

        var safe = WorkChapterHtml.Sanitize(deep);

        Assert.NotNull(safe);
        Assert.Contains("still here", safe);
        Assert.Equal(HtmlAllowlist.MaxDepth, safe.Split("<div>").Length - 1);
    }

    [Fact]
    public void Reports_nothing_for_a_chapter_of_nothing()
    {
        Assert.Null(WorkChapterHtml.Sanitize("<div><span>  </span></div>"));
        Assert.Null(WorkChapterHtml.Sanitize(""));
        Assert.Null(WorkChapterHtml.Sanitize(null));
    }
}
