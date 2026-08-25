using Ao3Tracker.Api.Services.Html;

namespace Ao3Tracker.Tests;

/// <summary>
/// The summary sanitizer behind the work detail page.
///
/// <c>Work.SummaryHtml</c> is markup an anonymous stranger typed into AO3 and this app stored
/// verbatim. The detail view is the first place it is rendered as HTML rather than dropped, so this
/// is where "a summary cannot run script in a reader's session" is decided. The rule the tests
/// below pin is deliberately blunt: an allowlist of element names, <b>no attributes at all</b>, and
/// anything else unwrapped to its text — a rule with no attribute-value parsing in it has nowhere
/// for a <c>javascript:</c> URL or a stray event handler to hide.
/// </summary>
public class WorkDetailSummaryTests
{
    [Fact]
    public void Keeps_the_formatting_a_summary_is_written_with()
    {
        var safe = WorkSummaryHtml.Sanitize(
            "<p>They meet in the <em>woods</em>.</p><p>Then <strong>everything</strong> changes.</p>");

        Assert.Equal(
            "<p>They meet in the <em>woods</em>.</p><p>Then <strong>everything</strong> changes.</p>",
            safe);
    }

    [Fact]
    public void Keeps_lists_and_quotes()
    {
        var safe = WorkSummaryHtml.Sanitize(
            "<blockquote>a line</blockquote><ul><li>one</li><li>two</li></ul>");

        Assert.Equal("<blockquote>a line</blockquote><ul><li>one</li><li>two</li></ul>", safe);
    }

    [Fact]
    public void Drops_a_script_and_everything_inside_it()
    {
        // Not merely the tags: unwrapping a script leaves its source as text, which reads as
        // gibberish at best and, in a context that re-parses it, as the very thing being removed.
        var safe = WorkSummaryHtml.Sanitize("<p>hi</p><script>alert('x')</script>");

        Assert.Equal("<p>hi</p>", safe);
        Assert.DoesNotContain("alert", safe);
    }

    [Fact]
    public void Drops_a_style_block_and_everything_inside_it()
    {
        var safe = WorkSummaryHtml.Sanitize("<style>body { display: none }</style><p>hi</p>");

        Assert.Equal("<p>hi</p>", safe);
    }

    [Theory]
    [InlineData("<img src=x onerror=\"alert(1)\">")]
    [InlineData("<iframe src=\"https://evil.test\"></iframe>")]
    [InlineData("<object data=\"evil.swf\"></object>")]
    [InlineData("<svg><script>alert(1)</script></svg>")]
    public void Drops_the_elements_a_summary_has_no_business_carrying(string markup)
    {
        Assert.Null(WorkSummaryHtml.Sanitize(markup));
    }

    [Fact]
    public void Keeps_no_attribute_even_on_an_element_it_allows()
    {
        // The allowlist is on names only. An attribute kept "because this element is fine" is how
        // style, event handlers and URLs get back in one at a time.
        var safe = WorkSummaryHtml.Sanitize(
            "<p onclick=\"steal()\" style=\"position:fixed\" class=\"x\">text</p>");

        Assert.Equal("<p>text</p>", safe);
    }

    [Fact]
    public void Keeps_a_links_words_and_not_its_destination()
    {
        // Unwrapped rather than dropped: the words are the author's summary. Keeping the anchor
        // would mean deciding which URL schemes are safe, and javascript: is only the best known of
        // those; keeping none means never having to be right about that list.
        var safe = WorkSummaryHtml.Sanitize("<p>read <a href=\"javascript:alert(1)\">this</a></p>");

        Assert.Equal("<p>read this</p>", safe);
    }

    [Fact]
    public void Escapes_text_that_is_trying_to_be_markup()
    {
        var safe = WorkSummaryHtml.Sanitize("<p>if x &lt; y &amp; y &gt; z</p>");

        Assert.Equal("<p>if x &lt; y &amp; y &gt; z</p>", safe);
    }

    [Fact]
    public void Escapes_a_stray_angle_bracket_the_archive_never_closed()
    {
        var safe = WorkSummaryHtml.Sanitize("<p>5 < 6</p>");

        Assert.NotNull(safe);
        Assert.DoesNotContain("< 6", safe);
        Assert.Contains("&lt;", safe);
    }

    [Fact]
    public void Closes_what_the_summary_left_open()
    {
        // Half a summary must not be able to swallow the page around it.
        var safe = WorkSummaryHtml.Sanitize("<p>unclosed");

        Assert.Equal("<p>unclosed</p>", safe);
    }

    [Fact]
    public void Drops_comments()
    {
        Assert.Equal("<p>hi</p>", WorkSummaryHtml.Sanitize("<p>hi</p><!-- hidden -->"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Reports_nothing_for_a_work_with_no_summary(string? html)
    {
        Assert.Null(WorkSummaryHtml.Sanitize(html));
    }

    [Fact]
    public void Reports_nothing_for_markup_that_sanitizes_away_to_emptiness()
    {
        // A summary that was only a tracking pixel is a work with no summary, and saying so lets
        // the page render its "no summary" line rather than an empty box.
        Assert.Null(WorkSummaryHtml.Sanitize("<img src=\"https://evil.test/p.gif\">"));
    }

    [Fact]
    public void Survives_markup_nested_deeper_than_anyone_means_to_read()
    {
        // A summary is untrusted input, so its nesting depth is untrusted input too. Whatever this
        // does with the tags, it must return rather than take the request down with a stack it ran
        // out of, and it must not lose the words.
        var deep = string.Concat(Enumerable.Repeat("<em>", 5_000))
            + "still here"
            + string.Concat(Enumerable.Repeat("</em>", 5_000));

        var safe = WorkSummaryHtml.Sanitize(deep);

        Assert.NotNull(safe);
        Assert.Contains("still here", safe);
    }
}
