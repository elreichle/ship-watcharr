using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Ao3Tracker.Tests.Ao3ListingFixtures;

namespace Ao3Tracker.Tests;

/// <summary>
/// An admin asking for a full sweep ahead of the schedule, which is how a ship whose listing was read
/// logged out gets its restricted works without waiting up to two sweep intervals.
///
/// The assertions that matter are the pair at either end of the request: that the worker actually
/// sweeps a ship carrying one, and that starting the sweep answers it. A request the worker ignored
/// would read "queued" for ever; one nothing cleared would sweep the ship again on every check.
/// </summary>
public class FullSweepQueueTests : IDisposable
{
    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- who may ask --------------------------------------------------------------------------------

    [Fact]
    public async Task A_non_admin_may_not_queue_a_sweep()
    {
        var shipId = await AReadShipAsync();

        var result = await _host.AdminShips(_host.SeedUser("sam")).QueueFullSweep(shipId, default);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Null((await ReloadAsync(shipId)).FullSweepRequestedAt);
    }

    [Fact]
    public async Task Queuing_a_sweep_for_a_ship_this_instance_has_never_seen_is_a_404()
    {
        var result = await Admin().QueueFullSweep(4040, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- what it writes -----------------------------------------------------------------------------

    [Fact]
    public async Task Queues_a_sweep_and_leaves_the_schedule_alone()
    {
        var shipId = await AReadShipAsync();
        var nextRunAt = await NextRunAtAsync(shipId);

        var queued = Queued(await Admin().QueueFullSweep(shipId, default));

        var ship = await ReloadAsync(shipId);
        Assert.NotNull(ship.FullSweepRequestedAt);
        Assert.Equal(ship.FullSweepRequestedAt, queued.FullSweepRequestedAt);
        Assert.Equal(nextRunAt, queued.NextScrapeAt);
        Assert.Equal(nextRunAt, await NextRunAtAsync(shipId));

        // A request is not a sweep under way: nothing has been read, so nothing may say so.
        Assert.Null(ship.FullSweepNextPage);
        Assert.Null(ship.LastFullSweepStartedAt);
    }

    [Fact]
    public async Task Asking_twice_keeps_the_first_request()
    {
        var shipId = await AReadShipAsync();
        var first = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        await MutateAsync(shipId, s => s.FullSweepRequestedAt = first);

        var queued = Queued(await Admin().QueueFullSweep(shipId, default));

        Assert.Equal(first, queued.FullSweepRequestedAt);
        Assert.Equal(first, (await ReloadAsync(shipId)).FullSweepRequestedAt);
    }

    [Theory]
    [InlineData(ShipBackfillState.NotStarted)]
    [InlineData(ShipBackfillState.InProgress)]
    public async Task Refuses_while_the_back_catalogue_is_still_being_read(ShipBackfillState state)
    {
        var shipId = await FollowAsync();
        await MutateAsync(shipId, s => s.BackfillState = state);

        var result = await Admin().QueueFullSweep(shipId, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Null((await ReloadAsync(shipId)).FullSweepRequestedAt);
    }

    [Fact]
    public async Task Queues_a_sweep_for_a_ship_whose_back_catalogue_was_given_up_on()
    {
        // A written-off backfill is the other ship a sweep is for: it never read the rest of its
        // listing at all.
        var shipId = await FollowAsync();
        await MutateAsync(shipId, s => s.BackfillState = ShipBackfillState.Failed);

        Assert.IsType<OkObjectResult>((await Admin().QueueFullSweep(shipId, default)).Result);
    }

    [Fact]
    public async Task Refuses_while_a_sweep_is_already_walking_the_listing()
    {
        var shipId = await AReadShipAsync();
        await MutateAsync(shipId, s =>
        {
            s.LastFullSweepStartedAt = _host.Clock.Now.UtcDateTime;
            s.FullSweepNextPage = 7;
        });

        var result = await Admin().QueueFullSweep(shipId, default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Null((await ReloadAsync(shipId)).FullSweepRequestedAt);
    }

    [Fact]
    public async Task Refuses_for_a_tag_AO3_has_denied()
    {
        var shipId = await AReadShipAsync();
        await MutateAsync(shipId, s => s.VerificationState = ShipVerificationState.NotFoundOnAo3);

        Assert.IsType<ConflictObjectResult>((await Admin().QueueFullSweep(shipId, default)).Result);
    }

    [Fact]
    public async Task Refuses_for_a_ship_no_schedule_would_pick_up()
    {
        var shipId = await AReadShipAsync();
        await using (var db = _host.NewContext())
        {
            (await db.ScrapeJobs.SingleAsync(j => j.ShipId == shipId)).IsEnabled = false;
            await db.SaveChangesAsync();
        }

        Assert.IsType<ConflictObjectResult>((await Admin().QueueFullSweep(shipId, default)).Result);
    }

    // ---- what the ship does afterwards --------------------------------------------------------------

    [Fact]
    public async Task The_worker_sweeps_a_ship_carrying_a_request_however_recently_it_was_swept()
    {
        var stub = new StubScraper(Ao3ScraperKeys.ShipIndex, ScrapeStopReason.Watermark);
        using var host = new LibraryTestHost(stub);

        Assert.IsType<CreatedAtActionResult>(
            (await host.Ships(host.SeedUser()).WatchShip(new(Lexa), CancellationToken.None)).Result);
        await host.SaveAo3LoginAsync();

        await using (var db = host.NewContext())
        {
            var ship = await db.Ships.SingleAsync();
            var now = host.Clock.Now.UtcDateTime;
            ship.BackfillState = ShipBackfillState.Complete;
            ship.BackfillCompletedAt = now;
            ship.LastFullSweepStartedAt = now.AddDays(-1);
            ship.FullSweepRequestedAt = now;
            await db.SaveChangesAsync();
        }

        await host.NewScrapeWorker().RunDueJobsAsync(CancellationToken.None);

        Assert.Equal([ScrapeRunMode.FullSweep], stub.ModesRun);
    }

    [Fact]
    public async Task Starting_the_sweep_answers_the_request()
    {
        var shipId = await AReadShipAsync();
        Assert.IsType<OkObjectResult>((await Admin().QueueFullSweep(shipId, default)).Result);

        _host.Http.Responds = LoggedInPages(Page(1, [Blurb(1)], total: 1));
        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        await _host.ScrapeAsync(shipId, ScrapeRunMode.FullSweep);

        var ship = await ReloadAsync(shipId);
        Assert.Null(ship.FullSweepRequestedAt);
        Assert.Equal(_host.Clock.Now.UtcDateTime, ship.LastFullSweepStartedAt);
        Assert.Equal(_host.Clock.Now.UtcDateTime, ship.WholeListingReadLoggedInAt);

        // And the ship goes back to its ordinary schedule rather than sweeping again next check.
        Assert.False(ScrapeWorker.FullSweepIsDue(ship, _host.Clock.Now.UtcDateTime));
    }

    [Fact]
    public async Task The_ships_list_reports_a_queued_sweep()
    {
        var emma = _host.SeedUser();
        var shipId = await FollowAsync(emma);
        var requestedAt = new DateTime(2026, 9, 11, 6, 0, 0, DateTimeKind.Utc);
        await MutateAsync(shipId, s => s.FullSweepRequestedAt = requestedAt);

        var listed = Assert.IsType<WatchedShipsDto>(
            Assert.IsType<OkObjectResult>((await _host.Ships(emma).GetWatchedShips(default)).Result).Value);

        Assert.Equal(requestedAt, listed.Ships.Single().FullSweepRequestedAt);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private Api.Controllers.AdminShipsController Admin() =>
        _host.AdminShips(_host.SeedUser(Guid.NewGuid().ToString("N")[..8], isAdmin: true));

    private static FullSweepQueuedDto Queued(ActionResult<FullSweepQueuedDto> result) =>
        Assert.IsType<FullSweepQueuedDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private async Task<int> FollowAsync(ApplicationUser? user = null)
    {
        var result = await _host.Ships(user ?? _host.SeedUser(Guid.NewGuid().ToString("N")[..8]))
            .WatchShip(new AddWatchedShipRequest(Lexa), CancellationToken.None);

        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    /// <summary>A followed ship whose back catalogue of one work was read, logged out.</summary>
    private async Task<int> AReadShipAsync()
    {
        var shipId = await FollowAsync();
        _host.Http.Responds = Pages(Page(1, [Blurb(1)]));
        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(ShipBackfillState.Complete, (await ReloadAsync(shipId)).BackfillState);
        return shipId;
    }

    private async Task<Ship> ReloadAsync(int shipId)
    {
        await using var db = _host.NewContext();
        return await db.Ships.SingleAsync(s => s.Id == shipId);
    }

    private async Task<DateTime?> NextRunAtAsync(int shipId)
    {
        await using var db = _host.NewContext();
        return (await db.ScrapeJobs.SingleAsync(j => j.ShipId == shipId)).NextRunAt;
    }

    private async Task MutateAsync(int shipId, Action<Ship> change)
    {
        await using var db = _host.NewContext();
        change(await db.Ships.SingleAsync(s => s.Id == shipId));
        await db.SaveChangesAsync();
    }
}
