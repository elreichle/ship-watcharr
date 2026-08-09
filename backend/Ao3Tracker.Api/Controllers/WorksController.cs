using System.Security.Claims;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// The library: scraped works, paged, scoped to the ships the current user watches.
///
/// Works are global rows, so "which works can this user see" is answered by their subscriptions
/// rather than by anything on the work itself — a work stays in the table when the last watcher
/// leaves, it just stops being reachable through here.
/// </summary>
[ApiController]
[Authorize]
[Route("api/works")]
public class WorksController : ControllerBase
{
    private const int DefaultPageSize = 25;

    /// <summary>
    /// Upper bound on <c>pageSize</c>. Each row fans out into authors, fandoms and ship names, so
    /// an unbounded page is an unbounded query, not merely a large response.
    /// </summary>
    private const int MaxPageSize = 100;

    private readonly AppDbContext _db;

    public WorksController(AppDbContext db)
    {
        _db = db;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request missing user id claim.");

    /// <param name="shipId">Restrict to one watched ship. 404s if the user does not watch it,
    /// which is also what stops it being used to read another user's library. Wins over the ship
    /// a saved filter names, since it is the page's own dropdown.</param>
    /// <param name="sort">updated | kudos | hits | bookmarks | comments | words. Anything else is
    /// rejected rather than silently ignored — a typo'd sort that quietly returns a different
    /// order is worse than an error. Omitted, a saved filter's own sort applies, then "updated".</param>
    /// <param name="savedFilterId">A saved set of criteria to apply. 404s if it isn't the caller's.</param>
    /// <param name="useDefaultFilter">Whether an unqualified request picks up the user's default
    /// set. True is the point of having a default; the works page sends false when the reader has
    /// explicitly asked for their whole library, which is otherwise inexpressible.</param>
    [HttpGet]
    public async Task<ActionResult<PagedResult<WorkListItemDto>>> GetWorks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] int? shipId = null,
        [FromQuery] string? sort = null,
        [FromQuery] bool? ascending = null,
        [FromQuery] int? savedFilterId = null,
        [FromQuery] bool useDefaultFilter = true,
        CancellationToken ct = default)
    {
        var userId = CurrentUserId;

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        if (shipId is int requested
            && !await _db.WatchedShips.AnyAsync(w => w.UserId == userId && w.ShipId == requested, ct))
        {
            return NotFound();
        }

        var filter = await ResolveFilterAsync(userId, savedFilterId, useDefaultFilter, ct);
        if (savedFilterId is not null && filter is null) return NotFound();

        // The page's own ship dropdown outranks the filter's, and the filter's is intersected with
        // the reader's subscriptions rather than trusted — see SavedWorkFilter.ShipId.
        var query = WorkQueries.Library(_db, userId, shipId ?? filter?.ShipId);
        if (filter is not null) query = WorkQueries.ApplyFilter(query, filter);

        // An explicit sort wins over the set's, so the works page's dropdown keeps working while a
        // saved view is applied. With neither, "updated" is the library's own default.
        var effectiveSort = sort ?? filter?.Sort ?? "updated";
        var effectiveAscending = ascending ?? filter?.Ascending ?? false;

        var ordered = WorkQueries.Order(query, effectiveSort, effectiveAscending);
        if (ordered is null)
        {
            ModelState.AddModelError(nameof(sort), $"'{effectiveSort}' is not a sort this endpoint offers.");
            return ValidationProblem(ModelState);
        }

        var watchedShipIds = _db.WatchedShips
            .Where(w => w.UserId == userId)
            .Select(w => w.ShipId);

        var totalCount = await query.CountAsync(ct);

        var rows = await ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(w => new
            {
                w.Id,
                w.Title,
                w.IsAnonymous,
                w.Rating,
                w.Categories,
                w.Warnings,
                w.IsComplete,
                w.WordCount,
                w.ChapterCount,
                w.PlannedChapterCount,
                w.Kudos,
                w.Hits,
                w.Bookmarks,
                w.CommentCount,
                w.LanguageName,
                w.UpdatedAt,
                w.UpdatedAtIsApproximate,
                w.IsRestricted,

                Authors = w.Authors.OrderBy(a => a.Position).Select(a => a.Pseud.DisplayName).ToList(),
                Fandoms = w.Tags.Where(t => t.Tag.Type == Ao3TagType.Fandom).Select(t => t.Tag.Name).ToList(),

                // Only the reader's own subscriptions. Which other ships this instance tracks for
                // other people is not something a work listing should leak.
                Ships = w.Ships
                    .Where(sw => watchedShipIds.Contains(sw.ShipId))
                    .Select(sw => sw.Ship.CanonicalTagName)
                    .ToList(),
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new WorkListItemDto(
            r.Id,
            r.Title,
            r.Authors,
            r.IsAnonymous,
            Ao3Labels.Describe(r.Rating),
            Ao3Labels.Describe(r.Categories),
            Ao3Labels.Describe(r.Warnings),
            r.Fandoms,
            r.Ships,
            r.IsComplete,
            r.WordCount,
            r.ChapterCount,
            r.PlannedChapterCount,
            r.Kudos,
            r.Hits,
            r.Bookmarks,
            r.CommentCount,
            r.LanguageName,
            r.UpdatedAt,
            r.UpdatedAtIsApproximate,
            r.IsRestricted)).ToList();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return Ok(new PagedResult<WorkListItemDto>(items, page, pageSize, totalCount, totalPages));
    }

    /// <summary>
    /// The saved set this request should apply, if any: the one it named, otherwise the caller's
    /// default. Null when nothing applies — including when a named set does not exist, which the
    /// caller turns into a 404 rather than quietly serving an unfiltered library.
    /// </summary>
    /// <remarks>
    /// The criteria collections are loaded because <c>ApplyFilter</c> reads them directly; without
    /// the Includes a set would apply as though it had no tag or author criteria, which fails open.
    /// </remarks>
    private async Task<SavedWorkFilter?> ResolveFilterAsync(
        string userId,
        int? savedFilterId,
        bool useDefaultFilter,
        CancellationToken ct)
    {
        if (savedFilterId is null && !useDefaultFilter) return null;

        return await _db.SavedWorkFilters
            .Where(f => f.UserId == userId)
            .Where(f => savedFilterId == null ? f.IsDefault : f.Id == savedFilterId)
            .Include(f => f.Tags)
            .Include(f => f.Authors)
            .FirstOrDefaultAsync(ct);
    }
}
