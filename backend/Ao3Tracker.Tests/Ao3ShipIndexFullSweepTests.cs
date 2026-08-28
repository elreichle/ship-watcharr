using System.Net.Http;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Ao3Tracker.Tests.Ao3ListingFixtures;

namespace Ao3Tracker.Tests;

/// <summary>
/// The full sweep: the only pass in this application permitted to conclude that a work has left a
/// tag.
///
/// Every test here is really about that entitlement rather than about walking, because the walking
/// is the same walk the other two passes make and is covered by their tests. What is this pass's
/// own is what it may say afterwards — and the failure mode being guarded is not a wasted request
/// but a library quietly disagreeing with AO3 in whichever direction the sweep got wrong.
/// </summary>
public class Ao3ShipIndexFullSweepTests : IDisposable
{
    private readonly CapturingLoggerProvider _logs = new();
    private readonly LibraryTestHost _host;

    public Ao3ShipIndexFullSweepTests() =>
        _host = new LibraryTestHost(services =>
        {
            services.AddSingleton<ILoggerProvider>(_logs);

            // See the same registration in Ao3ShipIndexScraperTests: ScraperRegistry resolves
            // IAo3Scraper, and without this a worker run in this class would find no scraper.
            services.AddScoped<IAo3Scraper>(sp => sp.GetRequiredService<Ao3ShipIndexScraper>());
        });

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- what a completed sweep concludes ------------------------------------------------------

    [Fact]
    public async Task Marks_a_work_the_completed_sweep_did_not_see_as_having_left_the_tag()
    {
        var shipId = await AShipHoldingAsync(1, 2);

        await SweepAsync(Page(1, [Blurb(1)], total: 1));

        Assert.Null(await MissingSinceAsync(shipId, 1));
        Assert.Equal(_host.Clock.Now.UtcDateTime, await MissingSinceAsync(shipId, 2));
    }

    [Fact]
    public async Task Leaves_the_work_row_itself_alone_when_it_leaves_a_tag()
    {
        // Leaving a tag is not deletion. Other ships may hold the work, and its reader may have
        // rated, noted or downloaded it — so the mark is on the ship's claim, never on the work.
        var shipId = await AShipHoldingAsync(1, 2);

        await SweepAsync(Page(1, [Blurb(1)], total: 1));

        await using var db = _host.NewContext();
        var work = await db.Works.SingleAsync(w => w.Id == 2);
        Assert.False(work.IsDeleted);
        Assert.Null(work.DeletedAt);
        Assert.Equal(2, await db.Works.CountAsync());
    }

    [Fact]
    public async Task Records_the_sweep_as_finished_and_puts_its_cursor_away()
    {
        var shipId = await AShipHoldingAsync(1);

        await SweepAsync(Page(1, [Blurb(1)], total: 1));

        var ship = await ReloadAsync(shipId);
        Assert.Equal(_host.Clock.Now.UtcDateTime, ship.LastFullSweepCompletedAt);
        Assert.Null(ship.FullSweepNextPage);
    }

    [Fact]
    public async Task A_work_that_comes_back_stops_being_missing()
    {
        // The clearing half is WorkIngestor's and predates this pass, but nothing had ever set the
        // mark for it to clear — so until now it was a rule with only one side proved.
        var shipId = await AShipHoldingAsync(1, 2);
        await SweepAsync(Page(1, [Blurb(1)], total: 1));
        Assert.NotNull(await MissingSinceAsync(shipId, 2));

        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        await SweepAsync(Page(1, [Blurb(1), Blurb(2)], total: 2));

        Assert.Null(await MissingSinceAsync(shipId, 2));
    }

    [Fact]
    public async Task Keeps_the_date_a_work_first_went_missing()
    {
        // A later sweep re-passing the same absent work must not restamp it: the column says when
        // the work went missing, and a reader deciding whether to care is reading exactly that.
        var shipId = await AShipHoldingAsync(1, 2);
        await SweepAsync(Page(1, [Blurb(1)], total: 1));
        var first = await MissingSinceAsync(shipId, 2);

        _host.Clock.Now = _host.Clock.Now.AddDays(40);
        await SweepAsync(Page(1, [Blurb(1)], total: 1));

        Assert.Equal(first, await MissingSinceAsync(shipId, 2));
    }

