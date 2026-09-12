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
/// The monthly re-read: a walk, in posting order, of the works AO3 lists as revised in the 90 days
/// before it began. The walking is the full sweep's and is covered there; what is this pass's own is
/// the window — what it asks AO3 for, and the narrower thing it may conclude from not seeing a work.
/// </summary>
public class Ao3ShipIndexRecentSweepTests : IDisposable
{
    /// <summary>
    /// Noon, so the hour each re-read in this class adds to the clock cannot carry it into the next
    /// day and move the window.
    /// </summary>
    private static readonly DateTimeOffset Start = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The first day of the window a re-read beginning an hour after <see cref="Start"/> asks for.</summary>
    private static readonly DateOnly Opens = new(2026, 3, 17);

    private readonly CapturingLoggerProvider _logs = new();
    private readonly LibraryTestHost _host;

    public Ao3ShipIndexRecentSweepTests()
    {
        _host = new LibraryTestHost(services =>
        {
            services.AddSingleton<ILoggerProvider>(_logs);
            services.AddScoped<IAo3Scraper>(sp => sp.GetRequiredService<Ao3ShipIndexScraper>());
        });

        _host.Clock.Now = Start;
    }

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- what it asks for ----------------------------------------------------------------------

    [Fact]
    public async Task Asks_for_the_works_revised_since_the_window_opened_in_posting_order()
    {
        // Posting order for the sweep's reason: a work revised while a multi-run walk is under way
        // must not jump to a page it has already read. The filter keeps meaning revision under that
        // order (Ao3DateFilteredListingTests).
        await AShipHoldingAsync(Recent(1));
        var before = _host.Http.Requested.Count;

        await ReReadAsync(Page(1, [Recent(1)], total: 1));

        var url = Assert.Single(_host.Http.Requested.Skip(before));
        Assert.EndsWith(
            "/works?work_search%5Bsort_column%5D=created_at&work_search%5Bdate_from%5D=2026-03-17", url);
    }

    [Fact]
    public async Task Fixes_its_window_and_start_when_it_begins_and_keeps_both_across_runs()
    {
        var shipId = await AShipHoldingAsync(Recent(1), Recent(2), Recent(3));

        await ReReadAsync(
            OneRequest(),
            Page(1, [Recent(1)], nextPage: true, total: 2),
            Page(2, [Recent(2)], total: 2));

        var first = await ReloadAsync(shipId);
        Assert.Equal(new DateTime(2026, 3, 17, 0, 0, 0, DateTimeKind.Utc), first.RecentSweepFrom);
        Assert.Equal(Start.UtcDateTime.AddHours(1), first.LastRecentSweepStartedAt);
        Assert.Equal(2, first.RecentSweepNextPage);
        Assert.Null(first.LastRecentSweepCompletedAt);
        Assert.Null(await MissingSinceAsync(shipId, 3));

        // Three days on, which would open a later window were it measured afresh.
        _host.Clock.Now = _host.Clock.Now.AddDays(3);
        var before = _host.Http.Requested.Count;

        await ReReadAsync(
            Page(1, [Recent(1)], nextPage: true, total: 2),
            Page(2, [Recent(2)], total: 2));

        Assert.Contains("date_from%5D=2026-03-17", Assert.Single(_host.Http.Requested.Skip(before)));

        var ship = await ReloadAsync(shipId);
        Assert.Equal(first.RecentSweepFrom, ship.RecentSweepFrom);
        Assert.Equal(first.LastRecentSweepStartedAt, ship.LastRecentSweepStartedAt);
        Assert.Null(ship.RecentSweepNextPage);
        Assert.Equal(_host.Clock.Now.UtcDateTime, ship.LastRecentSweepCompletedAt);

        Assert.Null(await MissingSinceAsync(shipId, 1));
        Assert.Null(await MissingSinceAsync(shipId, 2));
        Assert.NotNull(await MissingSinceAsync(shipId, 3));
    }

    // ---- what a completed re-read concludes ----------------------------------------------------

