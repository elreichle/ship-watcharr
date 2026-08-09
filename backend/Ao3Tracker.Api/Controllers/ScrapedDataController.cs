using System.Security.Claims;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/scraped-items")]
public class ScrapedDataController : ControllerBase
{
    private readonly AppDbContext _db;

    public ScrapedDataController(AppDbContext db)
    {
        _db = db;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request missing user id claim.");

    [HttpGet]
    public async Task<ActionResult<List<ScrapedItemDto>>> GetItems(
        [FromQuery] int? scrapeJobId,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);

        var query = _db.ScrapedItems
            .Where(i => i.ScrapeRun.ScrapeJob.UserId == CurrentUserId);

        if (scrapeJobId is not null)
            query = query.Where(i => i.ScrapeRun.ScrapeJobId == scrapeJobId);

        var items = await query
            .OrderByDescending(i => i.ScrapedAt)
            .Take(take)
            .Select(i => new ScrapedItemDto(i.Id, i.ScrapeRunId, i.SourceUrl, i.Title, i.PayloadJson, i.ScrapedAt))
            .ToListAsync(ct);

        return Ok(items);
    }
}
