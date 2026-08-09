using System.Security.Claims;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// Read-only view of the scrape schedules behind the ships the current user watches.
///
/// Jobs are no longer created or deleted here: a job belongs to a <c>Ship</c>, and its lifecycle
/// follows subscription (created when the first user watches a ship, disabled when the last one
/// stops). The endpoints that add and remove watched ships arrive with the full scrape pipeline.
/// </summary>
[ApiController]
[Authorize]
[Route("api/scrape-jobs")]
public class ScrapeJobsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ScraperRegistry _scraperRegistry;

    public ScrapeJobsController(AppDbContext db, ScraperRegistry scraperRegistry)
    {
        _db = db;
        _scraperRegistry = scraperRegistry;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request missing user id claim.");

    [HttpGet("scrapers")]
    public ActionResult<IReadOnlyCollection<string>> GetAvailableScrapers() =>
        Ok(_scraperRegistry.AvailableKeys);

    /// <summary>Jobs for every ship the current user watches.</summary>
    [HttpGet]
    public async Task<ActionResult<List<ScrapeJobDto>>> GetJobs(CancellationToken ct)
    {
        var userId = CurrentUserId;

        var jobs = await _db.ScrapeJobs
            .Where(j => _db.WatchedShips.Any(w => w.UserId == userId && w.ShipId == j.ShipId))
            .OrderBy(j => j.Name)
            .Select(j => new
            {
                Job = j,
                ShipName = j.Ship.CanonicalTagName,
                LastRun = j.Runs.OrderByDescending(r => r.StartedAt).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var dtos = jobs.Select(x => new ScrapeJobDto(
            x.Job.Id,
            x.Job.ShipId,
            x.ShipName,
            x.Job.ScraperKey,
            (int)x.Job.Interval.TotalMinutes,
            x.Job.IsEnabled,
            x.Job.LastRunAt,
            x.Job.NextRunAt,
            x.LastRun?.Status.ToString(),
            x.LastRun?.ErrorMessage));

        return Ok(dtos);
    }

    [HttpGet("{id:int}/runs")]
    public async Task<ActionResult<List<ScrapeRunDto>>> GetRuns(int id, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var visible = await _db.ScrapeJobs
            .AnyAsync(j => j.Id == id
                && _db.WatchedShips.Any(w => w.UserId == userId && w.ShipId == j.ShipId), ct);
        if (!visible) return NotFound();

        var runs = await _db.ScrapeRuns
            .Where(r => r.ScrapeJobId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .Select(r => new ScrapeRunDto(
                r.Id,
                r.ScrapeJobId,
                r.Status.ToString(),
                r.Mode.ToString(),
                r.StartedAt,
                r.CompletedAt,
                r.PagesFetched,
                r.RequestsMade,
                r.WorksSeen,
                r.WorksAdded,
                r.WorksUpdated,
                r.StopReason,
                r.ErrorMessage))
            .ToListAsync(ct);

        return Ok(runs);
    }
}
