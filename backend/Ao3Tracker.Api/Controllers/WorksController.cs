using System.Linq.Expressions;
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
    /// which is also what stops it being used to read another user's library.</param>
    /// <param name="sort">updated | kudos | hits | bookmarks | comments | words. Anything else is
    /// rejected rather than silently ignored — a typo'd sort that quietly returns a different
    /// order is worse than an error.</param>
    [HttpGet]
    public async Task<ActionResult<PagedResult<WorkListItemDto>>> GetWorks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] int? shipId = null,
        [FromQuery] string sort = "updated",
        [FromQuery] bool ascending = false,
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

        var watchedShipIds = _db.WatchedShips
            .Where(w => w.UserId == userId)
            .Select(w => w.ShipId);

        // Membership is ShipWork, never the work's own relationship tags: AO3 tag synonyms mean a
        // work returned by the canonical tag can render a synonym in its own blurb, so filtering
        // on tags would silently drop it. See the remarks on ShipWork.
        var query = _db.Works.Where(w => !w.IsDeleted && w.Ships.Any(sw =>
            shipId == null ? watchedShipIds.Contains(sw.ShipId) : sw.ShipId == shipId));

        var ordered = Order(query, sort, ascending);
        if (ordered is null)
        {
            ModelState.AddModelError(nameof(sort), $"'{sort}' is not a sort this endpoint offers.");
            return ValidationProblem(ModelState);
        }

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
    /// Applies the requested sort, always tie-broken by id. Null for a sort that isn't offered.
    ///
    /// The tie-break is what makes paging correct, not merely tidy: thousands of works share a
    /// kudos count, and without a total order the database may return those rows differently on
    /// each query — so a work could appear on two consecutive pages while another appears on none.
    ///
    /// Title is deliberately not offered. It has no normalized column, and ordering on it directly
    /// would put "apple" before or after "Banana" depending on whether the instance runs SQLite or
    /// PostgreSQL. See the remarks on <see cref="Tag.NameNormalized"/>.
    /// </summary>
    private static IOrderedQueryable<Work>? Order(IQueryable<Work> query, string sort, bool ascending)
    {
        IOrderedQueryable<Work> By<TKey>(Expression<Func<Work, TKey>> key) =>
            ascending ? query.OrderBy(key) : query.OrderByDescending(key);

        IOrderedQueryable<Work>? ordered = sort switch
        {
            "updated" => By(w => w.UpdatedAt),
            "kudos" => By(w => w.Kudos),
            "hits" => By(w => w.Hits),
            "bookmarks" => By(w => w.Bookmarks),
            "comments" => By(w => w.CommentCount),
            "words" => By(w => w.WordCount),
            _ => null,
        };

        if (ordered is null) return null;
        return ascending ? ordered.ThenBy(w => w.Id) : ordered.ThenByDescending(w => w.Id);
    }
}