    [Fact]
    public async Task Marks_a_work_revised_inside_the_window_that_it_did_not_see_as_having_left_the_tag()
    {
        var shipId = await AShipHoldingAsync(Recent(1), Recent(2));

        await ReReadAsync(Page(1, [Recent(1)], total: 1));

        Assert.Null(await MissingSinceAsync(shipId, 1));
        Assert.Equal(_host.Clock.Now.UtcDateTime, await MissingSinceAsync(shipId, 2));

        var ship = await ReloadAsync(shipId);
        Assert.Equal(_host.Clock.Now.UtcDateTime, ship.LastRecentSweepCompletedAt);
        Assert.Null(ship.RecentSweepNextPage);
    }

    [Fact]
    public async Task Leaves_alone_a_work_last_revised_before_the_window_or_on_its_first_day()
    {
        // Work 2 was never asked for. Work 3 is stored as revised on the window's first day, which in
        // the zone AO3 filters by may be the day before — so it may not have been asked for either.
        var shipId = await AShipHoldingAsync(
            Recent(1),
            Blurb(2, revisedOn: Opens.AddDays(-1)),
            Blurb(3, revisedOn: Opens),
            Blurb(4, revisedOn: Opens.AddDays(1)));

        await ReReadAsync(Page(1, [Recent(1)], total: 1));

        Assert.Null(await MissingSinceAsync(shipId, 2));
        Assert.Null(await MissingSinceAsync(shipId, 3));
        Assert.NotNull(await MissingSinceAsync(shipId, 4));
    }

    [Fact]
    public async Task Leaves_alone_a_work_with_no_stored_revision_date_whatever_its_updated_at_says()
    {
        // Every row read before T90 has none. UpdatedAt is not a stand-in: it runs days ahead of the
        // date AO3 filters by, and would put works in the window that AO3 left out.
        var shipId = await AShipHoldingAsync(
            Recent(1),
            Blurb(2, updatedAt: Start.UtcDateTime.AddDays(-10)));

        await using (var db = _host.NewContext())
        {
            (await db.Works.SingleAsync(w => w.Id == 2)).RevisedOn = null;
            await db.SaveChangesAsync();
        }

        await ReReadAsync(Page(1, [Recent(1)], total: 1));

        Assert.Null(await MissingSinceAsync(shipId, 2));
    }

    // ---- what an interrupted re-read concludes -------------------------------------------------

    [Fact]
    public async Task Abandons_a_re_read_that_read_a_page_without_a_session()
    {
        var shipId = await AShipHoldingAsync(Recent(1), Recent(2));

        _host.Http.Responds = Pages(Page(1, [Recent(1)], total: 1));
        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        await _host.ScrapeAsync(shipId, ScrapeRunMode.RecentSweep);

        Assert.Null(await MissingSinceAsync(shipId, 2));

        var ship = await ReloadAsync(shipId);
        Assert.Null(ship.RecentSweepNextPage);
        Assert.Null(ship.LastRecentSweepCompletedAt);
        Assert.Contains(_logs.Records, r => r.Template.Contains("was served without a session"));
    }

    [Fact]
    public async Task Abandons_a_re_read_that_got_no_further_than_the_page_it_started_on()
    {
        var shipId = await AShipHoldingAsync(Recent(1), Recent(2), Recent(3));

        await ReReadAsync(
            OneRequest(),
            Page(1, [Recent(1)], nextPage: true, total: 2),
            Page(2, [Recent(2)], total: 2));
        Assert.Equal(2, (await ReloadAsync(shipId)).RecentSweepNextPage);

        await ReReadAsync(Page(1, [Recent(1)], nextPage: true, total: 2));

        var ship = await ReloadAsync(shipId);
        Assert.Null(ship.RecentSweepNextPage);
        Assert.Null(ship.LastRecentSweepCompletedAt);
        Assert.Null(await MissingSinceAsync(shipId, 3));
    }

