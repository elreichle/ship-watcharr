using System.Security.Claims;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// What the caller has been told: which followed ships have gained works, and which of those they
/// have already seen.
/// </summary>
/// <remarks>
/// Reads only. Nothing here produces a notification — that happens once, where new works are
/// ingested, and is the scraper's business rather than a reader's. See <c>WorkIngestor</c> for the
/// rule that decides what counts as news.
/// </remarks>
/// <remarks>
/// In-app only by design: no email, no push, no SMTP configuration to get wrong. The unread count
/// is a number a page polls for, which is the whole of the delivery mechanism.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/notifications")]
public class NotificationsController : ControllerBase
{
    private const int DefaultPageSize = 25;

    /// <summary>
    /// Upper bound on <c>pageSize</c>. Lower than the works list's because there is nothing to
    /// filter by here — a reader who wants more of this list wants the works list instead.
    /// </summary>
    private const int MaxPageSize = 100;

    /// <summary>
    /// How many ids one <c>mark-read</c> may name. A reader cannot hold more than
    /// <see cref="Notification.MaxPerUser"/> notifications, so anything past that is naming rows
    /// that do not exist — and without a bound the array is unbounded work an authenticated client
    /// can ask for on an endpoint whose whole result set is smaller than the request.
    /// </summary>
    private const int MaxMarkReadIds = Notification.MaxPerUser;

    private readonly AppDbContext _db;

    public NotificationsController(AppDbContext db)
    {
        _db = db;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request missing user id claim.");

    /// <summary>
    /// The caller's notifications, newest first.
    /// </summary>
    /// <param name="unreadOnly">Narrow to what has not been marked read.</param>
    /// <remarks>
    /// Ordered by id descending rather than by <c>CreatedAt</c>: one ingest stamps a whole page of
    /// new works with the same instant, so the timestamp is not a total order and paging over it
    /// could show one row twice and another not at all. The id is the order they were produced in,
    /// which for these rows is the same thing as the order they happened in.
    /// </remarks>
    [HttpGet]
    public async Task<ActionResult<PagedResult<NotificationDto>>> GetNotifications(
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        // Widened before it is multiplied. `page` has no upper bound worth imposing — the answer
        // past the end is an empty page either way — but (page - 1) * pageSize overflows int in an
        // unchecked context, and a negative offset is a provider error rather than that empty page.
        var offset = (long)(page - 1) * pageSize;

        var mine = Mine(unreadOnly);

        var totalCount = await mine.CountAsync(ct);

        var items = await mine
            .OrderByDescending(n => n.Id)
            .Skip((int)Math.Min(offset, int.MaxValue))
            .Take(pageSize)
            .Select(n => new NotificationDto(
                n.Id,
                n.ShipId,
                n.Ship.CanonicalTagName,
                n.WorkId,
                n.Work.Title,
                n.CreatedAt,
                n.ReadAt))
            .ToListAsync(ct);

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return Ok(new PagedResult<NotificationDto>(items, page, pageSize, totalCount, totalPages));
    }

    /// <summary>How many the caller has not read — the number the shell polls for.</summary>
    [HttpGet("unread-count")]
    public async Task<ActionResult<UnreadNotificationsDto>> GetUnreadCount(CancellationToken ct = default) =>
        Ok(new UnreadNotificationsDto(await Mine(unreadOnly: true).CountAsync(ct)));

    /// <summary>
    /// Marks the named notifications read, and answers with the count that survived.
    /// </summary>
    /// <remarks>
    /// Ids the caller does not own are silently ignored rather than 404ing the request: the whole
    /// query is scoped to the caller, so an id belonging to somebody else matches nothing, and
    /// saying which of the two it was would answer "does notification 41 exist" for a reader who
    /// cannot see it.
    /// </remarks>
    /// <remarks>
    /// Already-read rows are excluded from the update rather than re-stamped, so marking a list
    /// read twice does not move the first read's timestamp.
    /// </remarks>
    [HttpPost("mark-read")]
    public async Task<ActionResult<UnreadNotificationsDto>> MarkRead(
        [FromBody] MarkNotificationsReadRequest request,
        CancellationToken ct = default)
    {
        if (request.Ids is not { Count: > 0 } ids)
        {
            ModelState.AddModelError(nameof(request.Ids), "Name at least one notification to mark read.");
            return ValidationProblem(ModelState);
        }

        if (ids.Count > MaxMarkReadIds)
        {
            ModelState.AddModelError(
                nameof(request.Ids), $"Name at most {MaxMarkReadIds} notifications to mark read.");
            return ValidationProblem(ModelState);
        }

        await MarkReadAsync(Mine(unreadOnly: true).Where(n => ids.Contains(n.Id)), ct);

        return Ok(new UnreadNotificationsDto(await Mine(unreadOnly: true).CountAsync(ct)));
    }

    /// <summary>Marks everything the caller has read, and answers with the count that survived — zero.</summary>
    [HttpPost("mark-all-read")]
    public async Task<ActionResult<UnreadNotificationsDto>> MarkAllRead(CancellationToken ct = default)
    {
        await MarkReadAsync(Mine(unreadOnly: true), ct);

        return Ok(new UnreadNotificationsDto(await Mine(unreadOnly: true).CountAsync(ct)));
    }

    /// <summary>
    /// The caller's own notifications and nobody else's.
    /// </summary>
    /// <remarks>
    /// Every query in this controller starts here, which is what makes "whose notification is this"
    /// one predicate rather than four. A read that forgot it would not merely leak a count: the
    /// mark-read endpoints take ids, so it would let one reader mark another's list read.
    /// </remarks>
    private IQueryable<Notification> Mine(bool unreadOnly)
    {
        var userId = CurrentUserId;

        return _db.Notifications
            .Where(n => n.UserId == userId)
            .Where(n => !unreadOnly || n.ReadAt == null);
    }

    /// <summary>
    /// Stamps a set of the caller's unread rows, in one statement rather than a load-and-save.
    /// Marking a hundred-row list read is one round trip either way; this one does not also drag
    /// the rows through the change tracker to write a single column.
    /// </summary>
    private static Task MarkReadAsync(IQueryable<Notification> unread, CancellationToken ct) =>
        unread.ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.UtcNow), ct);
}
