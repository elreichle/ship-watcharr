using System.Net;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// The operator's way back out of a backfill this instance wrote off.
///
/// <c>ShipBackfillState.Failed</c> is reached after twelve consecutive runs against a cursor the
/// listing will not answer, which is the right conclusion while AO3 is refusing and the wrong one
/// the moment it stops. Nothing in the scraper clears it — deliberately — so without these
/// endpoints an outage costs a back catalogue permanently, recoverable only by editing the database
/// by hand.
///
/// The assertions that matter are not the ones about the row. They are the two that say the ship
/// actually walks again afterwards: a restart that left the stalled counter where it was would
/// re-arm a ship that gives up on its very next stalled run rather than twelve runs later, and
/// nothing about the row on its own can tell those apart.
/// </summary>
public class BackfillRestartTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";

    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- who may perform it ----------------------------------------------------------------------

    [Fact]
    public async Task A_non_admin_may_not_restart_a_backfill()
    {
        var shipId = await FollowAsync();
        await FailBackfillAtAsync(shipId, page: 40);
        var sam = _host.SeedUser("sam");

        var result = await _host.AdminShips(sam).RestartBackfill(shipId, new(null), default);

        Assert.IsType<ForbidResult>(result.Result);

        // A refusal to answer that still re-armed the ship would be no refusal at all: the walk is
        // shared, so the request it would spend is spent on the whole instance's behalf.
        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.Failed, ship.BackfillState);
        Assert.Equal(Ao3ShipIndexScraper.MaxStalledBackfillRuns, ship.BackfillStalledRuns);
    }

    [Fact]
    public async Task Restarting_the_backfill_of_a_ship_this_instance_has_never_seen_is_a_404()
    {
        var admin = _host.SeedUser("admin", isAdmin: true);

        var result = await _host.AdminShips(admin).RestartBackfill(4040, new(null), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- what it writes --------------------------------------------------------------------------

    [Fact]
    public async Task Restarts_a_failed_backfill_from_where_the_walk_gave_up()
    {
        // The cursor is left pointing at the page the walk could not get past precisely so that a
        // restart has somewhere honest to resume from, which makes "where it gave up" the default
        // rather than a fallback.
        var shipId = await FollowAsync();
        await FailBackfillAtAsync(shipId, page: 40);

        var body = Restarted(await Admin().RestartBackfill(shipId, new(null), default));

        Assert.Equal("InProgress", body.BackfillState);
        Assert.Equal(40, body.BackfillNextPage);
        Assert.Equal(0, body.BackfillStalledRuns);
        Assert.Equal(Lexa, body.TagName);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.InProgress, ship.BackfillState);
        Assert.Equal(40, ship.BackfillNextPage);
        Assert.Equal(0, ship.BackfillStalledRuns);
    }

    [Fact]
    public async Task Restarts_a_failed_backfill_from_the_page_the_admin_names()
    {
        // The case the choice exists for: a listing that shrank, where resuming at the old cursor
        // would fail again for the same reason it failed the first twelve times.
        var shipId = await FollowAsync();
        await FailBackfillAtAsync(shipId, page: 40);

        var body = Restarted(await Admin().RestartBackfill(shipId, new(1), default));

        Assert.Equal(1, body.BackfillNextPage);
        Assert.Equal(1, (await ReloadAsync(shipId)).BackfillNextPage);
    }

    [Fact]
    public async Task A_restart_clears_the_completion_a_failed_backfill_never_had()
    {
        // Belt and braces on a field the give-up path leaves null: a Complete backfill cannot be
        // restarted, so the only way a completion timestamp could survive into a restarted walk is
        // a hand-edited row — and a ship reading "in progress, completed at 3pm" is worse than one
        // reading neither.
        var shipId = await FollowAsync();
        await FailBackfillAtAsync(shipId, page: 40);
        await MutateAsync(shipId, s => s.BackfillCompletedAt = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await Admin().RestartBackfill(shipId, new(null), default);

        Assert.Null((await ReloadAsync(shipId)).BackfillCompletedAt);
    }

    [Fact]
    public async Task A_restart_puts_away_a_full_sweep_that_was_in_flight()
    {
        // A Failed backfill is eligible for both a sweep and this restart, so the two can be owed
        // at once — and the backfill wins, for however many ticks it needs. A sweep resumed after
        // all that would reach the end of the listing still claiming to have walked pages 1..N of
        // one listing, when its first pages were read weeks and a whole re-walk ago. Its start date
        // is deliberately left alone: it is what spaces the next sweep, which would otherwise begin
        // on the tick the backfill finishes.
        var shipId = await FollowAsync();
        await FailBackfillAtAsync(shipId, page: 40);
        await MutateAsync(shipId, s =>
        {
            s.FullSweepNextPage = 40;
            s.LastFullSweepStartedAt = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        });

        await Admin().RestartBackfill(shipId, new(null), default);

        var ship = await ReloadAsync(shipId);
        Assert.Null(ship.FullSweepNextPage);
        Assert.Equal(new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc), ship.LastFullSweepStartedAt);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public async Task A_restarted_backfill_forgets_the_floor_the_old_walk_read(int? fromPage)
    {
        // The floor is the oldest work a *contiguous* walk has reached, and the scraper compares
        // every page against it to notice a listing shifting underneath itself. Carried into a
        // re-walk it makes every page report a shift that never happened, and never updates again.
        //
        // Dropped whichever page the restart names, including the ship's own cursor. Deciding by
        // comparing the two would get the common case exactly backwards: the halving retreat drags
        // a stalling ship's cursor down below the pages the floor was read from, so a default
        // restart at the stored page 1 of a walk that read to page 39 would keep a page-39 floor.
        var shipId = await FollowAsync();
        await FailBackfillAtAsync(shipId, page: 1);
        await MutateAsync(shipId, s => s.BackfillMinUpdatedAtSeen = new DateTime(2019, 5, 1, 0, 0, 0, DateTimeKind.Utc));

        await Admin().RestartBackfill(shipId, new(fromPage), default);

        Assert.Null((await ReloadAsync(shipId)).BackfillMinUpdatedAtSeen);
    }

    // ---- what it refuses -------------------------------------------------------------------------

    [Theory]
    [InlineData(ShipBackfillState.Complete)]
    [InlineData(ShipBackfillState.InProgress)]
    [InlineData(ShipBackfillState.NotStarted)]
    public async Task Refuses_to_restart_a_backfill_this_instance_did_not_give_up_on(ShipBackfillState state)
    {
        // Only Failed. Re-arming a Complete backfill would re-walk a back catalogue already read,
        // at 5-8 seconds a page, off one mis-click; re-arming an InProgress one would move the
        // cursor out from under a walk that is working.
        var shipId = await FollowAsync();
        await MutateAsync(shipId, s =>
        {
            s.BackfillState = state;
            s.BackfillNextPage = 40;
        });

        var result = await Admin().RestartBackfill(shipId, new(1), default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(40, (await ReloadAsync(shipId)).BackfillNextPage);
    }

    [Fact]
    public async Task Refuses_to_restart_the_backfill_of_a_tag_AO3_has_denied()
    {
        // The scraper returns before its first request for one of these, so a restart would leave a
        // ship reading "in progress" that no run will ever touch — which is worse than the
        // written-off state it replaced, because that one at least said so.
        var shipId = await FollowAsync();
        await FailBackfillAtAsync(shipId, page: 40);
        await MutateAsync(shipId, s => s.VerificationState = ShipVerificationState.NotFoundOnAo3);

        var result = await Admin().RestartBackfill(shipId, new(null), default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(ShipBackfillState.Failed, (await ReloadAsync(shipId)).BackfillState);
    }

    [Fact]
    public async Task Refuses_to_restart_a_backfill_no_schedule_would_pick_up()
    {
        // Same shape one layer out: the worker only ever takes an enabled job, so re-arming a ship
        // whose schedule is off — nobody watches it any more — parks it at InProgress for good.
        var shipId = await FollowAsync();
        await FailBackfillAtAsync(shipId, page: 40);

        await using (var db = _host.NewContext())
        {
            (await db.ScrapeJobs.SingleAsync(j => j.ShipId == shipId)).IsEnabled = false;
            await db.SaveChangesAsync();
        }

        var result = await Admin().RestartBackfill(shipId, new(null), default);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(ShipBackfillState.Failed, (await ReloadAsync(shipId)).BackfillState);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Refuses_to_restart_a_backfill_from_a_page_no_listing_has(int page)
    {
        var shipId = await FollowAsync();
        await FailBackfillAtAsync(shipId, page: 40);

        var result = await Admin().RestartBackfill(shipId, new(page), default);

        var problem = Assert.IsType<ValidationProblemDetails>(
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains(nameof(RestartBackfillRequest.FromPage), problem.Errors.Keys);

        Assert.Equal(ShipBackfillState.Failed, (await ReloadAsync(shipId)).BackfillState);
    }

    // ---- what the ship does afterwards -----------------------------------------------------------

    [Fact]
    public async Task A_restarted_backfill_walks_the_listing_again()
    {
        // The point of the whole endpoint. ScrapeWorker backfills a NotStarted or InProgress ship
        // only, so a Failed one goes on collecting new works and never touches its back catalogue
        // again; this is the assertion that the restart actually puts it back in that set.
        _host.Http.Responds = Pages(Page(2, [Blurb(2)], nextPage: true), Page(3, [Blurb(3)]));

        var shipId = await FollowAsync();
        await FailBackfillAtAsync(shipId, page: 2);
        await Admin().RestartBackfill(shipId, new(null), default);

        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipBackfillState.Complete, ship.BackfillState);

        await using var db = _host.NewContext();
        Assert.Equal([2, 3], await db.Works.OrderBy(w => w.Id).Select(w => w.Id).ToListAsync());
    }

    [Fact]
    public async Task A_restarted_backfill_gets_the_whole_allowance_again_rather_than_one_run()
    {
        // Resetting the counter is not a nicety in the restart, it is the half that makes it work.
        // Both of the scraper's own reset sites sit on paths a Failed ship no longer reaches and
        // giving up does not clear it either, so the counter is frozen at twelve: a restart that
        // left it there would hand back a ship that fails again on its first stalled run, which
        // looks from the outside exactly like a restart that did nothing.
        _host.Http.Responds = _ => new ScrapeHttpResponse(
            "<html><body><h1>Down for maintenance</h1></body></html>",
            HttpStatusCode.OK, FromCache: false, FinalUrl: "");

        var shipId = await FollowAsync();
        await FailBackfillAtAsync(shipId, page: 10);
        await Admin().RestartBackfill(shipId, new(null), default);

        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(1, ship.BackfillStalledRuns);
        Assert.Equal(ShipBackfillState.InProgress, ship.BackfillState);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private Api.Controllers.AdminShipsController Admin() =>
        _host.AdminShips(_host.SeedUser(Guid.NewGuid().ToString("N")[..8], isAdmin: true));

    private static BackfillRestartedDto Restarted(ActionResult<BackfillRestartedDto> result) =>
        Assert.IsType<BackfillRestartedDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private async Task<int> FollowAsync()
    {
        var result = await _host.Ships(_host.SeedUser(Guid.NewGuid().ToString("N")[..8]))
            .WatchShip(new AddWatchedShipRequest(Lexa), CancellationToken.None);

        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    private async Task<Ship> ReloadAsync(int shipId)
    {
        await using var db = _host.NewContext();
        return await db.Ships.SingleAsync(s => s.Id == shipId);
    }

    /// <summary>
    /// Puts a ship exactly where twelve stalled runs leave it: written off, the cursor still on the
    /// page nothing would answer for, and the counter frozen at the value it gave up on — which is
    /// what the scraper does, because neither of its reset sites is on a path a Failed ship reaches.
    /// </summary>
    private Task FailBackfillAtAsync(int shipId, int page) => MutateAsync(shipId, s =>
    {
        s.BackfillState = ShipBackfillState.Failed;
        s.BackfillStartedAt = new DateTime(2023, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        s.BackfillNextPage = page;
        s.BackfillStalledRuns = Ao3ShipIndexScraper.MaxStalledBackfillRuns;
    });

    private async Task MutateAsync(int shipId, Action<Ship> change)
    {
        await using var db = _host.NewContext();
        change(await db.Ships.SingleAsync(s => s.Id == shipId));
        await db.SaveChangesAsync();
    }

    private static Func<string, ScrapeHttpResponse> Pages(params (int Number, string Html)[] pages) => url =>
    {
        var number = 1;
        var marker = url.IndexOf("page=", StringComparison.Ordinal);
        if (marker >= 0) number = int.Parse(new string([.. url[(marker + 5)..].TakeWhile(char.IsDigit)]));

        var page = pages.FirstOrDefault(p => p.Number == number);
        return page.Html is null
            ? new ScrapeHttpResponse("", HttpStatusCode.NotFound, FromCache: false, FinalUrl: url)
            : new ScrapeHttpResponse(page.Html, HttpStatusCode.OK, FromCache: false, FinalUrl: url);
    };

    private static (int, string) Page(int number, string[] blurbs, bool nextPage = false) => (number, $"""
        <div id="main">
          <ol class="work index group">{string.Join('\n', blurbs)}</ol>
          {(nextPage ? """<ol class="pagination actions"><li><a href="?page=next">Next &rarr;</a></li></ol>""" : "")}
        </div>
        """);

    /// <summary>
    /// One blurb, in the shape Ao3ShipIndexScraperTests renders — the parser is exercised by its
    /// own tests, and what these need is markup it reads without complaint.
    /// </summary>
    private static string Blurb(long id) => $"""
        <li id="work_{id}" class="work blurb group">
          <div class="header module">
            <h4 class="heading">
              <a href="/works/{id}">Work {id}</a>
              by <a rel="author" href="/users/someuser/pseuds/somepseud">somepseud (someuser)</a>
            </h4>
            <ul class="required-tags">
              <li><span class="rating-teen rating" title="Teen And Up Audiences"></span></li>
              <li><span class="warning-no warnings" title="No Archive Warnings Apply"></span></li>
              <li><span class="category-femslash category" title="F/F"></span></li>
              <li><span class="complete-yes iswip" title="Complete Work"></span></li>
            </ul>
            <!-- updated_at=1672574400 -->
            <p class="datetime">1 Jan 2023</p>
          </div>
          <ul class="tags commas">
            <li class="relationships"><a class="tag" href="/tags/lexa/works">{Lexa}</a></li>
            <li class="freeforms"><a class="tag" href="/tags/Fluff/works">Fluff</a></li>
          </ul>
          <dl class="stats">
            <dt class="words">Words:</dt><dd class="words">1,000</dd>
            <dt class="chapters">Chapters:</dt><dd class="chapters">1/1</dd>
            <dt class="kudos">Kudos:</dt><dd class="kudos">10</dd>
          </dl>
        </li>
        """;
}
