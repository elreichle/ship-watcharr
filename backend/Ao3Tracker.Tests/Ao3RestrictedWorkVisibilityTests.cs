using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// Whether AO3 shows a restricted work to a request carrying no session — held by a capture rather
/// than by a comment.
///
/// <para>The premise is load-bearing in two places and was verified in neither.
/// <c>Ao3ShipIndexScraper</c> warns when an unauthenticated response carries a restricted blurb, on
/// the belief that this cannot happen; and <c>Ship.LastKnownTotalWasAuthenticated</c> exists
/// entirely because an anonymous total is expected to be short. If restricted works were merely
/// *marked* rather than *withheld*, the first is noise on most tags and the second is
/// bookkeeping for a difference that does not exist.</para>
///
/// <para><b>The evidence is a matched pair</b>: one URL —
/// <c>/tags/Clarke Griffin*s*Lexa/works</c> — captured twice on 2026-08-29, minutes apart, once
/// logged out and once with a session, same sort (Date Updated), same filter state. Neither page's
/// blurbs carry a restricted marker at all, which is exactly why the *totals* are the evidence and
/// the markup is not: there is nothing to mark, because the works are not on the page.</para>
///
/// <para>The filter sidebar settles what a single total could not. Every facet count rises with a
/// session, proportionally, across ratings, warnings, categories and fandoms alike — F/F 11,822 →
/// 13,220, The 100 (TV) 12,196 → 13,640, Explicit 1,539 → 1,762. A different sort or filter moves
/// one facet. A uniform ~10% lift across all of them is a set of works withheld wholesale from the
/// anonymous request, and withheld from its counts too. (The sidebar is not parsed by anything in
/// this app, so that comparison lives here in prose rather than as a fourth test.)</para>
///
/// <para><b>Conclusion: the premise holds.</b> An anonymous listing does not show restricted works,
/// so the scraper's warning is unreachable in practice and stays as the invariant check it is.</para>
/// </summary>
public class Ao3RestrictedWorkVisibilityTests
{
    private static readonly Ao3ListingPage Anonymous =
        Ao3BlurbParser.ParseListing(Fixtures.Load(Fixtures.AnonymousListing));

    private static readonly Ao3ListingPage Authenticated =
        Ao3BlurbParser.ParseListing(Fixtures.Load(Fixtures.AuthenticatedListing));

    [Fact]
    public void An_anonymous_listing_carries_no_restricted_work()
    {
        // The literal question the task asked. Weak on its own — a page of twenty works from a tag
        // with none in it would pass too — which is what the next two tests are for.
        Assert.NotEmpty(Anonymous.Works);
        Assert.DoesNotContain(Anonymous.Works, w => w.IsRestricted);
    }

    [Fact]
    public void A_session_is_shown_works_an_anonymous_request_is_not()
    {
        // 1,451 works, 10.6% of the tag, visible only with a session. This is the number
        // LastKnownTotalWasAuthenticated exists to keep honest: recording 12,285 as if it were the
        // tag's size is not a rounding error.
        Assert.Equal(12_285, Anonymous.TotalWorks);
        Assert.Equal(13_736, Authenticated.TotalWorks);
    }

    [Fact]
    public void The_two_captures_differ_by_what_is_withheld_not_by_how_they_are_sorted()
    {
        // The alternative reading of the totals above is that the two requests were served
        // different listings — a different sort, or a filter the session applied. They were not:
        // page 1 is the same twenty works in the same order. So the missing 1,451 are absent from
        // the anonymous listing rather than pushed further down it.
        Assert.Equal(
            Authenticated.Works.Select(w => w.WorkId),
            Anonymous.Works.Select(w => w.WorkId));
    }
}
