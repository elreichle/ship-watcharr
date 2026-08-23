using System.Net;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    private readonly LibraryTestHost _host = new();

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
        _host.Http.Responds = Pages(Page(1, [Blurb(1, updatedAt: Jan(9), restricted: true)], total: 2));

        var shipId = await FollowAsync();
        await SetTotalAsync(shipId, 4317, readAt: Jan(1));
        await SetWatermarkAsync(shipId, Jan(5));

        await _host.ScrapeAsync(shipId);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(4317, ship.LastKnownTotalWorks);
        Assert.False(ship.LastKnownTotalWasAuthenticated);
    }

    [Fact]
    public async Task Records_that_an_unfiltered_pass_read_the_total_while_logged_in()
    {
        // The other side of it: an unfiltered pass writes the total, so the flag describing that
        // total is its to set.
        _host.Http.Responds = Pages(Page(1, [Blurb(1, restricted: true)], total: 4317));

        var shipId = await FollowAsync();

        await _host.ScrapeAsync(shipId);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(4317, ship.LastKnownTotalWorks);
        Assert.True(ship.LastKnownTotalWasAuthenticated);
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
    public async Task Treats_a_404_past_the_last_page_as_the_end_of_the_walk()
    {
        // AO3 404s rather than serving an empty page past the end of a listing.
        _host.Http.Responds = url => url.Contains("page=2")
            ? new ScrapeHttpResponse("", HttpStatusCode.NotFound, FromCache: false, FinalUrl: url)
            : Ok(url, Page(1, [Blurb(1)], nextPage: true).Html);

        var shipId = await FollowAsync();

        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Equal(ShipBackfillState.Complete, (await ReloadAsync(shipId)).BackfillState);
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
        // ParseTotalWorks falls back to the trailing digits of any h2.heading when it finds no
        // "Works", so a soft-error page served as 200 offers "404" as the tag's size. RecordTotal
        // runs after the readability guard for this reason: the field is what a full sweep checks
        // itself against before concluding works have left the tag.
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
    private async Task SetTotalAsync(int shipId, int total, DateTime readAt)
    {
        await using var db = _host.NewContext();
        var ship = await db.Ships.SingleAsync(s => s.Id == shipId);
        ship.LastKnownTotalWorks = total;
        ship.LastKnownTotalWorksAt = readAt;
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
