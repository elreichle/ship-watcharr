using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// What a work's own page says that a listing blurb does not: when the work was published, and its
/// whole tag list rather than the abbreviated one a blurb prints.
///
/// Every expectation here is read out of <c>ao3-work-page.html</c> — the capture the parser was
/// written against — rather than from a shape this test invented. A parser and a test that agree
/// on remembered markup agree with each other and with nothing else.
/// </summary>
public class Ao3WorkPageParserTests
{
    private static Ao3WorkPage Captured() => Ao3WorkPageParser.Parse(Fixtures.Load(Fixtures.WorkPage));

    [Fact]
    public void Reads_the_published_date_the_captured_page_prints()
    {
        var page = Captured();

        // "Published: 2025-09-06", day-granular because that is all AO3 prints. UTC because every
        // timestamp this application stores is — see the spec's DateTime rule.
        Assert.Equal(new DateTime(2025, 9, 6, 0, 0, 0, DateTimeKind.Utc), page.PublishedAt);
        Assert.Equal(DateTimeKind.Utc, page.PublishedAt!.Value.Kind);
    }

    [Fact]
    public void Reads_a_page_it_recognises_as_a_work_page()
    {
        Assert.True(Captured().IsWorkPage);
        Assert.Equal(0, Captured().ParseWarnings);
    }

    /// <summary>
    /// One case per member of <see cref="Ao3TagType"/>, counted off the capture. The counts are the
    /// assertion that matters: the point of reading a work's own page is that it carries tags the
    /// blurb truncated, and a parser that found the first of each type would pass a test that only
    /// asked whether a relationship was read.
    /// </summary>
    [Theory]
    [InlineData(Ao3TagType.Warning, 1)]
    [InlineData(Ao3TagType.Fandom, 1)]
    [InlineData(Ao3TagType.Relationship, 1)]
    [InlineData(Ao3TagType.Character, 6)]
    [InlineData(Ao3TagType.Freeform, 22)]
    public void Reads_every_tag_of_type(Ao3TagType type, int expected)
    {
        Assert.Equal(expected, Captured().Tags.Count(t => t.Type == type));
    }

    [Fact]
    public void Reads_the_tags_by_the_names_the_page_shows()
    {
        var tags = Captured().Tags;

        Assert.Contains(tags, t => t.Type == Ao3TagType.Fandom && t.Name == "KPop Demon Hunters (2025)");
        Assert.Contains(
            tags, t => t.Type == Ao3TagType.Relationship && t.Name == "Mira/Rumi/Zoey (KPop Demon Hunters)");
        Assert.Contains(tags, t => t.Type == Ao3TagType.Warning && t.Name == "No Archive Warnings Apply");

        // Entity-decoded, because the name is what goes in the shared Tags table and "Cuddling &amp;
        // Snuggling" is not a tag anybody typed.
        Assert.Contains(tags, t => t.Type == Ao3TagType.Freeform && t.Name == "Cuddling & Snuggling");
    }

    /// <summary>
    /// The rating and the categories are on the page in the same <c>dd.*.tags</c> shape as the rest,
    /// and are deliberately not read as tags: <see cref="Ao3TagType"/> has no member for either, and
    /// both are already columns on <see cref="Work"/> written from the blurb. Inventing a type for
    /// them would put rows in the shared tag table that no filter, no listing and no page can use.
    /// </summary>
    [Theory]
    [InlineData("Explicit")]
    [InlineData("F/F")]
    [InlineData("Multi")]
    public void Does_not_read_as_a_tag(string name)
    {
        Assert.DoesNotContain(Captured().Tags, t => t.Name == name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body><p>Sorry, we couldn't find that.</p></body></html>")]
    public void A_document_that_is_not_a_work_page_is_not_read_as_one(string? html)
    {
        var page = Ao3WorkPageParser.Parse(html);

        // Nothing may be written from it — which is the whole of what IsWorkPage exists to say. A
        // page with no meta block read as an empty tag list would delete every tag the work has.
        Assert.False(page.IsWorkPage);
        Assert.Empty(page.Tags);
        Assert.Null(page.PublishedAt);
    }

    [Fact]
    public void A_listing_is_not_a_work_page()
    {
        // The near miss, and the reason the container is required rather than assumed: a listing
        // carries `a.tag` anchors by the dozen, one blurb's worth per work.
        Assert.False(Ao3WorkPageParser.Parse(Fixtures.Load(Fixtures.AuthenticatedListing)).IsWorkPage);
    }

    [Fact]
    public void A_work_page_missing_its_published_date_still_yields_its_tags()
    {
        // The capture with that one <dd> taken out. Every field is independently optional, the same
        // rule Ao3BlurbParser is written to: a page AO3 has reshaped costs a warning, not the tags
        // it was still readable enough to carry.
        var html = Fixtures.Load(Fixtures.WorkPage)
            .Replace(
                """<dt class="published">Published:</dt><dd class="published">2025-09-06</dd>""",
                "",
                StringComparison.Ordinal);

        var page = Ao3WorkPageParser.Parse(html);

        Assert.True(page.IsWorkPage);
        Assert.Null(page.PublishedAt);
        Assert.Equal(1, page.ParseWarnings);
        Assert.Equal(31, page.Tags.Count);
    }

    [Fact]
    public void A_work_page_carrying_no_tags_at_all_says_so()
    {
        // AO3 does not serve this — a work must have a fandom — so it is a markup change, and the
        // ingestor must be able to tell it from a work whose tags were read.
        var html = Fixtures.Load(Fixtures.WorkPage)
            .Replace("class=\"tag\"", "class=\"nothing\"", StringComparison.Ordinal);

        var page = Ao3WorkPageParser.Parse(html);

        Assert.True(page.IsWorkPage);
        Assert.Empty(page.Tags);
        Assert.Equal(1, page.ParseWarnings);
    }

    [Fact]
    public void The_same_tag_printed_twice_is_read_once()
    {
        // (WorkId, TagId) is a primary key, so a duplicate is not a cosmetic problem: it fails the
        // save for the whole work. Ao3BlurbParser de-duplicates for the same reason.
        var html = Fixtures.Load(Fixtures.WorkPage)
            .Replace(
                """<li><a class="tag" href="https://archiveofourown.org/tags/Post-Canon/works">Post-Canon</a></li>""",
                """
                <li><a class="tag" href="https://archiveofourown.org/tags/Post-Canon/works">Post-Canon</a></li>
                <li><a class="tag" href="https://archiveofourown.org/tags/Post-Canon/works">post-canon</a></li>
                """,
                StringComparison.Ordinal);

        var page = Ao3WorkPageParser.Parse(html);

        Assert.Equal(31, page.Tags.Count);
        Assert.Single(page.Tags, t => t.Name.Equals("Post-Canon", StringComparison.OrdinalIgnoreCase));
    }
}