    // ---- what an interrupted sweep concludes ---------------------------------------------------

    [Fact]
    public async Task Concludes_nothing_from_a_sweep_that_ran_out_of_budget_part_way()
    {
        // The rule the whole pass rests on: absence from a walk that stopped early is not absence
        // from the tag. This run read page 1 and never asked for page 2, where work 2 might be.
        var shipId = await AShipHoldingAsync(1, 2);

        await SweepAsync(
            OneRequest(),
            Page(1, [Blurb(1)], nextPage: true, total: 2),
            Page(2, [Blurb(2)], total: 2));

        Assert.Null(await MissingSinceAsync(shipId, 2));

        var ship = await ReloadAsync(shipId);
        Assert.Null(ship.LastFullSweepCompletedAt);
        Assert.Equal(2, ship.FullSweepNextPage);
    }

    [Fact]
    public async Task Resumes_at_its_cursor_and_concludes_only_when_the_walk_reaches_the_end()
    {
        // The multi-run case, which is the ordinary one: a listing longer than a run's budget is
        // swept over several runs, and the start it measures absence from is the first run's.
        var shipId = await AShipHoldingAsync(1, 2, 3);

        await SweepAsync(
            OneRequest(),
            Page(1, [Blurb(1)], nextPage: true, total: 2),
            Page(2, [Blurb(2)], total: 2));

        var startedAt = (await ReloadAsync(shipId)).LastFullSweepStartedAt;
        _host.Clock.Now = _host.Clock.Now.AddHours(1);

        await SweepAsync(
            Page(1, [Blurb(1)], nextPage: true, total: 2),
            Page(2, [Blurb(2)], total: 2));

        var ship = await ReloadAsync(shipId);
        Assert.Equal(startedAt, ship.LastFullSweepStartedAt);
        Assert.Null(ship.FullSweepNextPage);

        // Work 1 was seen by the first run of this sweep and work 2 by the second; only work 3,
        // seen by neither, has left the tag.
        Assert.Null(await MissingSinceAsync(shipId, 1));
        Assert.Null(await MissingSinceAsync(shipId, 2));
        Assert.NotNull(await MissingSinceAsync(shipId, 3));
    }

    [Fact]
    public async Task Abandons_a_sweep_that_got_no_further_than_the_page_it_started_on()
    {
        // A sweep that spins is worse than one that gives up: it concludes nothing either way, and
        // every tick it spends is a tick the ship's incremental pass did not get.
        var shipId = await AShipHoldingAsync(1, 2);

        await SweepAsync(
            OneRequest(),
            Page(1, [Blurb(1)], nextPage: true, total: 2),
            Page(2, [Blurb(2)], total: 2));
        Assert.Equal(2, (await ReloadAsync(shipId)).FullSweepNextPage);

        // Page 2 is now the page nothing answers for, and page 1 before it still offers a next
        // link — so the run retreats, learns nothing, and ends where it started.
        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        await SweepAsync(Page(1, [Blurb(1)], nextPage: true, total: 2));

        var ship = await ReloadAsync(shipId);
        Assert.Null(ship.FullSweepNextPage);
        Assert.Null(ship.LastFullSweepCompletedAt);
        Assert.Null(await MissingSinceAsync(shipId, 2));
    }

    [Fact]
    public async Task Abandons_a_sweep_that_read_a_page_without_a_session()
    {
        // Restricted works are invisible to a logged-out request, so an anonymous page has shown
        // this sweep a smaller tag than the one whose works it would declare gone. The sweep is
        // abandoned rather than merely stopped from concluding, and that is what makes the rule
        // hold over the several runs one sweep takes: there is no later run of it to conclude on
        // the strength of a page nobody remembers was anonymous.
        var shipId = await AShipHoldingAsync(1, 2);

        _host.Http.Responds = Pages(Page(1, [Blurb(1)], total: 1));
        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        await _host.ScrapeAsync(shipId, ScrapeRunMode.FullSweep);

        Assert.Null(await MissingSinceAsync(shipId, 2));

        var ship = await ReloadAsync(shipId);
        Assert.Null(ship.FullSweepNextPage);
        Assert.Null(ship.LastFullSweepCompletedAt);
        Assert.Contains(_logs.Records, r => r.Template.Contains("was served without a session"));
    }