    [Fact]
    public async Task Keeps_a_re_read_in_flight_when_the_archive_told_the_run_nothing()
    {
        var shipId = await AShipHoldingAsync(Recent(1), Recent(2), Recent(3));

        await ReReadAsync(
            OneRequest(),
            Page(1, [Recent(1)], nextPage: true, total: 2),
            Page(2, [Recent(2)], total: 2));
        var startedAt = (await ReloadAsync(shipId)).LastRecentSweepStartedAt;

        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        _host.Http.Responds = _ => throw new HttpRequestException("connection reset by peer");
        await _host.ScrapeAsync(shipId, ScrapeRunMode.RecentSweep);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(2, ship.RecentSweepNextPage);
        Assert.Equal(startedAt, ship.LastRecentSweepStartedAt);
    }

    // ---- what the count on page 1 settles ------------------------------------------------------

    [Fact]
    public async Task Stops_on_page_1_when_the_window_counts_what_the_library_holds_revised_inside_it()
    {
        // Three held, two counted — but work 3 was last revised long before the window, so AO3's
        // filtered count leaves it out and so does the library's side.
        var shipId = await AShipHoldingAsync(Recent(1), Recent(2), Blurb(3, revisedOn: Opens.AddDays(-100)));

        var outcome = await ReReadAsync(
            Page(1, [Recent(1)], nextPage: true, total: 2),
            Page(2, [Recent(2)], total: 2));

        Assert.Equal(ScrapeStopReason.Reconciled, outcome.StopReason);
        Assert.Equal(1, outcome.RequestsMade);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(_host.Clock.Now.UtcDateTime, ship.LastRecentSweepCompletedAt);
        Assert.Null(ship.RecentSweepNextPage);
        Assert.Null(await MissingSinceAsync(shipId, 3));
    }

    [Fact]
    public async Task Walks_its_window_when_the_counts_disagree()
    {
        var shipId = await AShipHoldingAsync(Recent(1), Recent(2), Recent(3));

        var outcome = await ReReadAsync(
            Page(1, [Recent(1)], nextPage: true, total: 2),
            Page(2, [Recent(2)], total: 2));

        Assert.Equal(ScrapeStopReason.LastPage, outcome.StopReason);
        Assert.Equal(2, outcome.RequestsMade);
        Assert.NotNull(await MissingSinceAsync(shipId, 3));
    }

    // ---- what a re-read must not touch ---------------------------------------------------------

    [Fact]
    public async Task Moves_neither_the_watermark_nor_the_total_nor_the_coverage_date()
    {
        // A window's count is not the tag's total, a posting-order page 1 says nothing about the
        // newest revision, and a window read in full is not the whole listing read in full — whether
        // it walked (the first run) or stopped on a matching count (the second).
        var shipId = await AShipHoldingAsync(Recent(1));
        await using (var db = _host.NewContext())
        {
            var held = await db.Ships.SingleAsync(s => s.Id == shipId);
            held.IncrementalWatermarkUtc = Jan(1);
            held.LastKnownTotalWorks = 500;
            held.LastKnownTotalWasAuthenticated = true;
            await db.SaveChangesAsync();
        }

        var fresh = Blurb(1, updatedAt: Start.UtcDateTime.AddDays(-2), revisedOn: Opens.AddDays(80));
        await ReReadAsync(Page(1, [fresh], total: 1));
        await ReReadAsync(Page(1, [fresh], nextPage: true, total: 1));

        var ship = await ReloadAsync(shipId);
        Assert.Equal(Jan(1), ship.IncrementalWatermarkUtc);
        Assert.Equal(500, ship.LastKnownTotalWorks);
        Assert.Null(ship.WholeListingReadLoggedInAt);
    }

