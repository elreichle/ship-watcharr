using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// What AO3 actually serves for a works index that matched nothing.
///
/// Two stopping rules rest on this page's shape and neither had a test over real markup.
/// <c>HasListing</c> is what tells an empty tag from a response that is not a results page at all,
/// on the premise — written in its doc, verified nowhere — that a zero-result index still renders
/// the container; and <c>PlausiblyTheEndOfTheListing</c> reads the heading beside it to decide
/// whether a walk may conclude. Every scraper test reaching the zero-work path went through the
/// <c>Page(n, [])</c> helper, which emits the container unconditionally: the tests proved the
/// premise by assuming it. Two in <c>Ao3ShipIndexScraperTests</c> now walk this capture instead.
///
/// The fixture is the case that matters most rather than the easiest one — a *filtered* request
/// (<c>work_search[date_from]=2026-08-31</c>) against a populated tag, which is the shape of every
/// quiet incremental pass. Had the container been absent, `HasListing` would have been false on
/// every tick of every ship nobody is writing for, the run would have stopped with
/// <see cref="ScrapeStopReason.Error"/>, and the watermark would never have moved again.
/// </summary>
public class Ao3EmptyListingTests
{
    private static readonly string Capture = Fixtures.Load(Fixtures.EmptyListing);

    [Fact]
    public void Renders_the_results_container_even_with_nothing_in_it()
    {
        // The premise HasListing is written on, and the one this whole task existed to check.
        Assert.True(Ao3BlurbParser.ParseListing(Capture).HasListing);
    }

    [Fact]
    public void Carries_no_works_and_nothing_it_could_not_read()
    {
        var page = Ao3BlurbParser.ParseListing(Capture);

        Assert.Empty(page.Works);

        // Zero blurbs is the healthy reading of this page, not a parse failure. A warning here
        // would mean the container held something the parser could not name, which is the one
        // reading that must not be confused with an empty result set.
        Assert.Equal(0, page.ParseWarnings);
    }

    [Fact]
    public void Names_its_results_container_with_the_work_class()
    {
        // Ao3BlurbParser looks for `ol.work.index.group` and falls back to a bare `ol.index.group`,
        // guessing that a zero-result listing might be paged by the second. It is not: the capture
        // carries the same class list a populated page does, so the fallback is defence against a
        // markup change rather than a selector this case depends on.
        Assert.Contains("""<ol class="work index group">""", Capture, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_its_empty_result_set_in_the_heading()
    {
        // "0 Works in <tag>", which is evidence of an empty result set rather than the absence of
        // evidence a page with no heading at all leaves behind. PlausiblyTheEndOfTheListing is
        // allowed to conclude on the first, and refuses to on the second.
        Assert.Equal(0, Ao3BlurbParser.ParseListing(Capture).TotalWorks);
    }

    [Fact]
    public void Offers_no_next_page_to_walk_to()
    {
        // AO3 renders no pagination at all here, rather than a Next link onto a second empty page.
        Assert.False(Ao3BlurbParser.ParseListing(Capture).HasNextPage);
    }
}
