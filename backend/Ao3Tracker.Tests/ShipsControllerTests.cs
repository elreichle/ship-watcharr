using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// Following and unfollowing relationship tags.
///
/// Nearly every test here is really about one invariant: a ship is shared. Two users naming the
/// same tag must land on one row driven by one schedule, and unfollowing must cost the people
/// still watching nothing — because the alternative is N users triggering N identical multi-hour
/// backfills against volunteer-run infrastructure.
/// </summary>
public class ShipsControllerTests : IDisposable
{
    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- following -----------------------------------------------------------------------------

    [Fact]
    public async Task Following_a_tag_creates_the_ship_and_its_schedule()
    {
        var emma = _host.SeedUser();

        var body = Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default));

        Assert.Equal("Clarke Griffin/Lexa", body.TagName);

        await using var db = _host.NewContext();
        var ship = await db.Ships.SingleAsync();
        Assert.Equal("CLARKE GRIFFIN/LEXA", ship.CanonicalTagNameNormalized);
        Assert.Equal("Clarke%20Griffin*s*Lexa", ship.TagUrlSegment);

        var job = await db.ScrapeJobs.SingleAsync();
        Assert.Equal(ship.Id, job.ShipId);
        Assert.Equal(Ao3ScraperKeys.ShipIndex, job.ScraperKey);
        Assert.True(job.IsEnabled);

        // Null means "due on the next poll" — a newly followed ship should not wait an interval
        // before anything happens.
        Assert.Null(job.NextRunAt);
    }

    [Fact]
    public async Task Trims_the_tag_before_storing_it()
    {
        var emma = _host.SeedUser();

        Created(await _host.Ships(emma).WatchShip(new("  Clarke Griffin/Lexa \n"), default));

        await using var db = _host.NewContext();
        Assert.Equal("Clarke Griffin/Lexa", (await db.Ships.SingleAsync()).CanonicalTagName);
    }

    [Fact]
    public async Task Two_users_following_the_same_tag_share_one_ship_and_one_schedule()
    {
        // The whole point of the design. A second Ship row here means the tag gets scraped twice.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");

        Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default));
        Created(await _host.Ships(sam).WatchShip(new("Clarke Griffin/Lexa"), default));

        await using var db = _host.NewContext();
        Assert.Equal(1, await db.Ships.CountAsync());
        Assert.Equal(1, await db.ScrapeJobs.CountAsync());
        Assert.Equal(2, await db.WatchedShips.CountAsync());
    }

    [Fact]
    public async Task Matches_an_existing_tag_regardless_of_case()
    {
        // Comparing CanonicalTagName directly would find the existing ship on SQLite and create a
        // duplicate on PostgreSQL, so the same instance would behave differently per provider.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");

        Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default));
        Created(await _host.Ships(sam).WatchShip(new("clarke griffin/lexa"), default));

        await using var db = _host.NewContext();
        Assert.Equal(1, await db.Ships.CountAsync());

        // The first spelling wins, rather than the second silently renaming the shared row.
        Assert.Equal("Clarke Griffin/Lexa", (await db.Ships.SingleAsync()).CanonicalTagName);
    }

    [Fact]
    public async Task Refuses_to_follow_the_same_tag_twice()
    {
        var emma = _host.SeedUser();

        Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default));
        var result = await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default);

        Assert.IsType<ConflictObjectResult>(result.Result);

        await using var db = _host.NewContext();
        Assert.Equal(1, await db.WatchedShips.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_a_blank_tag(string input)
    {
        var emma = _host.SeedUser();

        var problem = Problem(await _host.Ships(emma).WatchShip(new(input), default));

        Assert.True(problem.Errors.ContainsKey("TagName"));

        await using var db = _host.NewContext();
        Assert.Empty(db.Ships);
    }

    [Fact]
    public async Task Rejects_a_tag_longer_than_the_column()
    {
        // Without the check this is a 500 from the database rather than a readable 400.
        var emma = _host.SeedUser();

        var problem = Problem(await _host.Ships(emma).WatchShip(new(new string('x', 201)), default));

        Assert.True(problem.Errors.ContainsKey("TagName"));
    }

    [Fact]
    public async Task Accepts_a_platonic_pairing()
    {
        // No slash. Pinned because "require a /" reads like a sensible guard on a couple tag and
        // would reject every & pairing AO3 has.
        var emma = _host.SeedUser();

        var body = Created(await _host.Ships(emma)
            .WatchShip(new("Sam Winchester & Dean Winchester"), default));

        Assert.Equal("Sam Winchester & Dean Winchester", body.TagName);
    }

    // ---- unfollowing ---------------------------------------------------------------------------

    [Fact]
    public async Task Unfollowing_removes_only_the_subscription()
    {
        var emma = _host.SeedUser();
        var shipId = Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default)).ShipId;

        Assert.IsType<NoContentResult>(await _host.Ships(emma).UnwatchShip(shipId, default));

        await using var db = _host.NewContext();
        Assert.Empty(db.WatchedShips);

        // The ship and its run history survive: re-following should resume, not restart a backfill
        // that has already cost AO3 hours of requests.
        Assert.Equal(1, await db.Ships.CountAsync());
        Assert.Equal(1, await db.ScrapeJobs.CountAsync());
    }

    [Fact]
    public async Task Unfollowing_disables_the_schedule_once_the_last_watcher_leaves()
    {
        var emma = _host.SeedUser();
        var shipId = Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default)).ShipId;

        await _host.Ships(emma).UnwatchShip(shipId, default);

        await using var db = _host.NewContext();
        Assert.False((await db.ScrapeJobs.SingleAsync()).IsEnabled);
    }

    [Fact]
    public async Task Unfollowing_leaves_the_schedule_running_for_everyone_else()
    {
        // The failure this guards against is silent: one user leaving stops another user's ship
        // updating, and nothing reports an error.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");

        var shipId = Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default)).ShipId;
        Created(await _host.Ships(sam).WatchShip(new("Clarke Griffin/Lexa"), default));

        await _host.Ships(emma).UnwatchShip(shipId, default);

        await using var db = _host.NewContext();
        Assert.True((await db.ScrapeJobs.SingleAsync()).IsEnabled);
    }

    [Fact]
    public async Task Re_following_a_dropped_tag_re_enables_its_existing_schedule()
    {
        var emma = _host.SeedUser();
        var shipId = Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default)).ShipId;
        await _host.Ships(emma).UnwatchShip(shipId, default);

        Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default));

        await using var db = _host.NewContext();
        var job = await db.ScrapeJobs.SingleAsync();
        Assert.True(job.IsEnabled);
        Assert.Equal(1, await db.ScrapeJobs.CountAsync());
    }

    [Fact]
    public async Task Cannot_unfollow_a_ship_that_is_not_yours()
    {
        // Also the check that stops one user switching off another user's scrape schedule.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var shipId = Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default)).ShipId;

        Assert.IsType<NotFoundResult>(await _host.Ships(sam).UnwatchShip(shipId, default));

        await using var db = _host.NewContext();
        Assert.Equal(1, await db.WatchedShips.CountAsync());
        Assert.True((await db.ScrapeJobs.SingleAsync()).IsEnabled);
    }

    // ---- listing -------------------------------------------------------------------------------

    [Fact]
    public async Task Lists_only_your_own_subscriptions()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default));
        Created(await _host.Ships(sam).WatchShip(new("Kirk/Spock"), default));

        var listed = List(await _host.Ships(emma).GetWatchedShips(default));

        Assert.Equal(["Clarke Griffin/Lexa"], listed.Select(s => s.TagName));
    }

    [Fact]
    public async Task Reports_how_many_people_share_a_ship()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default));
        Created(await _host.Ships(sam).WatchShip(new("Clarke Griffin/Lexa"), default));

        Assert.Equal(2, List(await _host.Ships(emma).GetWatchedShips(default)).Single().WatcherCount);
    }

    [Fact]
    public async Task Counts_only_works_currently_in_the_tag()
    {
        // A work that left the tag and one AO3 deleted are both still rows — the sweep records the
        // absence rather than dropping the history — and neither is "in this ship" any more.
        var emma = _host.SeedUser();
        var shipId = Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default)).ShipId;

        await using (var db = _host.NewContext())
        {
            db.Works.AddRange(Work(1), Work(2), Work(3, isDeleted: true));
            db.ShipWorks.AddRange(
                new ShipWork { ShipId = shipId, WorkId = 1 },
                new ShipWork { ShipId = shipId, WorkId = 2, MissingSinceAt = DateTime.UtcNow },
                new ShipWork { ShipId = shipId, WorkId = 3 });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, List(await _host.Ships(emma).GetWatchedShips(default)).Single().WorkCount);
    }

    [Fact]
    public async Task Reports_that_no_scraper_can_run_the_schedule()
    {
        // Honest about this build: jobs are scheduled, but nothing is registered under
        // Ao3ScraperKeys.ShipIndex, so following a tag fetches nothing. The UI says so rather than
        // showing a permanently empty library with no explanation.
        var emma = _host.SeedUser();
        Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default));

        var listed = List(await _host.Ships(emma).GetWatchedShips(default)).Single();

        Assert.True(listed.IsScheduled);
        Assert.False(listed.ScraperAvailable);
    }

    [Fact]
    public async Task A_newly_followed_tag_starts_unverified()
    {
        // Accepted on trust, checked after. The alternative — blocking the response on an AO3
        // request behind a 5–8s shared gate — has no predictable latency to offer the caller.
        var emma = _host.SeedUser();

        var body = Created(await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default));

        Assert.Equal("Pending", body.VerificationState);
        Assert.Null(body.VerificationError);
        Assert.Null(body.RequestedTagName);
    }

    [Fact]
    public async Task Reports_that_verification_is_running()
    {
        Assert.True(Envelope(await _host.Ships(_host.SeedUser()).GetWatchedShips(default)).VerificationEnabled);
    }

    [Fact]
    public async Task Reports_that_verification_cannot_run_without_an_operator_contact()
    {
        // The page would otherwise show every ship stuck on "Checking…" with no visible reason, and
        // the setting that fixes it is admin-only.
        _host.OperatorContact = null;

        Assert.False(Envelope(await _host.Ships(_host.SeedUser()).GetWatchedShips(default)).VerificationEnabled);
    }

    [Fact]
    public async Task Reports_an_available_scraper_once_one_is_registered()
    {
        using var host = new LibraryTestHost(new StubScraper(Ao3ScraperKeys.ShipIndex));
        var emma = host.SeedUser();
        Created(await host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default));

        Assert.True(List(await host.Ships(emma).GetWatchedShips(default)).Single().ScraperAvailable);
    }

    // ---- fixture -------------------------------------------------------------------------------

    private static Work Work(long id, bool isDeleted = false) => new()
    {
        Id = id,
        Title = $"Work {id}",
        IsDeleted = isDeleted,
    };

    private static WatchedShipDto Created(ActionResult<WatchedShipDto> result) =>
        Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);

    private static IReadOnlyList<WatchedShipDto> List(ActionResult<WatchedShipsDto> result) =>
        Envelope(result).Ships;

    private static WatchedShipsDto Envelope(ActionResult<WatchedShipsDto> result) =>
        Assert.IsType<WatchedShipsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static ValidationProblemDetails Problem(ActionResult<WatchedShipDto> result) =>
        Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);
}
