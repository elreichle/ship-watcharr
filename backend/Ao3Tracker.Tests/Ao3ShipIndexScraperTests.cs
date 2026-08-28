using System.Net;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ao3Tracker.Tests;

/// <summary>
/// Walking a ship's works index.
///
/// The parser's tests cover what a page says; these cover what a run does with it — which pages get
/// asked for, and what is left behind afterwards. Nearly every test here is really about a stopping
/// rule, because that is where the cost lives: AO3 sees every request this makes, through one shared
/// 5–8 second gate, and the difference between a correct and an incorrect stop is the difference
/// between one request a day and thousands.
/// </summary>
public class Ao3ShipIndexScraperTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";

    /// <summary>
    /// Every line the scraper logged during a test, kept as structured values. A warning is the
    /// only output some of these stopping rules produce, so it is the only thing a test can assert
    /// on — see <see cref="CapturingLoggerProvider"/> for why it is never rendered to a string.
    /// </summary>
    private readonly CapturingLoggerProvider _logs = new();

    private readonly LibraryTestHost _host;

    public Ao3ShipIndexScraperTests() =>
        _host = new LibraryTestHost(services =>
        {
            services.AddSingleton<ILoggerProvider>(_logs);

            // The host registers the real scraper as itself, which is all ScrapeAsync needs, but
            // ScraperRegistry resolves IAo3Scraper — so a worker running in this class would find
            // no scraper for the ship-index key and record no run at all. Registered here rather
            // than in the host because the registry throws on duplicate keys, and the worker tests
            // register a stub under this one.
            services.AddScoped<IAo3Scraper>(sp => sp.GetRequiredService<Ao3ShipIndexScraper>());
        });

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- what a first pass writes -----------------------------------------------------------------

    [Fact]
    public async Task Records_the_works_a_listing_returned()
    {
        _host.Http.Responds = Pages(Page(1, [Blurb(1), Blurb(2)]));
        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(2, outcome.WorksSeen);
        Assert.Equal(2, outcome.WorksAdded);
        Assert.Equal(0, outcome.WorksUpdated);

        await using var db = _host.NewContext();
        Assert.Equal([1, 2], await db.Works.OrderBy(w => w.Id).Select(w => w.Id).ToListAsync());
    }

    [Fact]
    public async Task Links_each_work_to_the_ship_whose_index_returned_it()
    {
        // The ShipWork row, not the relationship tag, is what a library query filters on — see the
        // remarks on ShipWork for why the tag is not trustworthy for this.
        _host.Http.Responds = Pages(Page(1, [Blurb(1)]));
        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId);

        await using var db = _host.NewContext();
        var link = Assert.Single(await db.ShipWorks.ToListAsync());
        Assert.Equal(shipId, link.ShipId);
        Assert.Equal(1, link.WorkId);
        Assert.Null(link.MissingSinceAt);
    }

    [Fact]
    public async Task Writes_the_shared_vocabulary_a_blurb_carries()
    {
        _host.Http.Responds = Pages(Page(1, [Blurb(1)]));
        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId);

        await using var db = _host.NewContext();

        Assert.Contains(
            await db.Tags.Where(t => t.Type == Ao3TagType.Relationship).Select(t => t.Name).ToListAsync(),
            name => name == Lexa);

        var pseud = Assert.Single(await db.Ao3Pseuds.ToListAsync());
        Assert.Equal("someuser", pseud.Username);

        // The normalized column is what author search goes through; nothing else keeps it in step.
        Assert.Equal(pseud.DisplayName.ToUpperInvariant(), pseud.DisplayNameNormalized);
    }

    [Fact]
    public async Task Records_the_tags_total_for_the_ship()
    {
        // The ship has no watermark, so the pass asks for the unfiltered listing and AO3's
        // "N Works in ..." heading really is the tag's total. That is the only case in which it
        // is — see Leaves_the_tags_total_alone_when_the_listing_was_filtered.
        _host.Http.Responds = Pages(Page(1, [Blurb(1)], total: 4317));
        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId);

        Assert.Equal(4317, (await ReloadAsync(shipId)).LastKnownTotalWorks);
    }

    // ---- the incremental stop -----------------------------------------------------------------------

    [Fact]
    public async Task Advances_the_watermark_to_the_newest_work_it_ingested()
    {
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(5)), Blurb(2, updatedAt: Jan(3))]));
        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId);

        Assert.Equal(Jan(5), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Stops_at_the_first_page_holding_anything_it_already_has()
    {
        // The listing is newest-first, so a page that is not entirely new means everything after it
        // is older still. This is what keeps a routine pass to a single request.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(9)), Blurb(2, updatedAt: Jan(4))], nextPage: true),
            Page(2, [Blurb(3, updatedAt: Jan(1))]));

        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(5));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Watermark, outcome.StopReason);
        Assert.Equal(1, outcome.PagesFetched);

        // Only the work newer than the watermark was ingested.
        await using var db = _host.NewContext();
        Assert.Equal([1], await db.Works.Select(w => w.Id).ToListAsync());
    }

    [Fact]
    public async Task Keeps_reading_while_every_work_on_the_page_is_new()
    {
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(9))], nextPage: true),
            Page(2, [Blurb(2, updatedAt: Jan(8))]));

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(2, outcome.PagesFetched);
        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
    }

    [Fact]
    public async Task Does_not_advance_the_watermark_on_a_run_that_ran_out_of_budget()
    {
        // A watermark moved past works the run never reached would skip them permanently: no later
        // incremental pass looks that far back again.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(9))], nextPage: true),
            Page(2, [Blurb(2, updatedAt: Jan(8))], nextPage: true));

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, budget: OneRequest());

        Assert.Equal(ScrapeStopReason.Cap, outcome.StopReason);
        Assert.Null((await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    // ---- blurbs whose date could not be read -------------------------------------------------------

    [Fact]
    public async Task Keeps_an_incremental_pass_going_past_a_blurb_whose_date_it_could_not_read()
    {
        // The worst shape of the bug this covers: an unreadable date reads as DateTime.MinValue,
        // MinValue is not newer than the watermark, so the page looked like it held something we
        // already had and the pass stopped on page 1 — while still advancing the watermark to the
        // newest work it *had* seen. Work 3 would then be behind the watermark forever, and no
        // later incremental pass looks that far back.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(9)), Blurb(2, undated: true)], nextPage: true),
            Page(2, [Blurb(3, updatedAt: Jan(8))]));

        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(5));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Equal(2, outcome.PagesFetched);

        // The undated work is ingested rather than dropped: it is a real work, and storing it with
        // an unknown revision time is the lesser loss.
        await using var db = _host.NewContext();
        Assert.Equal([1, 2, 3], await db.Works.OrderBy(w => w.Id).Select(w => w.Id).ToListAsync());

        // ...but it does not get to propose a watermark. Jan 9 is the newest *dated* work.
        Assert.Equal(Jan(9), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Still_stops_an_incremental_pass_on_a_page_holding_a_dated_work_it_already_has()
    {
        // Abstaining must not disable the stop. One undated blurb beside a stale one still means
        // the walk has caught up with itself.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(9)), Blurb(2, undated: true), Blurb(3, updatedAt: Jan(2))], nextPage: true),
            Page(2, [Blurb(4, updatedAt: Jan(1))]));

        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(5));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Watermark, outcome.StopReason);
        Assert.Equal(1, outcome.PagesFetched);

        await using var db = _host.NewContext();
        Assert.Equal([1, 2], await db.Works.OrderBy(w => w.Id).Select(w => w.Id).ToListAsync());
    }

    [Fact]
    public async Task Stops_an_incremental_pass_when_no_blurb_on_a_page_carries_a_readable_date()
    {
        // Every voter abstained, so nothing on the page says where in the listing we are. Reading
        // on would walk the whole tag on every pass — the cost this pass exists to avoid.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, undated: true)], nextPage: true),
            Page(2, [Blurb(2, updatedAt: Jan(9))]));

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Equal(1, outcome.PagesFetched);

        // Stopped for a reason that means "something is wrong", not "there was nothing more to
        // read" — so the watermark stays where it was.
        Assert.Null((await ReloadAsync(shipId)).IncrementalWatermarkUtc);

        // The work is still ingested; only the walk stopped.
        await using var db = _host.NewContext();
        Assert.Equal([1], await db.Works.Select(w => w.Id).ToListAsync());
    }

    [Fact]
    public async Task Does_not_call_a_last_page_an_error_when_an_incremental_pass_could_not_read_its_dates()
    {
        // A small tag on one page, whose dates the parser cannot read. There is no page after it,
        // so there is no runaway walk to prevent — and calling this an error would leave the
        // watermark null and repeat the same complaint on every incremental pass forever.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, undated: true)]));

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);

        await using var db = _host.NewContext();
        Assert.Equal([1], await db.Works.Select(w => w.Id).ToListAsync());
    }

    [Fact]
    public async Task Marks_a_work_an_incremental_pass_first_saw_undated_as_having_an_approximate_date()
    {
        // Its UpdatedAt is the default of year 1, which is not a date anybody read. The row must
        // not present it as an exact one.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, undated: true)]));

        var shipId = await FollowAsync();
        await _host.ScrapeAsync(shipId);

        await using var db = _host.NewContext();
        var work = await db.Works.SingleAsync();
        Assert.Equal(DateTime.MinValue, work.UpdatedAt);
        Assert.True(work.UpdatedAtIsApproximate);
    }

    [Fact]
    public async Task Stamps_the_tags_total_from_the_injected_clock_on_an_incremental_pass()
    {
        // Every other write in the scraper reads the injected clock; this one called
        // DateTime.UtcNow, so no test could see it.
        _host.Clock.Now = new DateTimeOffset(2024, 6, 1, 9, 30, 0, TimeSpan.Zero);

        _host.Http.Responds = Pages(Page(1, [Blurb(1)], total: 4317));
        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId);

        Assert.Equal(_host.Clock.Now.UtcDateTime, (await ReloadAsync(shipId)).LastKnownTotalWorksAt);
    }

    [Fact]
    public async Task Leaves_the_tags_total_alone_when_the_listing_was_filtered()
    {
        // An incremental pass with a watermark adds work_search[revised_at] to the URL, so AO3's
        // heading counts the *filter's* result set. A ship that backfilled to 4,317 works read 2
        // after one quiet pass — and LastKnownTotalWorks is the figure a full sweep checks itself
        // against before concluding works have left the tag, so a 2 there reads as an emptied tag.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(9))], total: 2));

        var shipId = await FollowAsync();
        await SetTotalAsync(shipId, 4317, readAt: Jan(1));
        await SetWatermarkAsync(shipId, Jan(5));

        await _host.ScrapeAsync(shipId);

        // Not merely the count: the timestamp beside it must not claim the old figure was re-read.
        var ship = await ReloadAsync(shipId);
        Assert.Equal(4317, ship.LastKnownTotalWorks);
        Assert.Equal(Jan(1), ship.LastKnownTotalWorksAt);
    }

    [Fact]
    public async Task Does_not_claim_a_filtered_pass_authenticated_the_total_it_did_not_write()
    {
        // Ship documents the flag as whether the run that produced *the stored total* was logged
        // in. A filtered pass produces no total, so letting it flip the flag leaves a total counted
        // logged-out — restricted works invisible, so undercounted — wearing an authenticated run's
        // flag. That is the exact mis-conclusion the field exists to prevent, and it is what a full
        // sweep reads before deciding works have left the tag.
        //
        // The run is genuinely logged in — the transport says so — which is what keeps this test
        // from passing vacuously: remove the gate and the flag goes true.
        _host.Http.Responds = url =>
            Ok(url, Page(1, [Blurb(1, updatedAt: Jan(9))], total: 2).Html) with { Authenticated = true };

        var shipId = await FollowAsync();
        await SetTotalAsync(shipId, 4317, readAt: Jan(1));
        await SetWatermarkAsync(shipId, Jan(5));

        await _host.ScrapeAsync(shipId);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(4317, ship.LastKnownTotalWorks);
        Assert.False(ship.LastKnownTotalWasAuthenticated);
    }

    [Fact]
    public async Task Does_not_let_a_restricted_blurb_claim_the_total_was_Authenticated()
    {
        // A restricted work is documented as invisible to a logged-out request, so one on the page
        // looks like proof the run was logged in. It is not the kind of proof this flag may take:
        // the question is what the run *sent*, the transport is what knows, and here it says no
        // session. Believing the page instead stamps "counted while logged in" on a total fetched
        // without a session — on the strength of a markup premise nothing in this repo verifies,
        // which is T79's open question about a neighbouring one.
        //
        // The contradiction is worth a log line, and gets one. It is not worth a conclusion.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, restricted: true)], total: 4317));

        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(4317, ship.LastKnownTotalWorks);
        Assert.False(ship.LastKnownTotalWasAuthenticated);
    }

    [Fact]
    public async Task Clears_the_Authenticated_flag_when_a_later_run_reads_the_total_anonymously()
    {
        // The latch this task exists to open. A logged-in run once read the total and stamped it;
        // the session lapses; this run reads a fresh, lower total anonymously — restricted works
        // invisible to it, so the figure is short by exactly the number the flag would tell a sweep
        // to allow for. Leaving the flag true describes the wrong run's visibility, which is the
        // mis-conclusion the field exists to prevent. The flag belongs to the total beside it, so a
        // run that replaces the total replaces the flag.
        _host.Http.Responds = Pages(Page(1, [Blurb(1)], total: 4000));

        var shipId = await FollowAsync();
        await SetTotalAsync(shipId, 4317, readAt: Jan(1), authenticated: true);

        await _host.ScrapeAsync(shipId);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(4000, ship.LastKnownTotalWorks);
        Assert.False(ship.LastKnownTotalWasAuthenticated);
    }

    [Fact]
    public async Task Does_not_stamp_a_total_it_never_wrote_as_Authenticated()
    {
        // The other half of "the flag describes the total it sits beside", and the case the filter
        // gate alone does not cover: this pass is unfiltered, so it is entitled to write a total —
        // but its page carries no readable heading, so it writes none. The stored total is still
        // the *previous* run's, and a restricted work on this page says nothing about how that one
        // was read.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, restricted: true)], total: null));

        var shipId = await FollowAsync();
        await SetTotalAsync(shipId, 4317, readAt: Jan(1), authenticated: false);

        await _host.ScrapeAsync(shipId);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(4317, ship.LastKnownTotalWorks);
        Assert.Equal(Jan(1), ship.LastKnownTotalWorksAt);
        Assert.False(ship.LastKnownTotalWasAuthenticated);
    }

    [Fact]
    public async Task Reads_the_transport_for_whether_the_total_was_Authenticated()
    {
        // A restricted work proves a run was logged in; the absence of one proves nothing, because
        // a tag may simply hold none. So the flag cannot be read off the blurbs alone without
        // claiming "anonymous" of every authenticated pass over an unrestricted tag — a false
        // *false*, which is the harmful direction: it tells a sweep the stored total was counted at
        // its own visibility level when the total is in fact the higher, logged-in count. The
        // transport is the one thing that knows, and it says so on the response the heading came
        // from.
        _host.Http.Responds = url => Ok(url, Page(1, [Blurb(1)], total: 4317).Html) with
        {
            Authenticated = true,
        };

        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(4317, ship.LastKnownTotalWorks);
        Assert.True(ship.LastKnownTotalWasAuthenticated);
    }

    [Fact]
    public async Task Does_not_let_a_later_pages_session_stamp_a_total_read_anonymously()
    {
        // The flag describes *the request that read the total*, not "was anything in this run
        // authenticated". A run mixes the two as soon as it reads more than one page: the session
        // can be absent on the page whose heading is stored and present on a later one — a cached
        // page 1, or a session that only starts being honoured partway through — and an OR across
        // the run then stamps "counted while logged in" on a number demonstrably fetched without a
        // session. That is a total short by however many restricted works the tag holds, wearing
        // the flag that tells T15's sweep to allow for an invisibility it does not have.
        //
        // Page 2 carries no heading, so it writes no total and has nothing to say about the one on
        // the ship. It is only where the session appears.
        _host.Http.Responds = url =>
        {
            var page = Pages(
                Page(1, [Blurb(1)], nextPage: true, total: 4317),
                Page(2, [Blurb(2)]))(url);

            return url.Contains("page=2", StringComparison.Ordinal)
                ? page with { Authenticated = true }
                : page;
        };

        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(4317, ship.LastKnownTotalWorks);
        Assert.False(ship.LastKnownTotalWasAuthenticated);
    }

    [Fact]
    public async Task Takes_the_Authenticated_flag_from_the_last_page_that_wrote_the_total()
    {
        // The same rule from the other side, and the reachable half after T5: a session can die
        // mid-run — the client discards it the moment a page comes back logged out — so page 1
        // reads the tag with a cookie and page 2 re-reads the heading without one. Every page of an
        // unfiltered pass writes the total, so the stored number is page 2's, short by the
        // restricted works page 1 could see. The flag has to fall with it.
        //
        // Both pages carry a heading here, which is what makes the last writer the question rather
        // than the only writer.
        _host.Http.Responds = url =>
        {
            var page = Pages(
                Page(1, [Blurb(1)], nextPage: true, total: 4317),
                Page(2, [Blurb(2)], total: 4000))(url);

            return url.Contains("page=2", StringComparison.Ordinal)
                ? page
                : page with { Authenticated = true };
        };

        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(4000, ship.LastKnownTotalWorks);
        Assert.False(ship.LastKnownTotalWasAuthenticated);
    }

    [Fact]
    public async Task Records_the_tags_total_from_a_backfill_of_a_ship_that_has_a_watermark()
    {
        // The gate is on the filter, not on the mode. A backfill asks for the whole listing
        // whatever the ship's watermark says, so its heading is the tag's total and must land.
        _host.Http.Responds = Pages(Page(1, [Blurb(1)], total: 4317));

        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(5));

        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(4317, (await ReloadAsync(shipId)).LastKnownTotalWorks);
    }

    [Fact]
    public async Task Asks_AO3_to_exclude_what_it_already_has()
    {
        _host.Http.Responds = Pages(Page(1, [Blurb(1)]));
        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(5));

        await _host.ScrapeAsync(shipId);

        // A day's slack either side of the watermark, so the server-side filter can only ever
        // return more than needed — the exact cut is made against the watermark in memory.
        Assert.Contains("revised_at", _host.Http.Requested[0]);
        Assert.Contains("2023-01-04", Uri.UnescapeDataString(_host.Http.Requested[0]));
    }

    // ---- the backfill walk ---------------------------------------------------------------------------

    [Fact]
    public async Task Walks_forward_through_the_back_catalogue()
    {
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, [Blurb(2)], nextPage: true),
            Page(3, [Blurb(3)]));

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(3, outcome.PagesFetched);
        Assert.Equal(3, outcome.WorksAdded);
        Assert.Equal(1, outcome.FirstPage);
        Assert.Equal(3, outcome.LastPage);
    }

    [Fact]
    public async Task Marks_the_backfill_complete_when_it_runs_out_of_pages()
    {
        _host.Http.Responds = Pages(Page(1, [Blurb(1)]));
        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.Complete, ship.BackfillState);
        Assert.NotNull(ship.BackfillCompletedAt);
    }

    [Fact]
    public async Task Saves_a_cursor_a_capped_run_can_resume_from()
    {
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, [Blurb(2)], nextPage: true),
            Page(3, [Blurb(3)]));

        var shipId = await FollowAsync();

        var first = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill, OneRequest());

        Assert.Equal(ScrapeStopReason.Cap, first.StopReason);

        var afterFirst = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.InProgress, afterFirst.BackfillState);

        // Page 1 is committed, so the cursor points at page 2 — the next thing not yet written,
        // never the page that was in flight.
        Assert.Equal(2, afterFirst.BackfillNextPage);
    }

    [Fact]
    public async Task Resumes_a_backfill_where_the_last_run_stopped()
    {
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, [Blurb(2)], nextPage: true),
            Page(3, [Blurb(3)]));

        var shipId = await FollowAsync();
        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill, OneRequest());

        _host.Http.Requested.Clear();
        var second = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(2, second.FirstPage);
        Assert.DoesNotContain(_host.Http.Requested, url => !url.Contains("page="));
    }

    [Fact]
    public async Task Refuses_to_end_a_walk_on_a_404_the_page_before_it_said_would_answer()
    {
        // The walk only ever advances past a page that offered a next link, so a 404 arriving
        // straight after a page this run read is a page the listing itself said exists. That is a
        // contradiction, not the end of anything, and it used to be read as Complete.
        _host.Http.Responds = url => url.Contains("page=2")
            ? new ScrapeHttpResponse("", HttpStatusCode.NotFound, FromCache: false, FinalUrl: url)
            : Ok(url, Page(1, [Blurb(1)], nextPage: true).Html);

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.InProgress, ship.BackfillState);

        // Left on the page that did not answer, which is what hands the question to the retreat on
        // the next run rather than asking the same thing twice inside this one.
        Assert.Equal(2, ship.BackfillNextPage);
        Assert.Equal(1, _host.Http.Requested.Count(url => url.Contains("page=2")));
    }

    [Fact]
    public async Task Reads_the_same_conclusion_off_a_404_whichever_run_read_the_page_before_it()
    {
        // The crossing T43 was filed over, stated as a property. One server — pages 1 and 2 offering
        // next links, page 3 absent — and the only difference is whether this run read page 2 or an
        // earlier one did. A walk from page 1 used to call that Complete while a cursor resumed at
        // page 3 called it Error, so a single transient failure mid-retreat decided whether a ship
        // was retired with its back catalogue unread.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, [Blurb(2)], nextPage: true));

        var walked = await FollowAsync();
        var walkedOutcome = await _host.ScrapeAsync(walked, ScrapeRunMode.Backfill);
        var walkedShip = await ReloadAsync(walked);

        var resumed = await FollowAsync("Nomi Marks/Amanita Caplan");
        await ResumeBackfillAtAsync(resumed, page: 3);
        var resumedOutcome = await _host.ScrapeAsync(resumed, ScrapeRunMode.Backfill);
        var resumedShip = await ReloadAsync(resumed);

        Assert.Equal(resumedOutcome.StopReason, walkedOutcome.StopReason);
        Assert.Equal(resumedShip.BackfillState, walkedShip.BackfillState);
        Assert.Equal(resumedShip.BackfillNextPage, walkedShip.BackfillNextPage);

        Assert.Equal(ScrapeStopReason.Error, walkedOutcome.StopReason);
        Assert.Equal(ShipBackfillState.InProgress, walkedShip.BackfillState);
    }

    [Fact]
    public async Task Does_not_retire_a_ship_because_a_retreat_was_cut_short_by_a_passing_failure()
    {
        // The route in, run for run. Page 3 is absent and page 2 insists it is there, so the ship
        // must end up stalled rather than complete — but the first run's retreat never gets its
        // answer, because page 2 is having a bad moment, and it leaves the cursor at 2. The second
        // run is healthy, reads page 2, and walks into the 404 from the other side.
        _host.Http.Responds = url => url.Contains("page=2")
            ? new ScrapeHttpResponse("", HttpStatusCode.InternalServerError, FromCache: false, FinalUrl: url)
            : Pages(Page(1, [Blurb(1)], nextPage: true))(url);

        var shipId = await FollowAsync();
        await ResumeBackfillAtAsync(shipId, page: 3);

        var first = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, first.StopReason);
        Assert.Equal(2, (await ReloadAsync(shipId)).BackfillNextPage);

        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, [Blurb(2)], nextPage: true));

        var second = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, second.StopReason);
        Assert.Equal(ShipBackfillState.InProgress, (await ReloadAsync(shipId)).BackfillState);
    }

    [Fact]
    public async Task Completes_on_the_next_run_when_the_listing_really_did_shrink_under_the_walk()
    {
        // What refusing to conclude costs, and it is one run rather than the completion. Works are
        // deleted between the two requests of a walk, so page 2 — read moments ago offering a next
        // link — is followed by a 404. This run stops; the next run's first request lands on that
        // page, the retreat re-reads page 2, and the listing having dropped its next link is the
        // listing's own word that it ends there.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, [Blurb(2)], nextPage: true));

        var shipId = await FollowAsync();

        Assert.Equal(ScrapeStopReason.Error, (await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill)).StopReason);
        Assert.Equal(3, (await ReloadAsync(shipId)).BackfillNextPage);

        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, [Blurb(2)]));

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.Complete, ship.BackfillState);
        Assert.Equal(0, ship.BackfillStalledRuns);
    }

    // ---- a cursor pointing past the end of a listing that shrank ---------------------------------

    [Fact]
    public async Task Completes_a_backfill_whose_cursor_the_shrunken_listing_404s_past()
    {
        // A previous run left the cursor at page 3 because page 2 offered a next link. Works were
        // then deleted and the listing is two pages long, so the cursor's page is gone. AO3 answers
        // that with a 404 — which says nothing on its own about whether the listing ended or the
        // request was wrong, so the run asks the page before it, and page 2 having no next link is
        // the listing's own word that it ends there.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, [Blurb(2)]));

        var shipId = await FollowAsync();
        await ResumeBackfillAtAsync(shipId, page: 3);

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Contains(_host.Http.Requested, url => url.Contains("page=3"));
        Assert.Contains(_host.Http.Requested, url => url.Contains("page=2"));

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.Complete, ship.BackfillState);
        Assert.Equal(0, ship.BackfillStalledRuns);
    }

    [Fact]
    public async Task Completes_a_backfill_whose_cursor_the_shrunken_listing_serves_empty()
    {
        // The same event, and AO3's other answer to it: a 200 carrying the listing container with
        // nothing in it. The 404 branch and this one used to conclude opposite things here — the end
        // of the listing, or a parse failure re-requested on every run forever. Both now ask page 2.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, [Blurb(2)]),
            Page(3, []));

        var shipId = await FollowAsync();
        await ResumeBackfillAtAsync(shipId, page: 3);

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Equal(ShipBackfillState.Complete, (await ReloadAsync(shipId)).BackfillState);
    }

    [Fact]
    public async Task Leaves_a_stale_looking_cursor_alone_when_the_page_before_it_still_offers_a_next_link()
    {
        // The other half of the same question, and the reason the retreat is not licence to
        // complete: page 2 says page 3 exists, so page 3 not answering is AO3 having a bad moment,
        // not a listing that ended. The run stops, the cursor stays where it was, and page 3 is
        // asked for once more on the next scheduled run — not a second time inside this one.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, [Blurb(2)], nextPage: true));

        var shipId = await FollowAsync();
        await ResumeBackfillAtAsync(shipId, page: 3);

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Equal(1, _host.Http.Requested.Count(url => url.Contains("page=3")));

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.InProgress, ship.BackfillState);
        Assert.Equal(3, ship.BackfillNextPage);
        Assert.Equal(1, ship.BackfillStalledRuns);
    }

    [Fact]
    public async Task Halves_the_cursor_when_the_page_before_it_does_not_answer_either()
    {
        // Two pages in a row unanswerable says the listing is shorter than the cursor by an unknown
        // amount, not by one — so stepping back a page a run does not converge, and a listing that
        // lost dozens of pages (an instance login lapsing drops every restricted work at once)
        // would exhaust the allowance and be written off having never been broken.
        _host.Http.Responds = Pages(Page(1, [Blurb(1)]));

        var shipId = await FollowAsync();
        await ResumeBackfillAtAsync(shipId, page: 40);

        var first = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, first.StopReason);
        Assert.Contains("shorter than the backfill cursor", first.ErrorMessage);
        Assert.Equal(20, (await ReloadAsync(shipId)).BackfillNextPage);

        // And it keeps halving until it lands on a page the listing will answer for, rather than
        // spending the allowance walking. Four more runs reach page 1, which reads.
        for (var run = 0; run < 4; run++) await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.Complete, ship.BackfillState);
        Assert.Equal(0, ship.BackfillStalledRuns);
    }

    [Fact]
    public async Task Does_not_count_a_cursor_page_it_set_aside_as_a_page_it_read()
    {
        // The retreat sets the cursor's page aside unread, so it must not stand in for the page the
        // run went on to read. FinishAsync asks "did this run see page 1" to decide whether it may
        // move the watermark, and a stale cursor answering that question for page 1 throws away the
        // watermark the retreat just earned — while leaving a FirstPageFetched above the
        // LastPageFetched in the run history, which is a range an operator cannot read at all.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(9))]));

        var shipId = await FollowAsync();
        await ResumeBackfillAtAsync(shipId, page: 2);

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(1, outcome.FirstPage);
        Assert.Equal(1, outcome.LastPage);
        Assert.Equal(Jan(9), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Reads_the_same_run_off_both_of_AO3s_answers_to_a_page_that_is_gone()
    {
        // The unification, stated as a test: a 404 and a 200 with an empty listing are AO3's choice
        // about how to answer for a page that no longer exists, not a difference in what happened,
        // so a run must not be able to tell which it got from what it leaves behind.
        async Task<(ScrapeOutcome Outcome, Ship Ship)> RunAsync(FakePage[] pages)
        {
            _host.Http.Responds = Pages(pages);
            var id = await FollowAsync($"Alpha{pages.Length}/Beta{pages.Length}");
            await ResumeBackfillAtAsync(id, page: 2);
            return (await _host.ScrapeAsync(id, ScrapeRunMode.Backfill), await ReloadAsync(id));
        }

        // Page 2 missing from the listing entirely: the fake answers 404.
        var missing = await RunAsync([Page(1, [Blurb(1, updatedAt: Jan(9))])]);

        // Page 2 present and empty: the fake answers 200 with the listing container and no blurbs.
        var empty = await RunAsync([Page(1, [Blurb(1, updatedAt: Jan(9))]), Page(2, [])]);

        Assert.Equal(missing.Outcome.StopReason, empty.Outcome.StopReason);
        Assert.Equal(missing.Outcome.FirstPage, empty.Outcome.FirstPage);
        Assert.Equal(missing.Outcome.LastPage, empty.Outcome.LastPage);
        Assert.Equal(missing.Ship.BackfillState, empty.Ship.BackfillState);
        Assert.Equal(missing.Ship.IncrementalWatermarkUtc, empty.Ship.IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Gives_up_on_a_backfill_that_spends_run_after_run_on_a_cursor_nothing_answers()
    {
        // The bound. Every page is a maintenance page, so no retreat ever finds an answer and the
        // cursor walks back a page a run. Left alone that is two requests a run, forever, which is
        // the load this project's politeness rules exist to prevent. After MaxStalledBackfillRuns
        // the backfill is Failed — not Complete, which would claim a back catalogue never read.
        // The cursor halves its way down to page 1 on the way and goes on counting there, so a
        // ship that has run out of listing to retreat into is written off rather than left asking.
        _host.Http.Responds = _ => new ScrapeHttpResponse(
            "<html><body><h1>Down for maintenance</h1></body></html>",
            HttpStatusCode.OK, FromCache: false, FinalUrl: "");

        var shipId = await FollowAsync();
        await ResumeBackfillAtAsync(shipId, page: 10);

        for (var run = 1; run < Ao3ShipIndexScraper.MaxStalledBackfillRuns; run++)
        {
            await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

            var during = await ReloadAsync(shipId);
            Assert.Equal(run, during.BackfillStalledRuns);

            // Still trying, right up to the last run in the allowance.
            Assert.Equal(ShipBackfillState.InProgress, during.BackfillState);
        }

        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.Failed, ship.BackfillState);
        Assert.Null(ship.BackfillCompletedAt);

        // Failed, not Complete, is what keeps the gap visible: ScrapeWorker backfills a NotStarted
        // or InProgress ship only, so the ship falls back to its incremental pass rather than going
        // on asking. Closing the gap is a full sweep's job.
        Assert.NotEqual(ShipBackfillState.Complete, ship.BackfillState);
    }

    [Fact]
    public async Task Clears_a_stalled_streak_as_soon_as_the_backfill_moves_forward_again()
    {
        // The counter is about *consecutive* stalls. A ship that was stuck and is no longer must
        // not carry the count into the run that finally finds the listing again.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, [Blurb(2)], nextPage: true),
            Page(3, [Blurb(3)]));

        var shipId = await FollowAsync();
        await ResumeBackfillAtAsync(shipId, page: 2);
        await SetStalledRunsAsync(shipId, 3);

        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(0, ship.BackfillStalledRuns);
        Assert.Equal(ShipBackfillState.Complete, ship.BackfillState);
    }

    [Fact]
    public async Task Clears_a_stalled_streak_when_a_backfill_begins()
    {
        // A streak counts *consecutive* runs of one backfill getting nowhere, so a walk that is
        // starting has none by definition. The row can still arrive here carrying one — hand-edited,
        // or rewound to NotStarted by something later — and inheriting it would have the new walk
        // give up on its first stalled run instead of its twelfth.
        // Asserted through a run that *stalls*, so the reset has to have happened before the walk
        // rather than after it: a run that got anywhere would clear the counter on its own, and the
        // test would pass with the reset deleted.
        _host.Http.Responds = _ => new ScrapeHttpResponse(
            "<html><body><h1>Down for maintenance</h1></body></html>",
            HttpStatusCode.OK, FromCache: false, FinalUrl: "");

        var shipId = await FollowAsync();
        await SetStalledRunsAsync(shipId, Ao3ShipIndexScraper.MaxStalledBackfillRuns - 1);

        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        // At most one, rather than exactly one. T40 settled that a run whose only page was
        // unreadable *does* count against the ship, and
        // Counts_a_stalled_run_when_an_unreadable_page_left_PagesFetched_at_zero is
        // where that is pinned; either answer leaves the streak this ship arrived with — eleven —
        // discarded, which is what this test is about, so it stays loose about the other rule.
        var ship = await ReloadAsync(shipId);
        Assert.InRange(ship.BackfillStalledRuns, 0, 1);
        Assert.Equal(ShipBackfillState.InProgress, ship.BackfillState);
    }

    // ---- what a page with no readable works may conclude ----------------------------------------------

    [Fact]
    public async Task Refuses_to_call_a_backfill_complete_when_a_page_parses_to_no_works_under_a_populated_heading()
    {
        // The heading and the blurbs come off the same page and contradict each other: AO3 says the
        // tag holds 4,317 works and not one of them could be read. That is a parse failure, and
        // calling it the end of the listing retires the ship from backfilling having read nothing.
        _host.Http.Responds = Pages(Page(1, [], total: 4317));
        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Contains("4317", outcome.ErrorMessage);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.InProgress, ship.BackfillState);
        Assert.Null(ship.BackfillCompletedAt);
    }

    [Fact]
    public async Task Refuses_to_conclude_from_an_empty_page_reached_mid_walk()
    {
        // Page 1 advertised a Next link, so page 2 exists by AO3's own account. Nothing readable on
        // it means the markup changed or a soft-error page was served, not that the tag ended.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, []));

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.InProgress, ship.BackfillState);

        // The cursor still points at the page that failed, so the next run asks for it again —
        // once, at the scheduler's spacing.
        Assert.Equal(2, ship.BackfillNextPage);
    }

    // ---- and what it may be counted as ----------------------------------------------------------------

    [Fact]
    public async Task An_unreadable_page_adds_no_PagesFetched_and_names_no_boundary()
    {
        // The run history is read by an operator working out what a ship has actually done, and
        // PagesFetched's own summary says "listing pages successfully parsed". A fresh backfill
        // whose page 1 is a 200 maintenance page had been filing PagesFetched = 1 with
        // FirstPageFetched = LastPageFetched = 1 and no works: a run claiming the one page it could
        // not read. The retreat path was fixed for this in T37 by setting its page aside; this is
        // the same page reached by the other route.
        _host.Http.Responds = _ => new ScrapeHttpResponse(
            "<html><body><h1>Down for maintenance</h1></body></html>",
            HttpStatusCode.OK, FromCache: false, FinalUrl: "");

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Equal(0, outcome.PagesFetched);
        Assert.Null(outcome.FirstPage);
        Assert.Null(outcome.LastPage);
        Assert.Equal(0, outcome.WorksSeen);
    }

    [Fact]
    public async Task PagesFetched_and_the_boundary_stop_at_the_last_page_that_read()
    {
        // The same rule where the run got somewhere first. Page 1 reads and advertises more, page 2
        // comes back unreadable: the run has read exactly one page and its boundary is 1..1, not
        // 1..2. LastPageFetched naming the page that failed is what the error message is for.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, []));

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Equal(1, outcome.PagesFetched);
        Assert.Equal(1, outcome.FirstPage);
        Assert.Equal(1, outcome.LastPage);
    }

    [Fact]
    public async Task Keeps_an_unreadable_pages_blurb_warnings_out_of_PagesFetched_but_not_out_of_the_log()
    {
        // ParseWarnings is the fourth counter that used to move above the break, and dropping it is
        // the same rule — a run reporting warnings from a page its PagesFetched says it never read
        // is the conflation one field over. But this is the number that tells a markup change apart
        // from an empty page, so the error line carries it: two blurbs were there and neither could
        // be named, under a heading claiming 4,317 works in the tag.
        _host.Http.Responds = Pages(Page(1, [Nameless(), Nameless()], total: 4317));

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Equal(0, outcome.PagesFetched);
        Assert.Equal(0, outcome.ParseWarnings);

        var failure = Assert.Single(
            _logs.Records, r => r.Template.Contains("Treating this as a parse failure"));
        Assert.Equal(2, failure.Value("Warnings"));
    }

    [Fact]
    public async Task Counts_a_stalled_run_when_an_unreadable_page_left_PagesFetched_at_zero()
    {
        // The decision T40 had to make. Once a backfill's cursor has been dragged down to page 1 no
        // retreat can run there — CursorMayBeStale requires page > 1 — so `askedStaleCursor` is
        // false from then on. Reading the stalled guard as "did a page parse" would therefore
        // freeze the streak at page 1 and put Failed out of reach: the ship would re-request one
        // unanswerable page once a run, for ever, which is exactly the load the counter exists to
        // bound. So a page AO3 served and the parser could not read is counted against the ship,
        // and the run history it is counted from says PagesFetched = 0.
        //
        // Defensible only because T38 made the write-off reversible: POST
        // /api/admin/ships/{id}/backfill/restart puts a Failed backfill back to InProgress, so the
        // bound now ends a pointless request-a-run loop rather than retiring a back catalogue.
        _host.Http.Responds = _ => new ScrapeHttpResponse(
            "<html><body><h1>Down for maintenance</h1></body></html>",
            HttpStatusCode.OK, FromCache: false, FinalUrl: "");

        var shipId = await FollowAsync();
        await ResumeBackfillAtAsync(shipId, page: 1);

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(0, outcome.PagesFetched);
        Assert.Equal(1, (await ReloadAsync(shipId)).BackfillStalledRuns);

        // And goes on counting, rather than stopping at one: the streak is what reaches
        // MaxStalledBackfillRuns and ends the asking.
        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);
        Assert.Equal(2, (await ReloadAsync(shipId)).BackfillStalledRuns);
    }

    [Fact]
    public async Task Counts_no_stalled_run_when_a_refused_status_left_PagesFetched_at_zero()
    {
        // The other side of the same guard, and the reason it cannot simply count every run that
        // got nowhere. PagesFetched is zero here too, but AO3 told this run nothing at all — it
        // refused before serving a page — and twelve such runs are an archive having a bad
        // afternoon, not a ship whose cursor the listing will not answer. Writing off a back
        // catalogue for that is the mis-conclusion the guard exists to prevent.
        _host.Http.Responds = url =>
            new ScrapeHttpResponse("", HttpStatusCode.ServiceUnavailable, FromCache: false, FinalUrl: url);

        var shipId = await FollowAsync();
        await ResumeBackfillAtAsync(shipId, page: 1);

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);
        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Equal(0, outcome.PagesFetched);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(0, ship.BackfillStalledRuns);
        Assert.Equal(ShipBackfillState.InProgress, ship.BackfillState);
    }

    [Fact]
    public async Task Refuses_to_conclude_from_an_empty_page_a_resumed_backfill_started_on()
    {
        // The same anomaly through the route the old `pagesFetched == 1` guard could not see: this
        // run's *first* request is page 3, because a previous run's cap left the cursor there. The
        // page that advertised more was read by that earlier run.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, [Blurb(2)], nextPage: true),
            Page(3, []));

        var shipId = await FollowAsync();
        await ResumeBackfillAtAsync(shipId, page: 3);

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Equal(ShipBackfillState.InProgress, (await ReloadAsync(shipId)).BackfillState);
    }

    [Fact]
    public async Task Refuses_to_conclude_from_a_response_that_carries_no_listing_at_all()
    {
        // The motivating case, and the one no page-local count can catch: a 200 whose body is not a
        // results page. An empty tag still renders AO3's listing container; a maintenance page, a
        // truncated body or a proxy's substitute does not, which is what tells the two apart.
        // Note the fake HTTP client's own default response is exactly this shape.
        _host.Http.Responds = _ => new ScrapeHttpResponse(
            "<html><body><h1>Down for maintenance</h1></body></html>",
            HttpStatusCode.OK, FromCache: false, FinalUrl: "https://example.test/");

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Contains("no listing", outcome.ErrorMessage);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.InProgress, ship.BackfillState);
        Assert.Null(ship.BackfillCompletedAt);
    }

    [Fact]
    public async Task Does_not_let_a_page_it_could_not_read_write_the_tags_total()
    {
        // A soft-error page served as 200, whose only heading is its status code. T31 made
        // ParseTotalWorks refuse that heading — it requires the word beside the digits — and this
        // test is the second wall: RecordTotal runs after the readability guard, so even a heading
        // that did parse to a number could not be written by a page that parsed to no works. The
        // field is what a full sweep checks itself against before concluding works have left the
        // tag, so it is worth two independent reasons.
        _host.Http.Responds = _ => new ScrapeHttpResponse(
            "<html><body><div id='main'><h2 class='heading'>Error 404</h2></div></body></html>",
            HttpStatusCode.OK, FromCache: false, FinalUrl: "https://example.test/");

        var shipId = await FollowAsync();
        await SetTotalAsync(shipId, 4317, Jan(1));

        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(4317, ship.LastKnownTotalWorks);
        Assert.Equal(Jan(1), ship.LastKnownTotalWorksAt);
    }

    [Fact]
    public async Task Says_which_evidence_made_a_page_unreadable_rather_than_one_sentence_for_all_of_them()
    {
        // The run history is where an operator diagnoses a stuck backfill. A page reached only
        // because an earlier one offered a next link carries neither a heading nor a next link of
        // its own, so reporting it as "the listing says there are more" states the opposite of the
        // evidence printed beside it.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1)], nextPage: true),
            Page(2, []));

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Contains("page 2 was only reached because an earlier page offered a next one", outcome.ErrorMessage);
    }

    [Fact]
    public async Task Still_treats_an_empty_first_page_as_an_empty_tag()
    {
        // The other side of the rule. A tag with no works has one page, no heading count and no
        // Next link, and a backfill of it really is complete — refusing to conclude here would
        // leave the ship re-requesting an empty listing on every scheduled run forever.
        _host.Http.Responds = Pages(Page(1, []));
        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Equal(ShipBackfillState.Complete, (await ReloadAsync(shipId)).BackfillState);
    }

    [Fact]
    public async Task Reads_AO3s_own_zero_result_Listing_as_nothing_new_rather_than_an_error()
    {
        // The same rules as the two tests above, but over the page AO3 actually served instead of
        // over the Page(n, []) helper — which emits the results container unconditionally and so
        // agrees with the premise whatever the archive does. This is a real capture of a filtered
        // request matching nothing (Ao3EmptyListingTests has its shape), which is the shape of
        // every incremental pass on a ship nobody is writing for. Error here would be neither
        // Watermark nor LastPage, so the watermark would never move and every scheduled run on
        // every quiet ship would be filed as a failure for ever.
        _host.Http.Responds = Pages(new FakePage(1, Fixtures.Load(Fixtures.EmptyListing)));

        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(5));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Null(outcome.ErrorMessage);
        Assert.Equal(0, outcome.WorksSeen);
    }

    [Fact]
    public async Task Concludes_a_backfill_on_AO3s_own_zero_result_Listing()
    {
        // The unfiltered side. Note what this does *not* pin: page 1 short-circuits
        // PlausiblyTheEndOfTheListing before any heading is read, so the conclusion here rests on
        // the short-circuit alone and would be identical if the capture carried no heading at all —
        // as Still_treats_an_empty_first_page_as_an_empty_tag, whose Page(1, []) emits none,
        // demonstrates. What the capture adds is that a genuinely empty listing *does* carry a
        // "0 Works in <tag>" heading, which is what makes requiring one on page 1 safe rather than
        // a change that would strand every empty tag. Making that requirement is T80; until it
        // lands, T28's C7 is open and this test is the happy half of it.
        _host.Http.Responds = Pages(new FakePage(1, Fixtures.Load(Fixtures.EmptyListing)));
        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Equal(ShipBackfillState.Complete, (await ReloadAsync(shipId)).BackfillState);
    }

    [Fact]
    public async Task Reads_a_quiet_filtered_pass_with_a_populated_heading_as_nothing_new()
    {
        // A filtered listing's heading counts the filter's result set, not the tag — which is why
        // RecordTotal ignores it — so it cannot be held against the blurbs to detect a parse
        // failure. Were it, every quiet incremental pass on a tag whose heading still prints a
        // count would be recorded as an error, on every tick.
        _host.Http.Responds = Pages(Page(1, [], total: 4317));

        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(5));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Null(outcome.ErrorMessage);
    }

    [Fact]
    public async Task Refuses_an_empty_filtered_page_that_carries_no_heading_at_all()
    {
        // The waiver rests on the heading, so a page carrying none has not earned it. This fixture
        // is the one the waiver's first test used to build: page 2 with the listing container, no
        // Next link, no blurbs and no heading — every condition satisfied, `LastPage` concluded,
        // and the watermark moved to page 1's newest. Page 2's works are older than page 1's oldest
        // and newer than the old watermark, so the jump puts them behind the filter for good;
        // RevisedAtBound's day of slack recovers only what sits within a day of Jan 20, and a ship
        // catching up over a long gap has page 1 spanning weeks.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(20)), Blurb(2, updatedAt: Jan(15))], nextPage: true),
            Page(2, []));

        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(1));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Contains("no heading", outcome.ErrorMessage);
        Assert.Equal(Jan(1), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Still_refuses_an_empty_page_past_the_first_when_the_pass_was_not_filtered()
    {
        // The other side of the waiver. An incremental pass with no watermark asks for the whole
        // tag, so the argument the waiver sets aside is back in force: AO3 does not serve an empty
        // 200 past the last page of an unfiltered listing, and page 1's Next link is a count of the
        // tag rather than of a filter's result set.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(9))], nextPage: true),
            Page(2, []));

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
    }

    [Fact]
    public async Task Refuses_an_empty_filtered_page_whose_heading_counts_more_than_the_run_was_served()
    {
        // The waiver's boundary, and the one case where taking the page for the end costs works
        // rather than requests. A filtered heading counts the filter's result set, so a page 2
        // saying 60 works matched over a run that has been served 20 is the listing stating there
        // is more — and concluding LastPage there moves the watermark to page 1's newest, putting
        // everything between it and the old watermark behind the filter permanently. Nothing looks
        // that far back again until a full sweep exists.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(20)), Blurb(2, updatedAt: Jan(15))], nextPage: true, total: 60),
            Page(2, [], total: 60));

        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(5));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Contains("60", outcome.ErrorMessage);
        Assert.Contains("served 2", outcome.ErrorMessage);

        // The point of refusing: the watermark stays where it was, so the next tick asks again
        // rather than skipping the works the run never reached.
        Assert.Equal(Jan(5), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Ends_a_filtered_pass_on_an_empty_page_whose_heading_agrees_it_was_served_everything()
    {
        // And the case the waiver exists for, told by the same heading. The race is a work leaving
        // the filter's window between the two requests: page 1's Next link came off a count that
        // included it, page 2 is served empty, and page 2's heading — recounted for this request —
        // is down to what page 1 already held. Nothing on the page contradicts the end, so the run
        // may conclude it and the watermark may move.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(20)), Blurb(2, updatedAt: Jan(15))], nextPage: true, total: 3),
            Page(2, [], total: 2));

        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(5));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Null(outcome.ErrorMessage);
        Assert.Equal(Jan(20), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Still_refuses_an_empty_filtered_page_that_offers_a_next_one()
    {
        // Waiving `page > 1` under a filter waives that evidence and no other. A page carrying no
        // blurbs *and* a Next link contradicts itself on the same document, which is the parse
        // failure the rule is for.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(9))], nextPage: true),
            Page(2, [], nextPage: true),
            Page(3, [Blurb(2, updatedAt: Jan(8))]));

        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(5));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Contains("next one", outcome.ErrorMessage);
        Assert.Equal(Jan(5), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Still_refuses_a_filtered_page_that_carries_no_listing_at_all()
    {
        // And the load the waiver leaves entirely on HasListing: page 2 is not a results page. A
        // filter cannot make a maintenance page into the end of a listing, and this is the only
        // evidence left standing once `page > 1` is set aside.
        _host.Http.Responds = url => url.Contains("page=2", StringComparison.Ordinal)
            ? new ScrapeHttpResponse(
                "<html><body><h1>Down for maintenance</h1></body></html>",
                HttpStatusCode.OK, FromCache: false, FinalUrl: url)
            : Ok(url, Page(1, [Blurb(1, updatedAt: Jan(9))], nextPage: true).Html);

        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(5));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Contains("no listing", outcome.ErrorMessage);
        Assert.Equal(Jan(5), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    // ---- what a resumed backfill may conclude about the watermark -------------------------------------

    [Fact]
    public async Task Refuses_a_watermark_from_a_backfill_resumed_below_the_newest_page()
    {
        // The listing is revised_at desc, so a run resuming at page 2 has read only the older end
        // of it. Its newest reading is the revision time of some fairly old work, and taking that
        // for the watermark would send every later incremental pass asking for everything revised
        // since then — most of the tag, on every tick.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(9))], nextPage: true),
            Page(2, [Blurb(2, updatedAt: Jan(3))], nextPage: true),
            Page(3, [Blurb(3, updatedAt: Jan(2))]));

        var shipId = await FollowAsync();
        await ResumeBackfillAtAsync(shipId, page: 2);

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(2, outcome.FirstPage);
        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Null((await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Sets_a_watermark_from_page_1_even_when_the_run_stops_on_the_cap()
    {
        // The other half of the rule, and the reason it cannot simply be "only a completed pass
        // may propose one". Any tag big enough to need several backfill runs ends its first run on
        // the cap; if that run left no watermark, none of the resumed runs may set one either, and
        // the ship would reach Complete with a null watermark — which makes the first incremental
        // pass walk the whole catalogue and stop on the cap, forever.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(9))], nextPage: true),
            Page(2, [Blurb(2, updatedAt: Jan(3))]));

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill, OneRequest());

        Assert.Equal(ScrapeStopReason.Cap, outcome.StopReason);
        Assert.Equal(Jan(9), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Leaves_a_multi_run_backfill_with_the_watermark_its_first_page_proposed()
    {
        // End to end over the two rules above: the watermark a big tag ends up with is the newest
        // work on page 1, set by the first run, and the runs that finish the back catalogue later
        // neither lower it nor clear it.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(9))], nextPage: true),
            Page(2, [Blurb(2, updatedAt: Jan(3))], nextPage: true),
            Page(3, [Blurb(3, updatedAt: Jan(2))]));

        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill, OneRequest());
        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.Complete, ship.BackfillState);
        Assert.Equal(Jan(9), ship.IncrementalWatermarkUtc);
    }

    // ---- re-reading a work already known ---------------------------------------------------------------

    [Fact]
    public async Task Rewrites_the_statistics_of_a_work_it_has_seen_before()
    {
        // Driven through the ingestor rather than two scrapes: both stopping rules exist precisely
        // to stop a run reading the same page twice, so the scraper cannot reach this case on its own.
        var shipId = await FollowAsync();

        await _host.IngestAsync(shipId, Page(1, [Blurb(1, kudos: 10)]).Html);

        // Same revision time, more kudos — the normal case, since AO3's revision timestamp tracks
        // content and not engagement. Gating the write on it would freeze these columns forever.
        var result = await _host.IngestAsync(shipId, Page(1, [Blurb(1, kudos: 99)]).Html);

        Assert.Equal(1, result.WorksUpdated);
        Assert.Equal(0, result.WorksAdded);

        await using var db = _host.NewContext();
        Assert.Equal(99, (await db.Works.SingleAsync()).Kudos);
    }

    [Fact]
    public async Task Drops_a_tag_the_author_has_removed()
    {
        // An add-only ingest would keep a work's entire tag history forever while presenting it as
        // current.
        var shipId = await FollowAsync();

        await _host.IngestAsync(shipId, Page(1, [Blurb(1, freeforms: ["Fluff", "Angst"])]).Html);
        await _host.IngestAsync(shipId, Page(1, [Blurb(1, freeforms: ["Fluff"])]).Html);

        await using var db = _host.NewContext();
        var freeforms = await db.WorkTags
            .Where(wt => wt.Tag.Type == Ao3TagType.Freeform)
            .Select(wt => wt.Tag.Name)
            .ToListAsync();

        Assert.Equal(["Fluff"], freeforms);
    }

    [Fact]
    public async Task Sets_a_watermark_when_a_backfill_finishes()
    {
        // Without this the next incremental pass has no floor and walks the whole catalogue again,
        // undoing everything the backfill just paid for.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(9)), Blurb(2, updatedAt: Jan(3))]));
        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(Jan(9), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Names_the_page_it_saw_a_non_monotonic_boundary_on()
    {
        // A backfill walks backwards through the listing, so each page's oldest work should be older
        // than the last page's. A floor that moves *up* means the listing re-sorted under the walk,
        // and this line is how a human learns of it — T15's full sweep is what cleans up after. The
        // page number in it is the whole payload, so it has to be the page the shift was seen on and
        // not the cursor's next stop.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(3))], nextPage: true),
            Page(2, [Blurb(2, updatedAt: Jan(9))]));
        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        var boundary = Assert.Single(
            _logs.Records, r => r.Template.Contains("non-monotonic page boundary"));

        Assert.Equal(2, boundary.Value("Page"));
    }

    [Fact]
    public async Task Keeps_one_work_row_no_matter_how_many_ships_return_it()
    {
        // Works are global. Two ships listing the same work is the whole reason the schema separates
        // Work from ShipWork.
        _host.Http.Responds = Pages(Page(1, [Blurb(1)]));

        var first = await FollowAsync();
        var second = await FollowAsync("Bellamy Blake/Clarke Griffin");

        await _host.ScrapeAsync(first, ScrapeRunMode.Backfill);
        await _host.ScrapeAsync(second, ScrapeRunMode.Backfill);

        await using var db = _host.NewContext();
        Assert.Equal(1, await db.Works.CountAsync());
        Assert.Equal(2, await db.ShipWorks.CountAsync());
    }

    // ---- not scraping things that should not be scraped ---------------------------------------------------

    [Fact]
    public async Task Never_asks_AO3_about_a_tag_it_has_already_denied()
    {
        var shipId = await FollowAsync();
        await SetVerificationAsync(shipId, ShipVerificationState.NotFoundOnAo3);

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Empty(_host.Http.Requested);
        Assert.Equal(0, outcome.WorksSeen);
    }

    [Fact]
    public async Task Addresses_a_tag_by_name_even_when_its_numeric_id_is_known()
    {
        // Addressing by id would be preferable — it survives a rename mid-walk — but AO3 404s
        // /tags/{id}/works. A live run against a real tag proved it: the harvested id returned 404
        // where the name form served the same tag's index, and the walk recorded itself complete
        // having read nothing.
        _host.Http.Responds = Pages(Page(1, [Blurb(1)]));
        var shipId = await FollowAsync();
        await SetTagIdAsync(shipId, 48371);

        await _host.ScrapeAsync(shipId);

        Assert.DoesNotContain("/tags/48371/works", _host.Http.Requested[0]);
        Assert.Contains("Clarke%20Griffin*s*Lexa", _host.Http.Requested[0]);
    }

    [Fact]
    public async Task Survives_a_request_timeout_instead_of_letting_it_escape()
    {
        // HttpClient reports its own Timeout as a TaskCanceledException, which is an
        // OperationCanceledException. On the first live scrape one of these passed through both the
        // scraper's handler and the worker's, failed the BackgroundService, and stopped the host —
        // the whole API taken down by one slow page.
        _host.Http.Fails = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.");

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, budget: OneRequest());

        // Reported as a failed run, not thrown.
        Assert.Equal(0, outcome.WorksSeen);
        Assert.Equal(1, outcome.RequestsMade);
    }

    [Fact]
    public async Task Refuses_to_call_a_backfill_complete_when_the_first_page_404s()
    {
        // The end of a listing is a 404 *past* a page that was read. A 404 on the first request
        // means the address was wrong or the tag is gone — marking that complete retires a ship
        // that has never been read at all.
        _host.Http.Responds = url =>
            new ScrapeHttpResponse("", HttpStatusCode.NotFound, FromCache: false, FinalUrl: url);

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.NotEqual(ShipBackfillState.Complete, (await ReloadAsync(shipId)).BackfillState);
    }

    // ---- a page the archive will not serve -----------------------------------------------------------

    [Fact]
    public async Task Asks_once_for_a_page_the_archive_refuses()
    {
        // RateLimitedAo3HttpClient has already retried a 429 or a 5xx up to MaxRetries times with
        // backoff by the time one arrives here, so re-asking from the walk multiplies a request AO3
        // has already refused several times over. This branch used to `continue` without advancing
        // `page`, which re-sent the identical URL until the circuit breaker tripped.
        _host.Http.Responds = url => url.Contains("page=2")
            ? new ScrapeHttpResponse("", HttpStatusCode.InternalServerError, FromCache: false, FinalUrl: url)
            : Ok(url, Page(1, [Blurb(1)], nextPage: true).Html);

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(1, _host.Http.Requested.Count(url => url.Contains("page=2")));
        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
    }

    [Fact]
    public async Task Records_the_status_that_stopped_the_run()
    {
        // "Something went wrong" in the run history is not actionable; the status is. Reporting it
        // as the breaker — which is what re-asking until it tripped used to do — actively misleads,
        // blaming the archive for being down when one page was refusing.
        _host.Http.Responds = url => url.Contains("page=2")
            ? new ScrapeHttpResponse("", HttpStatusCode.Forbidden, FromCache: false, FinalUrl: url)
            : Ok(url, Page(1, [Blurb(1)], nextPage: true).Html);

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Contains("403", outcome.ErrorMessage);
    }

    [Fact]
    public async Task Keeps_what_it_read_before_the_page_that_failed()
    {
        _host.Http.Responds = url => url.Contains("page=2")
            ? new ScrapeHttpResponse("", HttpStatusCode.InternalServerError, FromCache: false, FinalUrl: url)
            : Ok(url, Page(1, [Blurb(1)], nextPage: true).Html);

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(1, outcome.WorksAdded);

        // Not "left behind": the cursor still points at the page that failed, so the next run asks
        // for it again — once, at the scheduler's spacing rather than in a tight loop. Skipping it
        // would lose every work on it with nothing ever going back.
        Assert.Equal(2, (await ReloadAsync(shipId)).BackfillNextPage);
    }

    [Fact]
    public async Task Leaves_the_watermark_alone_when_a_page_fails()
    {
        // The works on the pages the run never reached are older than the newest it did read, so a
        // watermark moved here would put them permanently out of reach of any later pass.
        _host.Http.Responds = url => url.Contains("page=2")
            ? new ScrapeHttpResponse("", HttpStatusCode.InternalServerError, FromCache: false, FinalUrl: url)
            : Ok(url, Page(1, [Blurb(1)], nextPage: true).Html);

        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId);

        Assert.Null((await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    // ---- a page an incremental pass cannot get past ---------------------------------------------

    // The shape all of these are about: a watermark, a page 1 entirely newer than it that offers a
    // next link, and a page 2 that will not answer. Nothing may conclude the listing ended there —
    // page 2's works are older than page 1's and a watermark moved past them is one no later pass
    // looks behind — so the run stops with Error, the watermark stays put, and the next run rebuilds
    // the identical two requests. For ever, until something bounds it.

    [Fact]
    public async Task Stops_asking_for_a_page_that_has_not_answered_for_the_last_few_runs()
    {
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(20))], nextPage: true));
        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(1));

        // Exactly the threshold, spelled as the constant rather than as three rows: the streak is
        // counted over a window, and a window that did not grow with the threshold would cap the
        // count below it and turn the hold off entirely — silently, and for every ship. Written
        // this way the test follows the constant wherever it is tuned to.
        await RecordIncrementalRunsAsync(
            shipId,
            [.. Enumerable.Repeat(Failed(1), Ao3ShipIndexScraper.MinStuckIncrementalRuns)]);

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Held, outcome.StopReason);
        Assert.DoesNotContain(_host.Http.Requested, url => url.Contains("page=2"));
        Assert.Equal(1, outcome.RequestsMade);
    }

    [Fact]
    public async Task Holds_a_page_AO3_answers_with_an_unreadable_listing_as_readily_as_one_it_404s()
    {
        // The second way into the stuck state, and the one T47 widened the entrance for: page 2 is
        // not a 404 but a 200 carrying the listing container, no blurbs, no Next link and no heading
        // to say the date filter's results ended. It stops with Error rather than concluding, for
        // the reasons FilteredHeadingSaysThisIsAll spells out — and it leaves the same signature
        // behind, a run that read up to page 1 and stopped. The bound reads where a run got to and
        // not why it stopped, which is what makes one rule cover both entrances.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(20))], nextPage: true),
            Page(2, []));
        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(1));
        await RecordIncrementalRunsAsync(shipId, Failed(1), Failed(1), Failed(1));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Held, outcome.StopReason);
        Assert.DoesNotContain(_host.Http.Requested, url => url.Contains("page=2"));
    }

    [Fact]
    public async Task Keeps_asking_for_a_page_that_has_only_just_started_refusing()
    {
        // Two runs is a page that failed, not a page that is refusing. The hold costs a ship the
        // rest of its listing until the next probe, so it is not worth paying for one bad afternoon.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(20))], nextPage: true));
        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(1));
        await RecordIncrementalRunsAsync(shipId, Failed(1), Failed(1));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Contains(_host.Http.Requested, url => url.Contains("page=2"));
    }

    [Fact]
    public async Task Holding_a_page_leaves_the_watermark_exactly_where_the_failures_left_it()
    {
        // The whole reason the erroring runs could not be left to conclude anything. Page 2 holds
        // works older than page 1's Jan 20 and newer than the Jan 1 watermark; moving the watermark
        // to Jan 20 would put every one of them out of reach of any later incremental pass.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(20))], nextPage: true));
        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(1));
        await RecordIncrementalRunsAsync(shipId, Failed(1), Failed(1), Failed(1));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Held, outcome.StopReason);
        Assert.Equal(Jan(1), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Ingests_the_new_works_on_the_page_before_the_one_it_is_holding()
    {
        // The request that is being kept is the one doing the work: page 1 is where new works
        // appear, and a held run reads it exactly as a healthy one would. A bound that stopped the
        // pass outright would be the give-up this product cannot have.
        _host.Http.Responds = Pages(Page(1, [Blurb(7, updatedAt: Jan(20))], nextPage: true));
        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(1));
        await RecordIncrementalRunsAsync(shipId, Failed(1), Failed(1), Failed(1));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Held, outcome.StopReason);
        Assert.Equal(1, outcome.WorksAdded);

        await using var db = _host.NewContext();
        Assert.Equal([7], await db.Works.Select(w => w.Id).ToListAsync());
    }

    [Fact]
    public async Task Asks_a_held_page_again_once_enough_runs_have_held_it()
    {
        // The hold has to be temporary. Nothing here knows why the page stopped answering, so a
        // listing that heals must be found without an operator noticing — one request every
        // ProbeHeldPageEveryNthRun runs is what that costs.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(20))], nextPage: true));
        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(1));
        await RecordIncrementalRunsAsync(
            shipId,
            [Failed(1), Failed(1), Failed(1),
             .. Enumerable.Repeat(Held(1), Ao3ShipIndexScraper.ProbeHeldPageEveryNthRun)]);

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Contains(_host.Http.Requested, url => url.Contains("page=2"));
        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
    }

    [Fact]
    public async Task Counts_its_own_held_runs_towards_the_streak_that_holds_the_page()
    {
        // A run that did not ask is no evidence the page recovered. Dropping the held runs from the
        // streak would end it on the first one and put the every-tick request straight back.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(20))], nextPage: true));
        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(1));
        await RecordIncrementalRunsAsync(shipId, Failed(1), Failed(1), Failed(1), Held(1));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Held, outcome.StopReason);
        Assert.DoesNotContain(_host.Http.Requested, url => url.Contains("page=2"));
    }

    [Fact]
    public async Task Holds_no_page_for_a_run_history_that_never_read_one()
    {
        // Runs that read no page name no page to hold at, and "the page after none" is page 1 —
        // which the hold, sitting below the read, turns into refusing page 2. That would spend three
        // failures that never got as far as asking for page 2 on a ship whose page 1 is now
        // answering perfectly well.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(20))], nextPage: true),
            Page(2, [Blurb(2, updatedAt: Jan(15))]));
        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(1));
        await RecordIncrementalRunsAsync(shipId, Failed(null), Failed(null), Failed(null));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Equal(2, outcome.PagesFetched);
    }

    [Fact]
    public async Task Reads_a_listing_that_has_since_ended_rather_than_holding_at_it()
    {
        // The hold sits below the last-page stop on purpose: it is a rule about asking for the next
        // page, not about reading this one. A page 1 that no longer offers a next link is the end of
        // the listing on the listing's own word, and that run is healthy and moves the watermark.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(20))]));
        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(1));
        await RecordIncrementalRunsAsync(shipId, Failed(1), Failed(1), Failed(1));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Equal(Jan(20), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Holds_at_the_page_the_failing_runs_reached_rather_than_at_page_one()
    {
        // The streak names its own page. A ship that gets two pages in before the refusal keeps both
        // of them; holding at page 1 would throw away a page that answers perfectly well.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(20))], nextPage: true),
            Page(2, [Blurb(2, updatedAt: Jan(15))], nextPage: true));
        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(1));
        await RecordIncrementalRunsAsync(shipId, Failed(2), Failed(2), Failed(2));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Held, outcome.StopReason);
        Assert.Equal(2, outcome.PagesFetched);
        Assert.DoesNotContain(_host.Http.Requested, url => url.Contains("page=3"));
    }

    [Fact]
    public async Task Holds_nothing_when_the_last_run_stopped_somewhere_else()
    {
        // A streak is consecutive by definition. One healthy run between the failures and the ship
        // is not stuck — and because the streak is read from the run history rather than counted
        // into a column, there is nothing to remember to reset for that to be true.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(20))], nextPage: true));
        var shipId = await FollowAsync();
        await SetWatermarkAsync(shipId, Jan(1));
        await RecordIncrementalRunsAsync(
            shipId, Failed(1), Failed(1), Failed(1), (ScrapeStopReason.Watermark, 1));

        var outcome = await _host.ScrapeAsync(shipId);

        Assert.Equal(ScrapeStopReason.Error, outcome.StopReason);
        Assert.Contains(_host.Http.Requested, url => url.Contains("page=2"));
    }

    [Fact]
    public async Task Holds_nothing_for_a_backfill()
    {
        // A backfill has a cursor to carry the question into the next run and MaxStalledBackfillRuns
        // to bound it; the hold is for the pass that has neither. Applying it to a backfill would
        // stop the walk at the page the *incremental* runs got stuck on, which has nothing to do
        // with where the back catalogue is being read.
        _host.Http.Responds = Pages(
            Page(1, [Blurb(1, updatedAt: Jan(20))], nextPage: true),
            Page(2, [Blurb(2, updatedAt: Jan(15))]));
        var shipId = await FollowAsync();
        await RecordIncrementalRunsAsync(shipId, Failed(1), Failed(1), Failed(1));

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Equal(2, outcome.PagesFetched);
    }

    [Fact]
    public async Task Bounds_a_stuck_incremental_pass_over_consecutive_runs_through_the_worker()
    {
        // The whole loop, end to end, because it is split across two classes: the walk reads a
        // streak the *worker* writes, and it reads it while its own row is open and still says
        // nothing about where it got to. Every other test in this section calls the scraper
        // directly, so none of them would notice the run in flight being taken for the most recent
        // finished one — which reads back as "no page read", holds nothing, and would leave the
        // bound never firing on a real instance while every test here passed.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(20))], nextPage: true));
        var shipId = await FollowAsync();
        await _host.SaveAo3LoginAsync();
        await SetWatermarkAsync(shipId, Jan(1));
        await SettleBackfillAsync(shipId);

        for (var i = 0; i < Ao3ShipIndexScraper.MinStuckIncrementalRuns + 1; i++)
        {
            await MakeDueAsync(shipId);
            await _host.NewScrapeWorker().RunDueJobsAsync(CancellationToken.None);
        }

        await using var db = _host.NewContext();

        Assert.Equal(
            [ScrapeStopReason.Error, ScrapeStopReason.Error, ScrapeStopReason.Error, ScrapeStopReason.Held],
            await db.ScrapeRuns.OrderBy(r => r.Id).Select(r => r.StopReason).ToListAsync());

        // Two requests each for the three that asked, one for the run that held.
        Assert.Equal(7, _host.Http.Requested.Count);
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private static ScrapeBudget OneRequest() => new(maxRequests: 1, maxConsecutiveFailures: 3, maxDuration: TimeSpan.FromHours(1));

    private static DateTime Jan(int day) => new(2023, 1, day, 12, 0, 0, DateTimeKind.Utc);

    private async Task<int> FollowAsync(string tagName = Lexa)
    {
        var result = await _host.Ships(_host.SeedUser(Guid.NewGuid().ToString("N")[..8]))
            .WatchShip(new AddWatchedShipRequest(tagName), CancellationToken.None);

        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    private async Task<Ship> ReloadAsync(int shipId)
    {
        await using var db = _host.NewContext();
        return await db.Ships.SingleAsync(s => s.Id == shipId);
    }

    private async Task SetWatermarkAsync(int shipId, DateTime watermark)
    {
        await using var db = _host.NewContext();
        (await db.Ships.SingleAsync(s => s.Id == shipId)).IncrementalWatermarkUtc = watermark;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Puts a total on the ship as an earlier unfiltered pass would have left it, so a later pass
    /// overwriting it is visible as a change rather than as a first write.
    /// </summary>
    private async Task SetTotalAsync(int shipId, int total, DateTime readAt, bool authenticated = false)
    {
        await using var db = _host.NewContext();
        var ship = await db.Ships.SingleAsync(s => s.Id == shipId);
        ship.LastKnownTotalWorks = total;
        ship.LastKnownTotalWorksAt = readAt;
        ship.LastKnownTotalWasAuthenticated = authenticated;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Puts a ship where a previous run's cap would have left it: backfill under way, cursor part
    /// of the way into the listing. The scraper reads this cursor to pick its starting page.
    /// </summary>
    private async Task ResumeBackfillAtAsync(int shipId, int page)
    {
        await using var db = _host.NewContext();
        var ship = await db.Ships.SingleAsync(s => s.Id == shipId);
        ship.BackfillState = ShipBackfillState.InProgress;
        ship.BackfillStartedAt = Jan(1);
        ship.BackfillNextPage = page;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Puts a stalled streak on the ship, as consecutive runs against an unanswerable cursor would
    /// have left it.
    /// </summary>
    private async Task SetStalledRunsAsync(int shipId, int runs)
    {
        await using var db = _host.NewContext();
        (await db.Ships.SingleAsync(s => s.Id == shipId)).BackfillStalledRuns = runs;
        await db.SaveChangesAsync();
    }

    /// <summary>A finished incremental run that stopped with an error having read up to <paramref name="lastPage"/>.</summary>
    private static (string StopReason, int? LastPage) Failed(int? lastPage) => (ScrapeStopReason.Error, lastPage);

    /// <summary>A finished incremental run that held rather than asking for the page after <paramref name="lastPage"/>.</summary>
    private static (string StopReason, int? LastPage) Held(int? lastPage) => (ScrapeStopReason.Held, lastPage);

    /// <summary>
    /// Writes the run history earlier incremental passes would have left, oldest first — which is
    /// also insertion order, and so Id order, which is the order the walk reads them back in.
    ///
    /// Rows rather than repeated scrapes because the scraper is being called directly here: the
    /// worker is what turns an outcome into a ScrapeRun, and going through it to arrange a streak
    /// would make every one of these tests a test of the worker as well.
    /// </summary>
    private async Task RecordIncrementalRunsAsync(int shipId, params (string StopReason, int? LastPage)[] runs)
    {
        await using var db = _host.NewContext();
        var job = await db.ScrapeJobs.FirstAsync(j => j.ShipId == shipId);

        foreach (var (stopReason, lastPage) in runs)
        {
            db.ScrapeRuns.Add(new ScrapeRun
            {
                ScrapeJobId = job.Id,
                Mode = ScrapeRunMode.Incremental,
                Status = stopReason is ScrapeStopReason.Error or ScrapeStopReason.Held
                    ? ScrapeRunStatus.Failed
                    : ScrapeRunStatus.Succeeded,
                StopReason = stopReason,
                LastPageFetched = lastPage,
                StartedAt = Jan(1),
                CompletedAt = Jan(1),
            });

            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Puts the ship past its back catalogue, which is what makes the worker choose the incremental
    /// pass — it backfills anything NotStarted or InProgress.
    /// </summary>
    private async Task SettleBackfillAsync(int shipId)
    {
        await using var db = _host.NewContext();
        var ship = await db.Ships.SingleAsync(s => s.Id == shipId);
        ship.BackfillState = ShipBackfillState.Complete;
        ship.BackfillCompletedAt = Jan(1);
        await db.SaveChangesAsync();
    }

    /// <summary>Makes the ship's job due again, as waiting out its interval would.</summary>
    private async Task MakeDueAsync(int shipId)
    {
        await using var db = _host.NewContext();
        (await db.ScrapeJobs.FirstAsync(j => j.ShipId == shipId)).NextRunAt = null;
        await db.SaveChangesAsync();
    }

    private async Task SetVerificationAsync(int shipId, ShipVerificationState state)
    {
        await using var db = _host.NewContext();
        (await db.Ships.SingleAsync(s => s.Id == shipId)).VerificationState = state;
        await db.SaveChangesAsync();
    }

    private async Task SetTagIdAsync(int shipId, long tagId)
    {
        await using var db = _host.NewContext();
        (await db.Ships.SingleAsync(s => s.Id == shipId)).Ao3TagId = tagId;
        await db.SaveChangesAsync();
    }

    private static ScrapeHttpResponse Ok(string url, string html) =>
        new(html, HttpStatusCode.OK, FromCache: false, FinalUrl: url);

    /// <summary>
    /// Answers each request with the page its query string asks for. Page 1 carries no
    /// <c>page=</c> parameter, which is what the scraper actually sends.
    /// </summary>
    private static Func<string, ScrapeHttpResponse> Pages(params FakePage[] pages) => url =>
    {
        var number = 1;
        var marker = url.IndexOf("page=", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var digits = new string([.. url[(marker + 5)..].TakeWhile(char.IsDigit)]);
            number = int.Parse(digits);
        }

        var page = pages.FirstOrDefault(p => p.Number == number);
        return page is null
            ? new ScrapeHttpResponse("", HttpStatusCode.NotFound, FromCache: false, FinalUrl: url)
            : Ok(url, page.Html);
    };

    private sealed record FakePage(int Number, string Html);

    /// <summary>
    /// A blurb the parser selects and then cannot name: <c>li.blurb</c> with a <c>work_</c> id
    /// carrying no number and no heading link to fall back to, which is what
    /// <see cref="Ao3Tracker.Api.Services.Scraping.Ao3BlurbParser"/> counts a parse warning for.
    /// A blurb with no <c>work_</c> id at all is never selected, so it produces no warning either.
    /// </summary>
    private static string Nameless() => """<li id="work_" class="work blurb group"></li>""";

    private static FakePage Page(int number, string[] blurbs, bool nextPage = false, int? total = null) =>
        new(number, $"""
            <div id="main">
              {(total is null ? "" : $"<h2 class='heading'>1 - 20 of {total} Works in {Lexa}</h2>")}
              <ol class="work index group">{string.Join('\n', blurbs)}</ol>
              {(nextPage ? """<ol class="pagination actions"><li><a href="?page=next">Next &rarr;</a></li></ol>""" : "")}
            </div>
            """);

    /// <summary>
    /// One blurb. <paramref name="undated"/> renders the shape AO3 has served on occasion and the
    /// parser reports as DateTime.MinValue: no <c>updated_at</c> comment, and a visible date in
    /// none of the formats it knows.
    /// </summary>
    private static string Blurb(
        long id,
        DateTime? updatedAt = null,
        int kudos = 10,
        string[]? freeforms = null,
        bool undated = false,
        bool restricted = false)
    {
        var epoch = new DateTimeOffset(updatedAt ?? Jan(1)).ToUnixTimeSeconds();
        var tags = string.Join('\n', (freeforms ?? ["Fluff"])
            .Select(f => $"""<li class="freeforms"><a class="tag" href="/tags/{f}/works">{f}</a></li>"""));

        return $"""
            <li id="work_{id}" class="work blurb group">
              <div class="header module">
                <h4 class="heading">
                  {(restricted ? """<img class="symbol" title="Restricted" alt="Restricted" />""" : "")}
                  <a href="/works/{id}">Work {id}</a>
                  by <a rel="author" href="/users/someuser/pseuds/somepseud">somepseud (someuser)</a>
                </h4>
                <ul class="required-tags">
                  <li><span class="rating-teen rating" title="Teen And Up Audiences"></span></li>
                  <li><span class="warning-no warnings" title="No Archive Warnings Apply"></span></li>
                  <li><span class="category-femslash category" title="F/F"></span></li>
                  <li><span class="complete-yes iswip" title="Complete Work"></span></li>
                </ul>
                {(undated ? "" : $"<!-- updated_at={epoch} -->")}
                <p class="datetime">{(undated ? "some time ago" : "1 Jan 2023")}</p>
              </div>
              <ul class="tags commas">
                <li class="relationships"><a class="tag" href="/tags/lexa/works">{Lexa}</a></li>
                {tags}
              </ul>
              <dl class="stats">
                <dt class="words">Words:</dt><dd class="words">1,000</dd>
                <dt class="chapters">Chapters:</dt><dd class="chapters">1/1</dd>
                <dt class="kudos">Kudos:</dt><dd class="kudos">{kudos}</dd>
              </dl>
            </li>
            """;
    }
}