    [Fact]
    public async Task Does_not_conclude_against_a_total_an_earlier_run_read()
    {
        // The flag says whether the request that read the *stored* total carried a session, so a
        // sweep whose own pages carried no heading would otherwise be concluding against some
        // earlier run's reading. It concludes against a number it read itself, or not at all —
        // which is the half of the session rule the per-page check above cannot see.
        var shipId = await AShipHoldingAsync(1, 2);
        await SetTotalAsync(shipId, total: 2, authenticated: true);

        await SweepAsync(Page(1, [Blurb(1)]));

        Assert.Null(await MissingSinceAsync(shipId, 2));
    }

    [Fact]
    public async Task A_sweep_that_concluded_nothing_is_not_recorded_as_a_completed_one()
    {
        // `LastFullSweepCompletedAt` is what an operator reads as "the last time this ship's whole
        // listing was walked". A walk that reached the end and then declined to say anything has
        // not done that, and writing the date anyway would hide the declining behind a green
        // column — the same conflation the run history's status rules exist to prevent.
        var shipId = await AShipHoldingAsync(1, 2);
        await SetTotalAsync(shipId, total: 2, authenticated: true);

        await SweepAsync(Page(1, [Blurb(1)]));

        var ship = await ReloadAsync(shipId);
        Assert.Null(ship.LastFullSweepCompletedAt);
        Assert.Null(ship.FullSweepNextPage);
    }

    [Fact]
    public async Task Keeps_a_sweep_in_flight_when_the_archive_told_the_run_nothing()
    {
        // An afternoon of AO3 being unreachable must not cost a sweep. Abandoning here would also
        // cost the ship a whole interval of absence detection, because the next sweep is spaced
        // from this one's start — on the strength of a run that read nothing at all.
        var shipId = await AShipHoldingAsync(1, 2);

        await SweepAsync(
            OneRequest(),
            Page(1, [Blurb(1)], nextPage: true, total: 2),
            Page(2, [Blurb(2)], total: 2));
        Assert.Equal(2, (await ReloadAsync(shipId)).FullSweepNextPage);

        var startedAt = (await ReloadAsync(shipId)).LastFullSweepStartedAt;
        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        _host.Http.Responds = _ => throw new HttpRequestException("connection reset by peer");
        await _host.ScrapeAsync(shipId, ScrapeRunMode.FullSweep);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(2, ship.FullSweepNextPage);
        Assert.Equal(startedAt, ship.LastFullSweepStartedAt);
    }

    // ---- what a sweep must not touch -----------------------------------------------------------

    [Fact]
    public async Task Does_not_move_the_watermark()
    {
        // The sweep reads the listing in posting order, so the newest thing on its page 1 is the
        // newest work posted and not the newest revised. Made the watermark, it would send every
        // later incremental pass past everything revised in between.
        var shipId = await AShipHoldingAsync(1);
        await SetWatermarkAsync(shipId, Jan(1));

        await SweepAsync(Page(1, [Blurb(1, updatedAt: Jan(20))], total: 1));

        Assert.Equal(Jan(1), (await ReloadAsync(shipId)).IncrementalWatermarkUtc);
    }

    [Fact]
    public async Task Asks_for_the_listing_in_posting_order_and_asks_for_all_of_it()
    {
        // Two requirements of one URL. The sort order is what keeps a multi-run sweep reading the
        // same listing it started on; the absence of a revised_at bound is what makes it a sweep
        // rather than another incremental pass, and is also what entitles its heading to be read
        // as the tag's own total.
        var shipId = await AShipHoldingAsync(1);
        await SetWatermarkAsync(shipId, Jan(1));

        var urls = new List<string>();
        _host.Http.Responds = url =>
        {
            urls.Add(url);
            return LoggedInPages(Page(1, [Blurb(1)], total: 1))(url);
        };
        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        await _host.ScrapeAsync(shipId, ScrapeRunMode.FullSweep);

        var url = Assert.Single(urls);
        Assert.Contains("work_search%5Bsort_column%5D=created_at", url);
        Assert.DoesNotContain("revised_at", url);
    }

