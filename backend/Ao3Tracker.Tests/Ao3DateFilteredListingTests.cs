using System.Globalization;
using AngleSharp.Html.Parser;
using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// What AO3 serves for a tag listing narrowed to recently updated works and sorted by posting
/// date — the request a re-read of a ship's recent works is built on, held by captures before any
/// of it is written.
///
/// <para>The re-read has to walk a stable order, for the reason the full sweep asks for
/// <c>created_at</c>: under <c>revised_at</c> a work edited mid-walk jumps to page 1, which the walk
/// has already passed, and is concluded to have left the tag. So it needs the listing's
/// <c>work_search[date_from]</c> bound, which sits under "Date Updated" in the filter form, to keep
/// narrowing by revision when the sort is by posting. Nothing in the app had ever sent the two
/// together.</para>
///
/// <para><b>The evidence is three pages of one result set</b>: <c>/tags/Clarke Griffin*s*Lexa/works</c>
/// with <c>date_from=2026-06-13</c>, captured logged out on 2026-09-12 between 01:31 and 01:33 UTC —
/// page 1 and the last page (9) by posting date, and page 1 by revision date.</para>
///
/// <para><b>Conclusions.</b> The bound narrows by revision under either sort: both orders count the
/// same 172 works, and the posting-date walk ends on works posted years before the window. The
/// posting-date order is newest work id first and pages cleanly. And the date a re-read can compare
/// against the window is the blurb's <i>visible</i> date, not the <c>updated_at</c> comment
/// <see cref="Ao3BlurbParser"/> prefers for <c>Work.UpdatedAt</c>: only the visible date follows the
/// listing's revision order, and <c>updated_at</c> runs days ahead of it on some works. On the
/// production library at the time, 358 unrestricted works under this ship had an <c>UpdatedAt</c>
/// inside the window AO3 counted 172 in — so a re-read deciding absence from that column would
/// have recorded about half of them as having left the tag.</para>
/// </summary>
public class Ao3DateFilteredListingTests
{
    private static readonly DateOnly WindowStart = new(2026, 6, 13);

    private static readonly string PostedFirstHtml = Fixtures.Load(Fixtures.FilteredByPostedFirstPage);
    private static readonly string PostedLastHtml = Fixtures.Load(Fixtures.FilteredByPostedLastPage);
    private static readonly string UpdatedFirstHtml = Fixtures.Load(Fixtures.FilteredByUpdatedFirstPage);

    private static readonly Ao3ListingPage PostedFirst = Ao3BlurbParser.ParseListing(PostedFirstHtml);
    private static readonly Ao3ListingPage PostedLast = Ao3BlurbParser.ParseListing(PostedLastHtml);
    private static readonly Ao3ListingPage UpdatedFirst = Ao3BlurbParser.ParseListing(UpdatedFirstHtml);

    [Fact]
    public void Both_sorts_count_the_same_result_set()
    {
        // The first half of "the bound survives the sort". A bound that switched to posting date
        // under created_at would count the works posted since June instead, a different number.
        Assert.Equal(172, PostedFirst.TotalWorks);
        Assert.Equal(172, PostedLast.TotalWorks);
        Assert.Equal(172, UpdatedFirst.TotalWorks);
    }

    [Fact]
    public void The_posting_date_walk_ends_on_old_works_revised_inside_the_window()
    {
        // The second half, and the one a count alone cannot give. Work ids are assigned as works are
        // posted, so id 7,995,769 was posted years before 2026 — it is on this listing only because
        // it was revised after the bound.
        Assert.Contains(PostedLast.Works, w => w.WorkId == 7_995_769);

        Assert.All(
            VisibleDates(PostedFirstHtml).Concat(VisibleDates(PostedLastHtml)).Concat(VisibleDates(UpdatedFirstHtml)),
            date => Assert.True(date >= WindowStart, $"{date} is before the window"));
    }

    [Fact]
    public void Posting_date_order_is_newest_work_id_first_and_pages_to_an_end()
    {
        var first = PostedFirst.Works.Select(w => w.WorkId).ToList();
        var last = PostedLast.Works.Select(w => w.WorkId).ToList();

        Assert.Equal(first.OrderByDescending(id => id), first);
        Assert.Equal(last.OrderByDescending(id => id), last);
        Assert.True(last.Max() < first.Min());

        // Eight full pages and twelve works: the heading and the walk agree, and the last page says
        // so by offering no next one.
        Assert.True(PostedFirst.HasNextPage);
        Assert.False(PostedLast.HasNextPage);
        Assert.Equal(20, PostedFirst.Works.Count);
        Assert.Equal(172, 8 * 20 + PostedLast.Works.Count);
    }

    [Fact]
    public void The_visible_date_follows_the_revision_order_and_updated_at_does_not()
    {
        // Sorted by revised_at, the visible dates never rise down the page...
        var visible = VisibleDates(UpdatedFirstHtml);
        Assert.Equal(visible.OrderByDescending(d => d), visible);

        // ...and the updated_at readings the parser stores do.
        var stored = UpdatedFirst.Works.Select(w => w.UpdatedAt).ToList();
        Assert.NotEqual(stored.OrderByDescending(d => d), stored);
    }

    [Fact]
    public void Updated_at_can_run_days_ahead_of_the_visible_date()
    {
        // Work 91921281: revised 3 September by its visible date, updated_at the 11th. A window
        // compared against updated_at holds this work eight days longer than AO3's bound does.
        var work = PostedFirst.Works.Single(w => w.WorkId == 91_921_281);
        var shown = VisibleDateOf(PostedFirstHtml, 91_921_281);

        Assert.Equal(new DateOnly(2026, 9, 3), shown);
        Assert.Equal(new DateOnly(2026, 9, 11), DateOnly.FromDateTime(work.UpdatedAt));
    }

    private static List<DateOnly> VisibleDates(string html) =>
        Blurbs(html).Select(b => b.Visible).ToList();

    private static DateOnly VisibleDateOf(string html, long workId) =>
        Blurbs(html).Single(b => b.WorkId == workId).Visible;

    /// <summary>
    /// Each blurb's id and visible date, read straight off the markup: the parser does not keep the
    /// visible date once it has the <c>updated_at</c> comment, which is the gap these tests are about.
    /// </summary>
    private static IEnumerable<(long WorkId, DateOnly Visible)> Blurbs(string html) =>
        new HtmlParser().ParseDocument(html)
            .QuerySelectorAll("ol.work.index.group > li[id^='work_']")
            .Select(li => (
                long.Parse(li.Id!["work_".Length..], CultureInfo.InvariantCulture),
                DateOnly.ParseExact(
                    li.QuerySelector("p.datetime")!.TextContent.Trim(), "d MMM yyyy", CultureInfo.InvariantCulture)));
}
