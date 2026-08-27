using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// The way back out of a written-off backfill.
///
/// A ship gives up on its back catalogue after <c>Ao3ShipIndexScraper.MaxStalledBackfillRuns</c>
/// consecutive runs against a cursor the listing will not answer. That is right while AO3 is
/// genuinely refusing and wrong the moment it stops — and nothing in the scraper moves a ship out
/// of <see cref="ShipBackfillState.Failed"/>, because a give-up that clears itself is a give-up
/// that can loop: the same run that would re-arm it is the run that would stall again, and the ship
/// would spend two requests a run forever with the counter never reaching its bound. So the exit is
/// a deliberate act by a person, and this is where they perform it.
///
/// Admin-only, and shared rather than per-user: a backfill belongs to the <c>Ship</c> every watcher
/// shares, so re-arming it spends requests on behalf of the whole instance.
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin/ships")]
public class AdminShipsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<AdminShipsController> _logger;

    public AdminShipsController(
        AppDbContext db,
        UserManager<ApplicationUser> userManager,
        ILogger<AdminShipsController> logger)
    {
        _db = db;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Puts a written-off backfill back to <see cref="ShipBackfillState.InProgress"/>, from a page
    /// the caller chooses or from where the walk gave up.
    /// </summary>
    [HttpPost("{shipId:int}/backfill/restart")]
    public async Task<ActionResult<BackfillRestartedDto>> RestartBackfill(
        int shipId, RestartBackfillRequest request, CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync()) return Forbid();

        var ship = await _db.Ships.FirstOrDefaultAsync(s => s.Id == shipId, ct);
        if (ship is null) return NotFound();

        // A tag AO3 has denied is never scraped whatever its backfill says: the scraper returns
        // before its first request, so re-arming one leaves a ship reading "in progress" that no run
        // will ever touch. A "restarted" ship that cannot move is worse than a written-off one,
        // because the written-off one at least says so.
        if (ship.VerificationState == ShipVerificationState.NotFoundOnAo3)
        {
            return Conflict(new
            {
                message = $"AO3 has no tag called {ship.CanonicalTagName}, so nothing will scrape it. "
                    + "Follow the tag under the name AO3 files it under instead.",
            });
        }

        // Same shape, one layer out: the worker only ever picks up an enabled job, so a ship whose
        // schedule is off — nobody watches it any more, or verification switched it off — would sit
        // at InProgress indefinitely.
        if (!await _db.ScrapeJobs.AnyAsync(j => j.ShipId == shipId && j.IsEnabled, ct))
        {
            return Conflict(new
            {
                message = $"{ship.CanonicalTagName} has no enabled scrape schedule, so a restarted "
                    + "backfill would never run. Follow the tag to schedule it again.",
            });
        }

        // Only a written-off backfill. Re-arming a Complete one would send a ship that has already
        // read its back catalogue back through thousands of pages of it, at 5-8 seconds a page, on
        // a single mis-click; re-arming an InProgress one would move a cursor out from under a walk
        // that is working. Neither is what this endpoint exists for, and both cost AO3 rather than
        // this instance.
        if (ship.BackfillState != ShipBackfillState.Failed)
        {
            return Conflict(new
            {
                message = $"The backfill of {ship.CanonicalTagName} is {ship.BackfillState}, not Failed. "
                    + "Only a backfill this instance gave up on can be restarted.",
            });
        }

        if (request.FromPage is { } requested && requested < 1)
        {
            ModelState.AddModelError(nameof(request.FromPage), "A listing page is numbered from 1.");
            return ValidationProblem(ModelState);
        }

        // Where the *last run landed*, which is not the same as how deep the walk ever got: a ship on
        // its way to Failed has had JumpCursorBackFrom halve its cursor once per run, so one written
        // off after an outage at page 40 usually ends up parked at page 1. It is still the honest
        // default — it is the only page this instance records — but it is the reason the choice
        // exists, and the reason nothing here claims it is the page AO3 refused.
        var fromPage = request.FromPage ?? ship.BackfillNextPage ?? 1;

        // Dropped unconditionally, not only when the cursor moves back. The floor is the oldest work
        // a *contiguous* walk has reached, and TrackBackfillFloor reports anything newer as the
        // listing shifting underneath — so a floor read at page 39 and carried into a re-walk makes
        // every page of that re-walk log "a full sweep will be needed" about a listing that never
        // moved, and the floor never updates again. Comparing `fromPage` against the stored cursor
        // to decide would get this exactly backwards in the common case, since the halving has
        // already dragged that cursor below the pages the floor came from. One page of shift
        // detection is lost at the restart point, and the next page re-establishes it.
        ship.BackfillMinUpdatedAtSeen = null;

        ship.BackfillState = ShipBackfillState.InProgress;
        ship.BackfillNextPage = fromPage;
        ship.BackfillCompletedAt = null;

        // Not a tidy-up: it is the half that makes the restart work. Both of the scraper's own reset
        // sites sit on paths a Failed ship no longer reaches, so the counter is frozen at the value
        // it gave up on — a ship re-armed without zeroing it gives up again on its very next stalled
        // run rather than twelve runs later.
        ship.BackfillStalledRuns = 0;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Backfill of ship {ShipId} ({Tag}) restarted at page {Page} by {UserId}",
            ship.Id, ship.CanonicalTagName, fromPage, _userManager.GetUserId(User));

        // The schedule is left alone on purpose. Making the ship due now would put a walk that has
        // been failing for days at the front of the queue the moment somebody pressed a button;
        // it resumes on its next scheduled run instead, which is soon enough for a back catalogue
        // and keeps the decision about request spacing in one place.
        return Ok(new BackfillRestartedDto(
            ship.Id,
            ship.CanonicalTagName,
            ship.BackfillState.ToString(),
            ship.BackfillNextPage,
            ship.BackfillStalledRuns));
    }

    private async Task<bool> IsCurrentUserAdminAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        return user?.IsAdmin == true;
    }
}