    [Fact]
    public async Task A_full_sweep_beginning_puts_away_a_re_read_in_flight()
    {
        var shipId = await AShipHoldingAsync(Recent(1), Recent(2), Recent(3));
        await ReReadAsync(
            OneRequest(),
            Page(1, [Recent(1)], nextPage: true, total: 2),
            Page(2, [Recent(2)], total: 2));
        Assert.Equal(2, (await ReloadAsync(shipId)).RecentSweepNextPage);

        _host.Http.Responds = LoggedInPages(Page(1, [Recent(1)], nextPage: true, total: 9));
        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        await _host.ScrapeAsync(shipId, ScrapeRunMode.FullSweep, OneRequest());

        Assert.Null((await ReloadAsync(shipId)).RecentSweepNextPage);
    }

    // ---- which pass a ship gets ----------------------------------------------------------------

    [Fact]
    public async Task The_worker_re_reads_a_fully_read_ship_once_a_month_has_passed_since_its_last_walk()
    {
        var modes = await ModesTheWorkerChoseAsync((ship, now) =>
        {
            ship.BackfillState = ShipBackfillState.Complete;
            ship.BackfillCompletedAt = now - ScrapeWorker.RecentSweepInterval - ScrapeWorker.RecentSweepStagger
                - TimeSpan.FromDays(1);
            ship.WholeListingReadLoggedInAt = ship.BackfillCompletedAt;
        });

        Assert.Equal([ScrapeRunMode.RecentSweep], modes);
    }

    [Fact]
    public async Task The_worker_resumes_a_re_read_in_flight_ahead_of_the_incremental_pass()
    {
        var modes = await ModesTheWorkerChoseAsync((ship, now) =>
        {
            ship.BackfillState = ShipBackfillState.Complete;
            ship.BackfillCompletedAt = now - TimeSpan.FromDays(365);
            ship.WholeListingReadLoggedInAt = ship.BackfillCompletedAt;
            ship.LastRecentSweepStartedAt = now;
            ship.RecentSweepNextPage = 4;
        });

        Assert.Equal([ScrapeRunMode.RecentSweep], modes);
    }

    [Fact]
    public async Task The_worker_runs_a_queued_full_sweep_ahead_of_a_re_read_in_flight()
    {
        var modes = await ModesTheWorkerChoseAsync((ship, now) =>
        {
            ship.BackfillState = ShipBackfillState.Complete;
            ship.BackfillCompletedAt = now - TimeSpan.FromDays(365);
            ship.WholeListingReadLoggedInAt = ship.BackfillCompletedAt;
            ship.LastRecentSweepStartedAt = now;
            ship.RecentSweepNextPage = 4;
            ship.FullSweepRequestedAt = now;
        });

        Assert.Equal([ScrapeRunMode.FullSweep], modes);
    }

    [Fact]
    public async Task The_worker_backfills_ahead_of_a_re_read_in_flight()
    {
        var modes = await ModesTheWorkerChoseAsync((ship, now) =>
        {
            ship.BackfillState = ShipBackfillState.InProgress;
            ship.WholeListingReadLoggedInAt = now - TimeSpan.FromDays(365);
            ship.RecentSweepNextPage = 4;
        });

        Assert.Equal([ScrapeRunMode.Backfill], modes);
    }

    [Fact]
    public void A_ship_whose_whole_listing_was_never_read_logged_in_is_owed_no_re_read()
    {
        // It is owed a full sweep instead, which reads everything a re-read would.
        var now = Start.UtcDateTime;
        var ship = new Ship { Id = 1, BackfillCompletedAt = now - TimeSpan.FromDays(365) };

        Assert.False(ScrapeWorker.RecentSweepIsDue(ship, now));
    }

    [Theory]
    [InlineData("re-read")]
    [InlineData("full sweep")]
    [InlineData("backfill")]
    public void A_re_read_is_spaced_from_the_latest_walk_of_any_kind(string latest)
    {
        var now = Start.UtcDateTime;
        var longAgo = now - TimeSpan.FromDays(365);
        var yesterday = now - TimeSpan.FromDays(1);

        var ship = new Ship
        {
            Id = 1,
            WholeListingReadLoggedInAt = longAgo,
            LastRecentSweepStartedAt = latest == "re-read" ? yesterday : longAgo,
            LastFullSweepStartedAt = latest == "full sweep" ? yesterday : longAgo,
            BackfillCompletedAt = latest == "backfill" ? yesterday : longAgo,
        };

        Assert.False(ScrapeWorker.RecentSweepIsDue(ship, now));
        Assert.True(ScrapeWorker.RecentSweepIsDue(
            ship, yesterday + ScrapeWorker.RecentSweepInterval + ScrapeWorker.RecentSweepStagger));
    }