    // ---- which pass a ship gets ----------------------------------------------------------------

    [Fact]
    public async Task The_worker_sweeps_a_ship_whose_listing_was_last_walked_in_full_long_enough_ago()
    {
        var modes = await ModesTheWorkerChoseAsync(ship =>
        {
            ship.BackfillState = ShipBackfillState.Complete;
            ship.BackfillCompletedAt = DateTime.UtcNow - ScrapeWorker.FullSweepInterval - TimeSpan.FromDays(1);
        });

        Assert.Equal([ScrapeRunMode.FullSweep], modes);
    }

    [Fact]
    public async Task The_worker_reads_the_newest_end_of_a_ship_swept_recently_enough()
    {
        var modes = await ModesTheWorkerChoseAsync(ship =>
        {
            ship.BackfillState = ShipBackfillState.Complete;
            ship.BackfillCompletedAt = DateTime.UtcNow - TimeSpan.FromDays(365);
            ship.LastFullSweepStartedAt = DateTime.UtcNow - TimeSpan.FromDays(1);
        });

        Assert.Equal([ScrapeRunMode.Incremental], modes);
    }

    [Fact]
    public async Task The_worker_measures_the_interval_from_the_last_sweeps_start_not_its_end()
    {
        // A sweep that got nowhere is abandoned rather than completed, so it leaves a start and no
        // completion. Measured from the completion it never reached, such a ship would be due a
        // sweep on every tick and would never run an incremental pass again.
        var modes = await ModesTheWorkerChoseAsync(ship =>
        {
            ship.BackfillState = ShipBackfillState.Complete;
            ship.BackfillCompletedAt = DateTime.UtcNow - TimeSpan.FromDays(365);
            ship.LastFullSweepStartedAt = DateTime.UtcNow - TimeSpan.FromDays(1);
            ship.LastFullSweepCompletedAt = null;
        });

        Assert.Equal([ScrapeRunMode.Incremental], modes);
    }

    [Fact]
    public async Task The_worker_resumes_a_sweep_in_flight_ahead_of_the_incremental_pass()
    {
        // A part-walked sweep is worth nothing until it reaches the end of the listing, so leaving
        // one for a tick is leaving it for ever.
        var modes = await ModesTheWorkerChoseAsync(ship =>
        {
            ship.BackfillState = ShipBackfillState.Complete;
            ship.BackfillCompletedAt = DateTime.UtcNow;
            ship.LastFullSweepStartedAt = DateTime.UtcNow;
            ship.FullSweepNextPage = 7;
        });

        Assert.Equal([ScrapeRunMode.FullSweep], modes);
    }

    [Fact]
    public async Task The_worker_backfills_a_ship_that_has_not_finished_its_back_catalogue()
    {
        // The sweep never displaces the backfill: a ship that has not read its listing once has
        // nothing for a sweep to check, and both walks would be spending the same requests.
        var modes = await ModesTheWorkerChoseAsync(ship =>
        {
            ship.BackfillState = ShipBackfillState.InProgress;
            ship.LastFullSweepStartedAt = DateTime.UtcNow - TimeSpan.FromDays(365);
        });

        Assert.Equal([ScrapeRunMode.Backfill], modes);
    }

    [Fact]
    public void The_worker_spreads_the_first_sweep_across_ships_rather_than_starting_them_all_at_once()
    {
        // The tick after this shipped is the case: every ship already followed has a backfill that
        // completed, or a follow date, well over an interval ago. Due together, and with a sweep in
        // flight beating the incremental pass, the instance would stop collecting new works on
        // every ship at once until the whole backlog of full-listing walks drained.
        var followedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var now = followedAt + ScrapeWorker.FullSweepInterval;

        var ships = Enumerable.Range(1, 30)
            .Select(id => new Ship { Id = id, CreatedAt = followedAt })
            .ToList();

        var due = ships.Count(s => ScrapeWorker.FullSweepIsDue(s, now));

        Assert.Equal(1, due);
        Assert.True(ships.All(s => ScrapeWorker.FullSweepIsDue(s, now + ScrapeWorker.FullSweepInterval)));
    }

    // ---- fixtures ------------------------------------------------------------------------------

