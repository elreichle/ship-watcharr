using System.Security.Claims;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Html;
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

    /// <summary>
    /// The half-star scale a rating sits on, matching the <c>CK_UserWorkStates_Rating</c> check
    /// constraint exactly — 7 is three and a half stars. Restated here so a bad rating is a 400
    /// naming the field rather than a constraint violation on the way out.
    /// </summary>
    private const int MinRating = 1;
    private const int MaxRating = 10;

    /// <summary>Matches <c>UserWorkState.Note</c>'s column length.</summary>
    private const int MaxNoteLength = 4000;

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

        // Closed over rather than called inside an expression tree: EF translates a captured
        // queryable into a correlated subquery, where a method call would not translate at all.
        // The same queryable serves the filter's per-user criteria and the projection below, so a
        // row's own state and the clause that selected it can never be read for different users.
        var myStates = WorkQueries.StatesOf(_db, userId);

        if (filter is not null) query = WorkQueries.ApplyFilter(query, filter, myStates);

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

                // The caller's own row if there is one, and null where there is not — which the
                // mapping below turns into the same cleared state a stored row saying nothing
                // would produce. Work has no navigation to it on purpose: state is per-user, and a
                // navigation is an invitation to load it without saying whose.
                State = myStates
                    .Where(s => s.WorkId == w.Id)
                    .Select(s => new { s.Status, s.Rating, s.Note })
                    .FirstOrDefault(),
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
            r.IsRestricted,
            r.State is null
                ? WorkStateDto.Cleared
                : new WorkStateDto(r.State.Status.ToString(), r.State.Rating, r.State.Note))).ToList();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return Ok(new PagedResult<WorkListItemDto>(items, page, pageSize, totalCount, totalPages));
    }

    // ---- one work -------------------------------------------------------------------------------

    /// <summary>
    /// Everything held about one work, plus the caller's own state on it.
    /// </summary>
    /// <remarks>
    /// Reads the database and nothing else — every field here was written by a listing scrape, so
    /// opening a work costs AO3 no request at all. The scoping is the list's: a work no ship the
    /// caller follows carries is a 404, which is what stops this being a way to read another user's
    /// library by guessing AO3 work numbers.
    ///
    /// The summary is sanitized on the way out rather than left to the client. It is markup a
    /// stranger typed into AO3 and this app stored verbatim, and this is the first endpoint that
    /// hands it to a browser at all — see <see cref="WorkSummaryHtml"/>.
    /// </remarks>
    [HttpGet("{id:long}")]
    public async Task<ActionResult<WorkDetailDto>> GetWork(long id, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var myStates = WorkQueries.StatesOf(_db, userId);

        var watchedShipIds = _db.WatchedShips
            .Where(w => w.UserId == userId)
            .Select(w => w.ShipId);

        var row = await WorkQueries.Library(_db, userId, shipId: null)
            .Where(w => w.Id == id)
            .Select(w => new
            {
                w.Id,
                w.Title,
                w.SummaryHtml,
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
                w.CollectionCount,
                w.LanguageName,
                w.LanguageCode,
                w.UpdatedAt,
                w.UpdatedAtIsApproximate,
                w.PublishedAt,
                w.DetailFetchedAt,
                w.IsRestricted,
                w.FirstSeenAt,
                w.LastSeenAt,

                Authors = w.Authors.OrderBy(a => a.Position).Select(a => a.Pseud.DisplayName).ToList(),

                // Kind first, so the list reads the way AO3 renders one, and by the normalized name
                // within a kind so two scrapes of the same work never order its freeforms
                // differently. Normalized rather than Name for the reason WorkQueries.Order refuses
                // to offer a title sort: SQLite and PostgreSQL disagree about where a lowercase tag
                // sorts, so ordering on the display text would order one instance's tags one way
                // and another's another.
                Tags = w.Tags
                    .OrderBy(t => t.Tag.Type)
                    .ThenBy(t => t.Tag.NameNormalized)
                    .Select(t => new { t.Tag.Type, t.Tag.Name })
                    .ToList(),

                Series = w.Series
                    .OrderBy(s => s.Series.Title)
                    .Select(s => new WorkSeriesDto(s.SeriesId, s.Series.Title, s.Part))
                    .ToList(),

                // The reader's own subscriptions, as on the list: which other ships this instance
                // tracks for other people is not something a work page should leak.
                Ships = w.Ships
                    .Where(sw => watchedShipIds.Contains(sw.ShipId))
                    .OrderBy(sw => sw.Ship.CanonicalTagName)
                    .Select(sw => new WorkShipDto(sw.ShipId, sw.Ship.CanonicalTagName))
                    .ToList(),

                State = myStates
                    .Where(s => s.WorkId == w.Id)
                    .Select(s => new { s.Status, s.Rating, s.Note })
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return NotFound();

        return Ok(new WorkDetailDto(
            row.Id,
            row.Title,
            row.Authors,
            row.IsAnonymous,
            WorkSummaryHtml.Sanitize(row.SummaryHtml),
            Ao3Labels.Describe(row.Rating),
            Ao3Labels.Describe(row.Categories),
            Ao3Labels.Describe(row.Warnings),
            [.. row.Tags.Select(t => new WorkTagDto(t.Type.ToString(), t.Name))],
            row.Series,
            row.Ships,
            row.IsComplete,
            row.WordCount,
            row.ChapterCount,
            row.PlannedChapterCount,
            row.Kudos,
            row.Hits,
            row.Bookmarks,
            row.CommentCount,
            row.CollectionCount,
            row.LanguageName,
            row.LanguageCode,
            row.UpdatedAt,
            row.UpdatedAtIsApproximate,
            row.PublishedAt,
            row.DetailFetchedAt,
            row.IsRestricted,
            row.FirstSeenAt,
            row.LastSeenAt,
            row.State is null
                ? WorkStateDto.Cleared
                : new WorkStateDto(row.State.Status.ToString(), row.State.Rating, row.State.Note)));
    }

    // ---- one reader's own state ---------------------------------------------------------------

    /// <summary>
    /// What the caller has made of one work. A work they cannot see is a 404, not an empty state —
    /// the same scoping <see cref="GetWorks"/> applies, so "no watched ship carries this" and "AO3
    /// deleted it" answer alike here and there.
    /// </summary>
    [HttpGet("{id:long}/state")]
    public async Task<ActionResult<WorkStateDto>> GetWorkState(long id, CancellationToken ct)
    {
        var userId = CurrentUserId;

        if (!await IsInLibraryAsync(userId, id, ct)) return NotFound();

        var state = await WorkQueries.StatesOf(_db, userId)
            .Where(s => s.WorkId == id)
            .Select(s => new WorkStateDto(s.Status.ToString(), s.Rating, s.Note))
            .FirstOrDefaultAsync(ct);

        return Ok(state ?? WorkStateDto.Cleared);
    }

    /// <summary>
    /// Replaces the caller's state on one work. Nothing here can name an owner: the work id is all
    /// a request may say and the claim decides the rest, which is what makes reading or writing
    /// someone else's state unreachable rather than merely filtered out.
    /// </summary>
    /// <remarks>
    /// A state with nothing left in it is stored as <b>no row</b>, and that is the canonical form:
    /// a row saying <see cref="ReadingStatus.None"/> with no rating and no note means exactly what
    /// an absent row means, so keeping both would leave every "unread" query with two cases to
    /// cover instead of one. Callers cannot tell the difference — both read back as
    /// <see cref="WorkStateDto.Cleared"/>. Only a wholly empty state is an absence: clearing a
    /// rating while a status stands keeps the row, since the row still holds something.
    /// </remarks>
    [HttpPut("{id:long}/state")]
    public async Task<ActionResult<WorkStateDto>> SetWorkState(
        long id,
        SetWorkStateRequest request,
        CancellationToken ct)
    {
        var userId = CurrentUserId;

        if (!await IsInLibraryAsync(userId, id, ct)) return NotFound();

        var status = ReadingStatus.None;
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (Enum.TryParse(request.Status, ignoreCase: true, out ReadingStatus parsed) && Enum.IsDefined(parsed))
            {
                status = parsed;
            }
            else
            {
                ModelState.AddModelError(
                    nameof(request.Status),
                    $"'{request.Status}' is not a reading status this library offers.");
            }
        }

        // Checked here rather than left to the column's check constraint, which would answer a
        // typo with a 500 and take the whole SaveChanges with it.
        if (request.Rating is int rating && rating is < MinRating or > MaxRating)
        {
            ModelState.AddModelError(
                nameof(request.Rating),
                $"A rating is {MinRating}-{MaxRating} half-stars, or absent for unrated.");
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is not null && note.Length > MaxNoteLength)
        {
            ModelState.AddModelError(
                nameof(request.Note),
                $"A note is at most {MaxNoteLength} characters.");
        }

        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var stored = await WorkQueries.StatesOf(_db, userId).FirstOrDefaultAsync(s => s.WorkId == id, ct);

        if (status == ReadingStatus.None && request.Rating is null && note is null)
        {
            if (stored is null) return Ok(WorkStateDto.Cleared);

            _db.UserWorkStates.Remove(stored);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A second clear from this same reader removed the row between the read above and
                // this write. The state it asked for is the state that now holds, so this request
                // succeeded — reporting the 500 an unhandled concurrency failure would produce
                // would be describing someone else's win as this caller's error.
                _db.Entry(stored).State = EntityState.Detached;
            }

            return Ok(WorkStateDto.Cleared);
        }

        var now = DateTime.UtcNow;
        var isInsert = stored is null;

        stored ??= new UserWorkState { UserId = userId, WorkId = id, CreatedAt = now };
        if (isInsert) _db.UserWorkStates.Add(stored);

        stored.Status = status;
        stored.Rating = request.Rating;
        stored.Note = note;
        stored.UpdatedAt = now;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (isInsert)
        {
            // Two requests from this same reader for this same work, in flight together — a rating
            // and a status set from one feed row — each found no row and each inserted one. The
            // unique index on (UserId, WorkId) caught the loser. Same shape as the ship-insert race
            // in ShipsController.ResolveShipAsync, with one difference: a Ship has nothing to merge
            // and this does, because PUT replaces, so what this request asked for is written onto
            // the row that won rather than discarded with the losing insert.
            _db.Entry(stored).State = EntityState.Detached;

            var winner = await WorkQueries.StatesOf(_db, userId).FirstOrDefaultAsync(s => s.WorkId == id, ct);

            // No winner means the write failed for some reason other than the race this catch is
            // for. Rethrowing keeps that a 500 carrying its own cause rather than a confusing
            // null-reference further up.
            if (winner is null) throw;

            winner.Status = status;
            winner.Rating = request.Rating;
            winner.Note = note;
            winner.UpdatedAt = now;

            await _db.SaveChangesAsync(ct);
            stored = winner;
        }

        return Ok(new WorkStateDto(stored.Status.ToString(), stored.Rating, stored.Note));
    }

    /// <summary>
    /// Whether the caller can see this work at all — the one question both state endpoints ask
    /// before anything else, through the same query the list is built from.
    /// </summary>
    private Task<bool> IsInLibraryAsync(string userId, long workId, CancellationToken ct) =>
        WorkQueries.Library(_db, userId, shipId: null).AnyAsync(w => w.Id == workId, ct);

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
