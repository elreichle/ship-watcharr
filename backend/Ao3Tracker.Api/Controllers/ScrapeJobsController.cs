using System.Security.Claims;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Controllers;

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

    [HttpGet]
    public async Task<ActionResult<List<ScrapeJobDto>>> GetJobs(CancellationToken ct)
    {
        var jobs = await _db.ScrapeJobs
            .Where(j => j.UserId == CurrentUserId)
            .OrderBy(j => j.Name)
            .Select(j => new
            {
                Job = j,
                LastRun = j.Runs.OrderByDescending(r => r.StartedAt).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var dtos = jobs.Select(x => new ScrapeJobDto(
            x.Job.Id,
            x.Job.Name,
            x.Job.ScraperKey,
            (int)x.Job.Interval.TotalMinutes,
            x.Job.IsEnabled,
            x.Job.LastRunAt,
            x.Job.NextRunAt,
            x.LastRun?.Status.ToString(),
            x.LastRun?.ErrorMessage));

        return Ok(dtos);
    }

    [HttpPost]
    public async Task<ActionResult<ScrapeJobDto>> CreateJob(CreateScrapeJobRequest request, CancellationToken ct)
    {
        if (_scraperRegistry.TryGet(request.ScraperKey) is null)
            return BadRequest(new { message = $"Unknown scraper key '{request.ScraperKey}'." });

        var job = new ScrapeJob
        {
            UserId = CurrentUserId,
            Name = request.Name,
            ScraperKey = request.ScraperKey,
            Interval = TimeSpan.FromMinutes(request.IntervalMinutes),
            IsEnabled = true,
            NextRunAt = DateTimeOffset.UtcNow,
        };

        _db.ScrapeJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        return Ok(new ScrapeJobDto(job.Id, job.Name, job.ScraperKey, request.IntervalMinutes, job.IsEnabled, null, job.NextRunAt, null, null));
    }

    [HttpPost("{id:int}/enable")]
    public async Task<IActionResult> SetEnabled(int id, [FromQuery] bool enabled, CancellationToken ct)
    {
        var job = await _db.ScrapeJobs.SingleOrDefaultAsync(j => j.Id == id && j.UserId == CurrentUserId, ct);
        if (job is null) return NotFound();

        job.IsEnabled = enabled;
        if (enabled && job.NextRunAt is null) job.NextRunAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteJob(int id, CancellationToken ct)
    {
        var job = await _db.ScrapeJobs.SingleOrDefaultAsync(j => j.Id == id && j.UserId == CurrentUserId, ct);
        if (job is null) return NotFound();

        _db.ScrapeJobs.Remove(job);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("{id:int}/runs")]
    public async Task<ActionResult<List<ScrapeRunDto>>> GetRuns(int id, CancellationToken ct)
    {
        var jobExists = await _db.ScrapeJobs.AnyAsync(j => j.Id == id && j.UserId == CurrentUserId, ct);
        if (!jobExists) return NotFound();

        var runs = await _db.ScrapeRuns
            .Where(r => r.ScrapeJobId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .Select(r => new ScrapeRunDto(r.Id, r.ScrapeJobId, r.Status.ToString(), r.StartedAt, r.CompletedAt, r.ItemsScraped, r.ErrorMessage))
            .ToListAsync(ct);

        return Ok(runs);
    }
}