    [Fact]
    public void The_worker_spreads_re_reads_across_ships_rather_than_starting_them_all_at_once()
    {
        var walkedAt = Start.UtcDateTime;
        var ships = Enumerable.Range(1, 30)
            .Select(id => new Ship { Id = id, WholeListingReadLoggedInAt = walkedAt, BackfillCompletedAt = walkedAt })
            .ToList();

        var dueAtTheInterval = walkedAt + ScrapeWorker.RecentSweepInterval;

        Assert.Equal(1, ships.Count(s => ScrapeWorker.RecentSweepIsDue(s, dueAtTheInterval)));
        Assert.True(ships.All(s => ScrapeWorker.RecentSweepIsDue(s, dueAtTheInterval + ScrapeWorker.RecentSweepStagger)));
    }

    // ---- fixtures ------------------------------------------------------------------------------

    /// <summary>A blurb AO3 shows as revised well inside the window.</summary>
    private static string Recent(long id) => Blurb(id, revisedOn: Opens.AddDays(30));

    private static ScrapeBudget OneRequest() =>
        new(maxRequests: 1, maxConsecutiveFailures: 3, maxDuration: TimeSpan.FromHours(1));

    /// <summary>
    /// A followed ship whose library already holds <paramref name="blurbs"/>, read by an ordinary
    /// incremental pass an hour before the re-read — which is what puts their <c>LastSeenAt</c> before
    /// the re-read's start.
    /// </summary>
    private async Task<int> AShipHoldingAsync(params string[] blurbs)
    {
        var result = await _host.Ships(_host.SeedUser()).WatchShip(new(Lexa), CancellationToken.None);
        var shipId = Assert.IsType<WatchedShipDto>(
            Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;

        _host.Http.Responds = Pages(Page(1, blurbs));
        await _host.ScrapeAsync(shipId);

        await using var db = _host.NewContext();
        Assert.Equal(blurbs.Length, await db.ShipWorks.CountAsync(sw => sw.ShipId == shipId));
        return shipId;
    }

    /// <summary>One re-read run of the ship, an hour on, served <paramref name="pages"/> logged in.</summary>
    private Task<ScrapeOutcome> ReReadAsync(params FakePage[] pages) => ReReadAsync(null, pages);

    /// <inheritdoc cref="ReReadAsync(FakePage[])"/>
    private async Task<ScrapeOutcome> ReReadAsync(ScrapeBudget? budget, params FakePage[] pages)
    {
        _host.Http.Responds = LoggedInPages(pages);
        _host.Clock.Now = _host.Clock.Now.AddHours(1);

        await using var db = _host.NewContext();
        var shipId = await db.Ships.Select(s => s.Id).SingleAsync();

        return await _host.ScrapeAsync(shipId, ScrapeRunMode.RecentSweep, budget);
    }

    /// <summary>The modes the worker chose for one due job, with a stub standing in for the walk.</summary>
    private static async Task<IReadOnlyList<ScrapeRunMode>> ModesTheWorkerChoseAsync(
        Action<Ship, DateTime> arrange)
    {
        var stub = new StubScraper(Ao3ScraperKeys.ShipIndex, ScrapeStopReason.Watermark);
        using var host = new LibraryTestHost(stub);

        var result = await host.Ships(host.SeedUser()).WatchShip(new(Lexa), CancellationToken.None);
        Assert.IsType<CreatedAtActionResult>(result.Result);
        await host.SaveAo3LoginAsync();

        await using (var db = host.NewContext())
        {
            arrange(await db.Ships.SingleAsync(), host.Clock.Now.UtcDateTime);
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
}
