using System.Net;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// The pass that reads works' own pages: which works it asks for, what it writes from what came
/// back, and what it declines to write.
///
/// Against the real ingestor and a real SQLite database, because every rule here is about rows —
/// which work is still in the backlog, which tag survived, what a 404 settles. The archive is the
/// fixture's fake one, serving the captured work page.
/// </summary>
public class Ao3WorkPageDetailFetchTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";

    /// <summary>The work <c>ao3-work-page.html</c> was captured from.</summary>
    private const long Captured = 70441196;

    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Records_the_published_date_a_listing_blurb_never_carried()
    {
        var shipId = await FollowAsync();
        await IngestAsync(shipId, Captured);
        ServeTheCapturedWorkPage();

        var result = await _host.FetchWorkDetailsAsync();

        await using var db = _host.NewContext();
        var work = await db.Works.SingleAsync(w => w.Id == Captured);

        Assert.Equal(new DateTime(2025, 9, 6, 0, 0, 0, DateTimeKind.Utc), work.PublishedAt);
        Assert.Equal(_host.Clock.Now.UtcDateTime, work.DetailFetchedAt);
        Assert.Equal(new WorkDetailPassResult(1, 1, 1, 0, 1, 0), result);
    }

    [Fact]
    public async Task Records_the_tags_the_blurb_left_out()
    {
        var shipId = await FollowAsync();
        await IngestAsync(shipId, Captured);
        ServeTheCapturedWorkPage();

        await _host.FetchWorkDetailsAsync();

        await using var db = _host.NewContext();
        var tags = await db.WorkTags
            .Where(wt => wt.WorkId == Captured)
            .Select(wt => new { wt.Tag.Type, wt.Tag.Name })
            .ToListAsync();

        // Exactly what the page carries, of the types this application stores. The two the blurb
        // carried are gone with it: the capture is a real work's page and the blurb standing in for
        // its listing row is not, so the page's complete list does not contain them — which is the
        // reconcile working, and is pinned on its own below.
        Assert.Equal(31, tags.Count);
        Assert.Contains(tags, t => t.Type == Ao3TagType.Character && t.Name == "Rumi (KPop Demon Hunters)");
        Assert.Contains(tags, t => t.Type == Ao3TagType.Freeform && t.Name == "Wedding Fluff");
        Assert.Contains(tags, t => t.Type == Ao3TagType.Fandom && t.Name == "KPop Demon Hunters (2025)");
    }

    [Fact]
    public async Task The_detail_page_shows_what_the_fetch_wrote()
    {
        // The delivery, end to end: the endpoint T9 built reports "not fetched yet" as two nulls
        // until a pass has read the work's own page, and this is the pass.
        var user = _host.SeedUser();
        var shipId = await FollowAsync(user);
        await IngestAsync(shipId, Captured);
        ServeTheCapturedWorkPage();

        var before = await DetailAsync(user, Captured);
        Assert.Null(before.PublishedAt);
        Assert.Null(before.DetailFetchedAt);

        await _host.FetchWorkDetailsAsync();

        var after = await DetailAsync(user, Captured);
        Assert.Equal(new DateTime(2025, 9, 6, 0, 0, 0, DateTimeKind.Utc), after.PublishedAt);
        Assert.Equal(_host.Clock.Now.UtcDateTime, after.DetailFetchedAt);
        Assert.Equal(31, after.Tags.Count);
    }

    [Fact]
    public async Task A_work_whose_page_has_been_read_is_not_asked_for_again()
    {
        // The whole point of the column. A detail fetch is one request per work, and a pass that
        // re-read what it had already read would spend the instance's entire allowance on works
        // nobody has touched since.
        var shipId = await FollowAsync();
        await IngestAsync(shipId, Captured);
        ServeTheCapturedWorkPage();

        await _host.FetchWorkDetailsAsync();
        _host.Http.Requested.Clear();

        var second = await _host.FetchWorkDetailsAsync();

        Assert.Empty(_host.Http.Requested);
        Assert.Equal(0, second.WorksSelected);
    }

    [Fact]
    public async Task A_work_revised_since_its_page_was_read_is_asked_for_again()
    {
        // AO3's revision timestamp is the only thing that may re-open a work: an interval of ours
        // would re-fetch a library nobody has edited, and never re-fetching would leave a tag list
        // frozen at whatever the first pass read.
        var shipId = await FollowAsync();
        await IngestAsync(shipId, Captured);
        ServeTheCapturedWorkPage();

        await _host.FetchWorkDetailsAsync();
        _host.Http.Requested.Clear();

        await using (var db = _host.NewContext())
        {
            var work = await db.Works.SingleAsync(w => w.Id == Captured);
            work.UpdatedAt = work.DetailFetchedAt!.Value.AddDays(1);
            await db.SaveChangesAsync();
        }

        var second = await _host.FetchWorkDetailsAsync();

        Assert.Equal(1, second.WorksRead);
        Assert.Single(_host.Http.Requested);
    }

    [Fact]
    public async Task Asks_for_the_never_fetched_works_first_and_the_newest_of_those()
    {
        var shipId = await FollowAsync();
        await IngestAsync(shipId, 1, Ao3ListingFixtures.Jan(1));
        await IngestAsync(shipId, 2, Ao3ListingFixtures.Jan(3));
        await IngestAsync(shipId, 3, Ao3ListingFixtures.Jan(2));

        // A work already read, then revised — so it is in the backlog too, but behind all three
        // works whose detail page has never been read at all.
        await using (var db = _host.NewContext())
        {
            var work = await db.Works.SingleAsync(w => w.Id == 3);
            work.DetailFetchedAt = Ao3ListingFixtures.Jan(1);
            await db.SaveChangesAsync();
        }

        ServeTheCapturedWorkPage();
        await _host.FetchWorkDetailsAsync();

        Assert.Equal(
            [WorkPageUrl(2), WorkPageUrl(1), WorkPageUrl(3)],
            _host.Http.Requested);
    }

    [Fact]
    public async Task Stops_when_the_budget_is_spent_and_leaves_the_rest_for_the_next_pass()
    {
        var shipId = await FollowAsync();
        await IngestAsync(shipId, 1, Ao3ListingFixtures.Jan(2));
        await IngestAsync(shipId, 2, Ao3ListingFixtures.Jan(1));
        ServeTheCapturedWorkPage();

        var result = await _host.FetchWorkDetailsAsync(
            new ScrapeBudget(maxRequests: 1, maxConsecutiveFailures: 3, maxDuration: TimeSpan.FromHours(1)));

        Assert.Equal(2, result.WorksSelected);
        Assert.Equal(1, result.WorksRead);
        Assert.Equal([WorkPageUrl(1)], _host.Http.Requested);

        await using var db = _host.NewContext();
        Assert.Null((await db.Works.SingleAsync(w => w.Id == 2)).DetailFetchedAt);
    }

    [Fact]
    public async Task A_404_records_the_work_as_deleted_rather_than_as_fetched()
    {
        // The one place this application may conclude a work is gone — see Work.IsDeleted. It is
        // also what keeps the backlog finite: a work AO3 has taken down would otherwise be asked
        // for on every pass for ever.
        var shipId = await FollowAsync();
        await IngestAsync(shipId, 1);
        _host.Http.Responds = url => new ScrapeHttpResponse(
            "", HttpStatusCode.NotFound, FromCache: false, FinalUrl: url);

        var result = await _host.FetchWorkDetailsAsync();

        await using var db = _host.NewContext();
        var work = await db.Works.SingleAsync(w => w.Id == 1);

        Assert.True(work.IsDeleted);
        Assert.Equal(_host.Clock.Now.UtcDateTime, work.DeletedAt);
        Assert.Null(work.DetailFetchedAt);
        Assert.Equal(1, result.WorksGone);

        _host.Http.Requested.Clear();
        await _host.FetchWorkDetailsAsync();
        Assert.Empty(_host.Http.Requested);
    }

    [Fact]
    public async Task A_page_that_is_not_a_work_page_writes_nothing_and_stays_in_the_backlog()
    {
        // A 200 carrying the adult-content interstitial, a login page, or anything else AO3 or a
        // proxy substitutes. Stamping the work would hide that behind a row claiming to have been
        // read, and the work would never be asked for again.
        var shipId = await FollowAsync();
        await IngestAsync(shipId, 1);
        _host.Http.Responds = url => new ScrapeHttpResponse(
            "<html><body><p>This work could have adult content.</p></body></html>",
            HttpStatusCode.OK, FromCache: false, FinalUrl: url);

        var result = await _host.FetchWorkDetailsAsync();

        Assert.Equal(0, result.WorksRead);
        Assert.Equal(1, result.ParseWarnings);

        await using var db = _host.NewContext();
        var work = await db.Works.SingleAsync(w => w.Id == 1);
        Assert.Null(work.DetailFetchedAt);
        Assert.Null(work.PublishedAt);
    }

    [Fact]
    public async Task A_work_page_carrying_no_tags_is_not_written_from_at_all()
    {
        // AO3 requires a fandom of every work, so a page that parsed with no tags is a markup change
        // and has observed nothing. Nothing may be written from it — not the tags, which would be
        // the empty-observation deletion T51 forbids, and not the stamp either: DetailFetchedAt is
        // what puts the listing pass into add-only mode, so a work stamped from a tagless page would
        // have no source left that may ever drop a tag.
        var shipId = await FollowAsync();
        await IngestAsync(shipId, 1);

        var stripped = Fixtures.Load(Fixtures.WorkPage)
            .Replace("class=\"tag\"", "class=\"nothing\"", StringComparison.Ordinal);
        _host.Http.Responds = url => new ScrapeHttpResponse(
            stripped, HttpStatusCode.OK, FromCache: false, FinalUrl: url);

        var result = await _host.FetchWorkDetailsAsync();

        await using var db = _host.NewContext();
        var work = await db.Works.SingleAsync(w => w.Id == 1);

        Assert.Equal(0, result.WorksRead);
        Assert.Equal(1, result.ParseWarnings);
        Assert.Null(work.DetailFetchedAt);
        Assert.Null(work.PublishedAt);
        Assert.Equal(2, await db.WorkTags.CountAsync(wt => wt.WorkId == 1));
    }

    [Fact]
    public async Task A_work_whose_page_never_reads_is_written_off_and_stops_costing_requests()
    {
        // Without this the backlog starves. Selection is deterministic — never-fetched first, newest
        // first — and only a successful read takes a work out of it, so ten works whose pages cannot
        // be read fill every pass for ever and nothing else in the library is fetched again.
        var shipId = await FollowAsync();
        await IngestAsync(shipId, 1, Ao3ListingFixtures.Jan(2));
        await IngestAsync(shipId, 2, Ao3ListingFixtures.Jan(1));

        // Work 1 answers with the adult-content interstitial for ever; work 2's page reads.
        _host.Http.Responds = url => new ScrapeHttpResponse(
            url.Contains("/works/1?", StringComparison.Ordinal)
                ? "<html><body><p>This work could have adult content.</p></body></html>"
                : Fixtures.Load(Fixtures.WorkPage),
            HttpStatusCode.OK, FromCache: false, FinalUrl: url);

        for (var pass = 0; pass < WorkDetailAttempts.MaxAttempts; pass++)
            await _host.FetchWorkDetailsAsync();

        Assert.Equal(WorkDetailAttempts.MaxAttempts, _host.Http.Requested.Count(u => u == WorkPageUrl(1)));

        _host.Http.Requested.Clear();
        var after = await _host.FetchWorkDetailsAsync();

        // Work 2 was read on the first pass and has left the backlog; work 1 is written off. So the
        // fourth pass asks for nothing at all rather than for work 1 again.
        Assert.Equal(0, after.WorksSelected);
        Assert.Empty(_host.Http.Requested);
    }

    [Fact]
    public async Task A_run_of_deleted_works_does_not_stop_the_pass()
    {
        // A 404 is the conclusive answer this pass exists to record, not a failure. Charged to the
        // circuit breaker it would cut a pass to MaxConsecutiveFailures works whenever a library had
        // a run of deleted ones — and, since the ordering is deterministic, do it again every pass.
        var shipId = await FollowAsync();
        for (var id = 1; id <= 5; id++) await IngestAsync(shipId, id, Ao3ListingFixtures.Jan(id));

        _host.Http.Responds = url => new ScrapeHttpResponse(
            "", HttpStatusCode.NotFound, FromCache: false, FinalUrl: url);

        var result = await _host.FetchWorkDetailsAsync();

        Assert.Equal(5, result.WorksGone);

        await using var db = _host.NewContext();
        Assert.Equal(5, await db.Works.CountAsync(w => w.IsDeleted));
    }

    [Fact]
    public async Task Works_that_answer_with_a_refusal_do_not_block_the_ones_behind_them()
    {
        // The same starvation as above by the other route, and the sharper one: a non-OK status is a
        // failure, so three of them at the head of the backlog open the breaker and stop the pass —
        // every pass, for ever, with nothing behind them ever read.
        var shipId = await FollowAsync();
        for (var id = 1; id <= 4; id++) await IngestAsync(shipId, id, Ao3ListingFixtures.Jan(5 - id));

        _host.Http.Responds = url => url.Contains("/works/4?", StringComparison.Ordinal)
            ? new ScrapeHttpResponse(
                Fixtures.Load(Fixtures.WorkPage), HttpStatusCode.OK, FromCache: false, FinalUrl: url)
            : new ScrapeHttpResponse("", HttpStatusCode.Forbidden, FromCache: false, FinalUrl: url);

        // Three passes: each trips the breaker on works 1-3, which is what writes them off.
        for (var pass = 0; pass < WorkDetailAttempts.MaxAttempts; pass++)
            await _host.FetchWorkDetailsAsync();

        var last = await _host.FetchWorkDetailsAsync();

        Assert.Equal(1, last.WorksWritten);

        await using var db = _host.NewContext();
        Assert.NotNull((await db.Works.SingleAsync(w => w.Id == 4)).DetailFetchedAt);
    }

    [Fact]
    public async Task A_tag_the_work_page_no_longer_carries_is_dropped()
    {
        // The half of the rule only this source has: the work's own page is the complete list, so
        // what it stops showing really has gone. The listing pass may not conclude that, which is
        // what T51 settled.
        var shipId = await FollowAsync();
        await IngestAsync(shipId, Captured);
        ServeTheCapturedWorkPage();
        await _host.FetchWorkDetailsAsync();

        var withoutOne = Fixtures.Load(Fixtures.WorkPage)
            .Replace(
                """<li><a class="tag" href="https://archiveofourown.org/tags/Kid%20Fic/works">Kid Fic</a></li>""",
                "",
                StringComparison.Ordinal);
        _host.Http.Responds = url => new ScrapeHttpResponse(
            withoutOne, HttpStatusCode.OK, FromCache: false, FinalUrl: url);

        await using (var db = _host.NewContext())
        {
            var work = await db.Works.SingleAsync(w => w.Id == Captured);
            work.UpdatedAt = work.DetailFetchedAt!.Value.AddDays(1);
            await db.SaveChangesAsync();
        }

        await _host.FetchWorkDetailsAsync();

        await using var after = _host.NewContext();
        Assert.False(await after.WorkTags.AnyAsync(wt => wt.WorkId == Captured && wt.Tag.Name == "Kid Fic"));
        Assert.Equal(30, await after.WorkTags.CountAsync(wt => wt.WorkId == Captured));
    }

    [Fact]
    public async Task The_next_listing_pass_does_not_undo_what_the_fetch_added()
    {
        // T51's rule and this pass, together: the whole reason a detail fetch was blocked on that
        // task. Without it the tags read here are deleted by the next incremental pass over the
        // ship, silently, on a run recorded as a success.
        var shipId = await FollowAsync();
        await IngestAsync(shipId, Captured);
        ServeTheCapturedWorkPage();
        await _host.FetchWorkDetailsAsync();

        await IngestAsync(shipId, Captured);

        // The page's thirty-one, and the two the blurb carries put back — added, because a blurb
        // may add to a detail-fetched work. What matters is that none of the thirty-one went.
        await using var db = _host.NewContext();
        Assert.Equal(33, await db.WorkTags.CountAsync(wt => wt.WorkId == Captured));
        Assert.True(await db.WorkTags.AnyAsync(wt => wt.WorkId == Captured && wt.Tag.Name == "Wedding Fluff"));
    }

    [Fact]
    public async Task A_work_nothing_watches_is_never_asked_for()
    {
        // Works outlive the ships that brought them in — an unfollow leaves the rows behind — and a
        // request for one is an request for something no reader can see.
        var shipId = await FollowAsync();
        await IngestAsync(shipId, 1);

        await using (var db = _host.NewContext())
        {
            db.ShipWorks.RemoveRange(await db.ShipWorks.Where(sw => sw.ShipId == shipId).ToListAsync());
            await db.SaveChangesAsync();
        }

        ServeTheCapturedWorkPage();
        var result = await _host.FetchWorkDetailsAsync();

        Assert.Equal(0, result.WorksSelected);
        Assert.Empty(_host.Http.Requested);
    }

    [Fact]
    public async Task The_worker_reads_nothing_until_the_instance_may_scrape()
    {
        // The same gates the scrape and download workers apply. A fresh install has neither an
        // operator contact nor an AO3 login, and until both exist this instance sends nothing.
        var shipId = await FollowAsync();
        await IngestAsync(shipId, Captured);
        ServeTheCapturedWorkPage();

        _host.OperatorContact = null;
        var worker = _host.NewWorkDetailWorker();
        await worker.RunPassAsync(default);

        Assert.Empty(_host.Http.Requested);

        _host.OperatorContact = "ops@example.com";
        await _host.SaveAo3LoginAsync();
        await worker.RunPassAsync(default);

        Assert.Equal([WorkPageUrl(Captured)], _host.Http.Requested);
    }

    private static string WorkPageUrl(long workId) =>
        $"{LibraryTestHost.BaseUrl}/works/{workId}?view_adult=true";

    /// <summary>
    /// Serves the captured work page for whatever is asked for. The pass reads one work's page at a
    /// time and applies it to the work it asked for, so what matters per test is which URLs were
    /// requested rather than which body each one got.
    /// </summary>
    private void ServeTheCapturedWorkPage() => _host.Http.Responds = url => new ScrapeHttpResponse(
        Fixtures.Load(Fixtures.WorkPage), HttpStatusCode.OK, FromCache: false, FinalUrl: url);

    private Task IngestAsync(int shipId, long workId, DateTime? updatedAt = null) =>
        _host.IngestAsync(shipId, $"""
            <html><body><ol class="work index group">
            {Ao3ListingFixtures.Blurb(workId, updatedAt ?? Ao3ListingFixtures.Jan(1))}
            </ol></body></html>
            """);

    private async Task<WorkDetailDto> DetailAsync(ApplicationUser user, long workId)
    {
        var result = await _host.NewWorksRequest(user).GetWork(workId, default);
        return Assert.IsType<WorkDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private async Task<int> FollowAsync(ApplicationUser? user = null)
    {
        var result = await _host.Ships(user ?? _host.SeedUser()).WatchShip(new(Lexa), default);
        return Assert.IsType<WatchedShipDto>(
            Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }
}
