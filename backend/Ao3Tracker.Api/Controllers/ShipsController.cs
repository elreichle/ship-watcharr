using System.Security.Claims;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// The ships the current user watches, and the endpoints for starting and stopping.
///
/// Watching is a subscription to shared data, never a private copy. Two users who name the same
/// tag land on one <c>Ship</c> row driven by one <c>ScrapeJob</c>; unwatching removes only the
/// subscription, and leaves the ship, its works and its scrape history alone for whoever else is
/// still watching. Everything below follows from that.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ships")]
public class ShipsController : ControllerBase
{
    /// <summary>Matches the <c>CanonicalTagName</c> column. AO3's own limit is lower.</summary>
    private const int MaxTagNameLength = 200;

    private static readonly TimeSpan DefaultScrapeInterval = TimeSpan.FromHours(6);

    private readonly AppDbContext _db;
    private readonly ScraperRegistry _scraperRegistry;
    private readonly ScrapingGate _gate;
    private readonly ScrapeWakeSignal _scrapeWake;

    public ShipsController(
        AppDbContext db,
        ScraperRegistry scraperRegistry,
        ScrapingGate gate,
        ScrapeWakeSignal scrapeWake)
    {
        _db = db;
        _scraperRegistry = scraperRegistry;
        _gate = gate;
        _scrapeWake = scrapeWake;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request missing user id claim.");

    [HttpGet]
    public async Task<ActionResult<WatchedShipsDto>> GetWatchedShips(CancellationToken ct)
    {
        var ships = await LoadWatchedShipsAsync(null, ct);

        // Asked of the same gate the workers consult, so the page cannot claim checks are running
        // while the worker is sitting them out — and, for the login, cannot leave a user staring at
        // an empty library with nothing on screen saying why.
        var gate = await _gate.EvaluateAsync(ct);

        return Ok(new WatchedShipsDto(ships, gate.IdentityConfigured, gate.Ao3LoginConfigured));
    }

    /// <summary>
    /// The current user's subscriptions, or just one of them when <paramref name="shipId"/> is
    /// given. Shared by the list endpoint and by the response to a successful watch, so a client
    /// never sees two differently-shaped versions of the same row.
    /// </summary>
    private async Task<List<WatchedShipDto>> LoadWatchedShipsAsync(int? shipId, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var rows = await _db.WatchedShips
            .Where(w => w.UserId == userId)
            .Where(w => shipId == null || w.ShipId == shipId)
            .OrderBy(w => w.Ship.CanonicalTagName)
            .Select(w => new
            {
                w.ShipId,
                w.Ship.CanonicalTagName,
                w.NotificationsEnabled,
                w.CreatedAt,
                w.RequestedTagName,
                w.Ship.BackfillState,
                w.Ship.BackfillNextPage,
                w.Ship.BackfillStalledRuns,
                w.Ship.VerificationState,
                w.Ship.VerificationError,

                // A work that left the tag, or that AO3 deleted, is still a row — the sweep records
                // the absence rather than dropping the history. Neither belongs in a count of what
                // is currently there.
                WorkCount = w.Ship.Works.Count(sw => sw.MissingSinceAt == null && !sw.Work.IsDeleted),
                WatcherCount = w.Ship.Watchers.Count,

                Job = _db.ScrapeJobs
                    .Where(j => j.ShipId == w.ShipId)
                    .OrderBy(j => j.Id)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        // Enum-to-string and the registry lookup both happen here rather than in the projection:
        // neither is translatable to SQL, and a user's subscription list is small enough that the
        // round trip costs nothing.
        return [.. rows.Select(r => new WatchedShipDto(
            r.ShipId,
            r.CanonicalTagName,
            r.NotificationsEnabled,
            r.CreatedAt,
            r.WorkCount,
            r.WatcherCount,
            r.BackfillState.ToString(),
            r.Job?.LastRunAt,
            r.Job?.NextRunAt,
            r.Job?.IsEnabled ?? false,
            r.Job is not null && _scraperRegistry.TryGet(r.Job.ScraperKey) is not null,
            r.VerificationState.ToString(),
            r.VerificationError,
            r.RequestedTagName,
            r.BackfillNextPage,
            r.BackfillStalledRuns))];
    }

    [HttpPost]
    public async Task<ActionResult<WatchedShipDto>> WatchShip(AddWatchedShipRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var tagName = (request.TagName ?? string.Empty).Trim();
        if (tagName.Length == 0)
        {
            ModelState.AddModelError(nameof(request.TagName), "Enter the relationship tag you want to track.");
            return ValidationProblem(ModelState);
        }

        if (tagName.Length > MaxTagNameLength)
        {
            ModelState.AddModelError(
                nameof(request.TagName),
                $"That tag is longer than {MaxTagNameLength} characters, so it isn't an AO3 tag.");
            return ValidationProblem(ModelState);
        }

        var ship = await FindOrCreateShipAsync(tagName, ct);

        if (await _db.WatchedShips.AnyAsync(w => w.UserId == userId && w.ShipId == ship.Id, ct))
            return Conflict(new { message = $"You are already tracking {ship.CanonicalTagName}." });

        _db.WatchedShips.Add(new WatchedShip { UserId = userId, ShipId = ship.Id });
        var scheduled = await EnsureScheduledAsync(ship, ct);
        await _db.SaveChangesAsync(ct);

        // After the commit, never before: the worker sweeps in a scope of its own, so a signal sent
        // while this transaction was still open could find nothing and go back to sleep for a full
        // interval — the exact wait this is here to remove.
        if (scheduled) _scrapeWake.Wake();

        var dto = (await LoadWatchedShipsAsync(ship.Id, ct)).Single();
        return CreatedAtAction(nameof(GetWatchedShips), dto);
    }

    /// <summary>Stops watching. Shared data survives — see the class remarks.</summary>
    [HttpDelete("{shipId:int}")]
    public async Task<IActionResult> UnwatchShip(int shipId, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var watch = await _db.WatchedShips
            .FirstOrDefaultAsync(w => w.UserId == userId && w.ShipId == shipId, ct);
        if (watch is null) return NotFound();

        // Their notifications about this ship go with the subscription. Unlike reading state, which
        // survives an unfollow because it is about the *work*, a notification is about the tag: it
        // says this ship gained something, and a reader who has stopped following it has stopped
        // being told. Leaving them would keep an unread count ticking for a ship no longer on the
        // page, with nothing to click through to.
        //
        // Before the save rather than after it. There is no ordering that survives a failure
        // between the two, so the choice is which half to be left holding: a delete that lands
        // without the unfollow costs this reader notifications the next pass will send again, where
        // an unfollow that lands without the delete leaves rows nothing can ever clear — the state
        // the paragraph above says must not exist. `ct` is the request's abort token, so a client
        // that disconnects mid-call is the ordinary way into that window, not an exotic one.
        await _db.Notifications
            .Where(n => n.UserId == userId && n.ShipId == shipId)
            .ExecuteDeleteAsync(ct);

        _db.WatchedShips.Remove(watch);
        await _db.SaveChangesAsync(ct);

        // Disabled, not deleted, once the last watcher leaves: the job carries the run history, and
        // re-watching later should resume the ship where it got to rather than restart a backfill
        // that has already cost AO3 hours of requests.
        if (!await _db.WatchedShips.AnyAsync(w => w.ShipId == shipId, ct))
        {
            var jobs = await _db.ScrapeJobs.Where(j => j.ShipId == shipId && j.IsEnabled).ToListAsync(ct);
            foreach (var job in jobs) job.IsEnabled = false;
            await _db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    /// <summary>
    /// Resolves the shared <c>Ship</c> for a tag name, creating it if this instance has never seen
    /// it. Saved on its own so that the unique index on the normalized name — not this lookup — is
    /// what actually decides a race between two users naming the same new tag at once.
    /// </summary>
    private async Task<Ship> FindOrCreateShipAsync(string tagName, CancellationToken ct)
    {
        // Every lookup goes through the normalized column. Comparing CanonicalTagName directly
        // would match case-insensitively on SQLite and case-sensitively on PostgreSQL, so the same
        // tag typed in lower case would find the existing ship on one provider and create a second
        // one on the other.
        var normalized = tagName.ToUpperInvariant();

        var existing = await _db.Ships.FirstOrDefaultAsync(s => s.CanonicalTagNameNormalized == normalized, ct);
        if (existing is not null) return existing;

        var ship = new Ship
        {
            CanonicalTagName = tagName,
            CanonicalTagNameNormalized = normalized,
            TagUrlSegment = Ao3TagUrl.ToUrlSegment(tagName),
        };
        _db.Ships.Add(ship);

        try
        {
            await _db.SaveChangesAsync(ct);
            return ship;
        }
        catch (DbUpdateException)
        {
            // Someone else inserted the same tag between the read above and this write. Drop our
            // losing insert and take theirs; the tag is shared, so there is nothing to merge.
            _db.Entry(ship).State = EntityState.Detached;

            var winner = await _db.Ships.FirstOrDefaultAsync(s => s.CanonicalTagNameNormalized == normalized, ct);

            // No winner means the write failed for some reason other than the race this catch is
            // for. Rethrowing keeps that a 500 with its original stack rather than a confusing
            // null-reference somewhere further up.
            if (winner is null) throw;
            return winner;
        }
    }

    /// <summary>
    /// Gives the ship a scrape schedule, or re-enables the one it already has. Left unsaved for the
    /// caller so the subscription and its schedule commit together.
    /// </summary>
    /// <returns>
    /// Whether the ship now has an enabled schedule — false for a tag AO3 has already denied, which
    /// is the one case where there is nothing for the worker to be woken for.
    /// </returns>
    private async Task<bool> EnsureScheduledAsync(Ship ship, CancellationToken ct)
    {
        // A tag AO3 has already told us does not exist stays unscheduled, however many people
        // follow it. Enabling it here would undo what verification concluded and put a permanently
        // 404ing request back on the schedule.
        var enabled = ship.VerificationState != ShipVerificationState.NotFoundOnAo3;

        var job = await _db.ScrapeJobs.FirstOrDefaultAsync(j => j.ShipId == ship.Id, ct);
        if (job is not null)
        {
            job.IsEnabled = enabled;
            return enabled;
        }

        _db.ScrapeJobs.Add(new ScrapeJob
        {
            ShipId = ship.Id,
            Name = ship.CanonicalTagName,
            ScraperKey = Ao3ScraperKeys.ShipIndex,
            Interval = DefaultScrapeInterval,
            IsEnabled = enabled,

            // Null NextRunAt makes it due immediately, and the caller's signal has the worker pick it
            // up rather than wait for a tick. Nothing stampedes: the worker runs due jobs one at a
            // time and every request they make queues behind the shared rate gate.
            NextRunAt = null,
        });

        return enabled;
    }
}