    private static ScrapeBudget OneRequest() =>
        new(maxRequests: 1, maxConsecutiveFailures: 3, maxDuration: TimeSpan.FromHours(1));

    /// <summary>
    /// A followed ship whose library already holds <paramref name="workIds"/>, read by an ordinary
    /// incremental pass an hour before whatever the test does next. The hour is what makes the
    /// sweep's own start later than these rows' <c>LastSeenAt</c>, which is the line the conclusion
    /// is drawn along.
    /// </summary>
    private async Task<int> AShipHoldingAsync(params long[] workIds)
    {
        var result = await _host.Ships(_host.SeedUser()).WatchShip(new(Lexa), CancellationToken.None);
        var shipId = Assert.IsType<WatchedShipDto>(
            Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;

        _host.Http.Responds = Pages(Page(1, [.. workIds.Select(id => Blurb(id))]));
        await _host.ScrapeAsync(shipId);

        Assert.Equal(workIds.Length, await CountLinksAsync(shipId));
        return shipId;
    }

    /// <summary>One sweep of the ship, served <paramref name="pages"/> as a logged-in session.</summary>
    private Task<ScrapeOutcome> SweepAsync(params FakePage[] pages) => SweepAsync(null, pages);

    /// <inheritdoc cref="SweepAsync(FakePage[])"/>
    private async Task<ScrapeOutcome> SweepAsync(ScrapeBudget? budget, params FakePage[] pages)
    {
        _host.Http.Responds = LoggedInPages(pages);

        // Every sweep in this class runs after the works it is sweeping over were ingested, which
        // is the only ordering a real one can have.
        _host.Clock.Now = _host.Clock.Now.AddHours(1);

        await using var db = _host.NewContext();
        var shipId = await db.Ships.Select(s => s.Id).SingleAsync();

        return await _host.ScrapeAsync(shipId, ScrapeRunMode.FullSweep, budget);
    }

    /// <summary>
    /// The modes the worker chose for one due job, with a stub scraper standing in for the walk —
    /// the choice is the worker's and is what these tests are about.
    /// </summary>
    private async Task<IReadOnlyList<ScrapeRunMode>> ModesTheWorkerChoseAsync(Action<Ship> arrange)
    {
        var stub = new StubScraper(Ao3ScraperKeys.ShipIndex, ScrapeStopReason.Watermark);
        using var host = new LibraryTestHost(stub);

        var result = await host.Ships(host.SeedUser()).WatchShip(new(Lexa), CancellationToken.None);
        Assert.IsType<CreatedAtActionResult>(result.Result);
        await host.SaveAo3LoginAsync();

        await using (var db = host.NewContext())
        {
            arrange(await db.Ships.SingleAsync());
            await db.SaveChangesAsync();
        }

        await host.NewScrapeWorker().RunDueJobsAsync(CancellationToken.None);
        return stub.ModesRun;
    }

    private async Task<Ship> ReloadAsync(int shipId)
    {
        await using var db = _host.NewContext();
        return await db.Ships.SingleAsync(s => s.Id == shipId);
    }

    private async Task<DateTime?> MissingSinceAsync(int shipId, long workId)
    {
        await using var db = _host.NewContext();
        return (await db.ShipWorks.SingleAsync(sw => sw.ShipId == shipId && sw.WorkId == workId))
            .MissingSinceAt;
    }

    private async Task<int> CountLinksAsync(int shipId)
    {
        await using var db = _host.NewContext();
        return await db.ShipWorks.CountAsync(sw => sw.ShipId == shipId);
    }

    private async Task SetWatermarkAsync(int shipId, DateTime watermark)
    {
        await using var db = _host.NewContext();
        (await db.Ships.SingleAsync(s => s.Id == shipId)).IncrementalWatermarkUtc = watermark;
        await db.SaveChangesAsync();
    }

    private async Task SetTotalAsync(int shipId, int total, bool authenticated)
    {
        await using var db = _host.NewContext();
        var ship = await db.Ships.SingleAsync(s => s.Id == shipId);
        ship.LastKnownTotalWorks = total;
        ship.LastKnownTotalWorksAt = _host.Clock.Now.UtcDateTime;
        ship.LastKnownTotalWasAuthenticated = authenticated;
        await db.SaveChangesAsync();
    }
}
