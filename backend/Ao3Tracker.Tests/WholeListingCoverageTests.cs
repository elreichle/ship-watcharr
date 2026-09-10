using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Ao3Tracker.Tests.Ao3ListingFixtures;

namespace Ao3Tracker.Tests;

/// <summary>
/// <see cref="Ship.WholeListingReadLoggedInAt"/>: the date that says a ship's library holds the works
/// only a logged-in reader can see.
///
/// The failure this guards is the one production had for a month: a backfill that read its pages
/// logged out finished, reported itself complete, and the library quietly lacked every restricted
/// work in the tag with nothing anywhere saying so. So the assertions that matter are the refusals
/// — a walk with one logged-out page in it must not earn the date, however it ends.
/// </summary>
public class WholeListingCoverageTests : IDisposable
{
    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- the backfill -----------------------------------------------------------------------------

    [Fact]
    public async Task A_backfill_read_logged_in_from_end_to_end_records_when_it_finished()
    {
        var shipId = await FollowAsync();
        _host.Http.Responds = LoggedInPages(Page(1, [Blurb(1)], nextPage: true), Page(2, [Blurb(2)]));

        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill, OneRequest());
        Assert.Null((await ReloadAsync(shipId)).WholeListingReadLoggedInAt);

        _host.Clock.Now = _host.Clock.Now.AddHours(6);
        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill, OneRequest());

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.Complete, ship.BackfillState);
        Assert.Equal(_host.Clock.Now.UtcDateTime, ship.WholeListingReadLoggedInAt);
    }

    [Fact]
    public async Task A_backfill_with_one_run_read_logged_out_does_not_claim_the_listing()
    {
        // The run that reads the last page was logged in. The one before it was not, and its page's
        // restricted works were never seen — which is exactly what a session lapsing half an hour
        // into a run used to do, on every long backfill.
        var shipId = await FollowAsync();
        var pages = new[] { Page(1, [Blurb(1)], nextPage: true), Page(2, [Blurb(2)]) };

        _host.Http.Responds = Pages(pages);
        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill, OneRequest());

        _host.Http.Responds = LoggedInPages(pages);
        _host.Clock.Now = _host.Clock.Now.AddHours(6);
        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill, OneRequest());

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.Complete, ship.BackfillState);
        Assert.True(ship.BackfillReadAnonymously);
        Assert.Null(ship.WholeListingReadLoggedInAt);
    }

    [Fact]
    public async Task A_backfill_beginning_forgets_what_an_earlier_walk_read_logged_out()
    {
        var shipId = await FollowAsync();
        await MutateAsync(shipId, s => s.BackfillReadAnonymously = true);
        _host.Http.Responds = LoggedInPages(Page(1, [Blurb(1)]));

        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        var ship = await ReloadAsync(shipId);
        Assert.False(ship.BackfillReadAnonymously);
        Assert.Equal(_host.Clock.Now.UtcDateTime, ship.WholeListingReadLoggedInAt);
    }

    [Fact]
    public async Task A_restarted_backfill_keeps_what_the_walk_before_the_restart_read_logged_out()
    {
        // A restart continues the same walk, so the pages it read before the write-off are still
        // pages of the listing this backfill is claiming to have read.
        var shipId = await FollowAsync();
        await MutateAsync(shipId, s =>
        {
            s.BackfillState = ShipBackfillState.Failed;
            s.BackfillStartedAt = _host.Clock.Now.UtcDateTime.AddDays(-3);
            s.BackfillNextPage = 2;
            s.BackfillStalledRuns = Ao3ShipIndexScraper.MaxStalledBackfillRuns;
            s.BackfillReadAnonymously = true;
        });

        var admin = _host.AdminShips(_host.SeedUser("admin", isAdmin: true));
        Assert.IsType<OkObjectResult>((await admin.RestartBackfill(shipId, new(null), default)).Result);

        _host.Http.Responds = LoggedInPages(Page(2, [Blurb(2)]));
        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.Complete, ship.BackfillState);
        Assert.Null(ship.WholeListingReadLoggedInAt);
    }

    // ---- the sweep ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_sweep_that_concludes_records_the_listing_as_read_logged_in()
    {
        var shipId = await ALoggedOutBackfillHoldingAsync(1);

        _host.Http.Responds = LoggedInPages(Page(1, [Blurb(1)], total: 1));
        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        await _host.ScrapeAsync(shipId, ScrapeRunMode.FullSweep);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(_host.Clock.Now.UtcDateTime, ship.LastFullSweepCompletedAt);
        Assert.Equal(_host.Clock.Now.UtcDateTime, ship.WholeListingReadLoggedInAt);
    }

    [Fact]
    public async Task A_sweep_stopped_on_page_1_by_a_logged_in_count_that_matches_records_it_too()
    {
        var shipId = await ALoggedOutBackfillHoldingAsync(1, 2);

        _host.Http.Responds = LoggedInPages(
            Page(1, [Blurb(1)], nextPage: true, total: 2), Page(2, [Blurb(2)], total: 2));
        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        var outcome = await _host.ScrapeAsync(shipId, ScrapeRunMode.FullSweep);

        Assert.Equal(ScrapeStopReason.Reconciled, outcome.StopReason);
        Assert.Equal(_host.Clock.Now.UtcDateTime, (await ReloadAsync(shipId)).WholeListingReadLoggedInAt);
    }

    [Fact]
    public async Task A_sweep_that_read_a_page_logged_out_records_nothing()
    {
        var shipId = await ALoggedOutBackfillHoldingAsync(1);

        _host.Http.Responds = Pages(Page(1, [Blurb(1)], total: 1));
        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        await _host.ScrapeAsync(shipId, ScrapeRunMode.FullSweep);

        Assert.Null((await ReloadAsync(shipId)).WholeListingReadLoggedInAt);
    }

    // ---- what a reader is shown ---------------------------------------------------------------------

    [Fact]
    public async Task The_ships_list_reports_when_the_whole_listing_was_last_read_logged_in()
    {
        var emma = _host.SeedUser();
        var shipId = await FollowAsync(emma);
        var readAt = new DateTime(2026, 9, 11, 6, 0, 0, DateTimeKind.Utc);
        await MutateAsync(shipId, s => s.WholeListingReadLoggedInAt = readAt);

        var listed = Assert.IsType<WatchedShipsDto>(
            Assert.IsType<OkObjectResult>((await _host.Ships(emma).GetWatchedShips(default)).Result).Value);

        Assert.Equal(readAt, listed.Ships.Single().WholeListingReadLoggedInAt);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static ScrapeBudget OneRequest() =>
        new(maxRequests: 1, maxConsecutiveFailures: 3, maxDuration: TimeSpan.FromHours(1));

    private async Task<int> FollowAsync(ApplicationUser? user = null)
    {
        var result = await _host.Ships(user ?? _host.SeedUser(Guid.NewGuid().ToString("N")[..8]))
            .WatchShip(new AddWatchedShipRequest(Lexa), CancellationToken.None);

        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    /// <summary>
    /// A ship whose back catalogue of <paramref name="workIds"/> was read logged out, as every ship on
    /// the first production instance was — so its coverage date is null going into the sweep, and
    /// whatever the sweep leaves there is the sweep's own doing.
    /// </summary>
    private async Task<int> ALoggedOutBackfillHoldingAsync(params long[] workIds)
    {
        var shipId = await FollowAsync();
        _host.Http.Responds = Pages(Page(1, [.. workIds.Select(id => Blurb(id))]));
        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.Complete, ship.BackfillState);
        Assert.Null(ship.WholeListingReadLoggedInAt);
        return shipId;
    }

    private async Task<Ship> ReloadAsync(int shipId)
    {
        await using var db = _host.NewContext();
        return await db.Ships.SingleAsync(s => s.Id == shipId);
    }

    private async Task MutateAsync(int shipId, Action<Ship> change)
    {
        await using var db = _host.NewContext();
        change(await db.Ships.SingleAsync(s => s.Id == shipId));
        await db.SaveChangesAsync();
    }
}
