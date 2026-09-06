using System.Security.Claims;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// Where the caller is in a work, as the in-app reader left it. PER-USER, like reading state, and
/// scoped the same way: the work id is all a request may say and the claim decides whose place
/// it is.
/// </summary>
/// <remarks>
/// Not part of <c>WorksController.SetWorkState</c> on purpose. That endpoint replaces a whole
/// state a reader edits deliberately and stores an empty one as no row; this is written every few
/// seconds by a page the reader is scrolling. See <see cref="ReadingPosition"/>.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/works/{id:long}/reading-position")]
public class ReadingPositionsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ReadingPositionsController(AppDbContext db)
    {
        _db = db;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request missing user id claim.");

    /// <summary>The caller's place in the work, or 204 where they have none yet.</summary>
    [HttpGet]
    public async Task<ActionResult<ReadingPositionDto>> GetReadingPosition(long id, CancellationToken ct)
    {
        var userId = CurrentUserId;

        if (!await IsReachableAsync(userId, id, ct)) return NotFound();

        var position = await _db.ReadingPositions.AsNoTracking()
            .Where(p => p.UserId == userId && p.WorkId == id)
            .Select(p => new ReadingPositionDto(p.ChapterIndex, p.BlockIndex, p.Progress, p.UpdatedAt))
            .FirstOrDefaultAsync(ct);

        return position is null ? NoContent() : Ok(position);
    }

    /// <summary>Replaces the caller's place in the work.</summary>
    [HttpPut]
    public async Task<ActionResult<ReadingPositionDto>> SetReadingPosition(
        long id, SetReadingPositionRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId;

        if (request.ChapterIndex < 0)
            ModelState.AddModelError(nameof(request.ChapterIndex), "A chapter index is zero or more.");
        if (request.BlockIndex < 0)
            ModelState.AddModelError(nameof(request.BlockIndex), "A block index is zero or more.");
        if (double.IsNaN(request.Progress) || request.Progress < 0 || request.Progress > 1)
            ModelState.AddModelError(nameof(request.Progress), "Progress is a fraction from 0 to 1.");

        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        if (!await IsReachableAsync(userId, id, ct)) return NotFound();

        var now = DateTime.UtcNow;

        var stored = await _db.ReadingPositions
            .FirstOrDefaultAsync(p => p.UserId == userId && p.WorkId == id, ct);

        if (stored is null)
        {
            stored = new ReadingPosition { UserId = userId, WorkId = id };
            _db.ReadingPositions.Add(stored);
        }

        Apply(stored, request, now);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (_db.Entry(stored).State == EntityState.Added)
        {
            // Two saves from this reader's own page in flight together, both finding no row: the
            // unique index caught the loser. What the winner wrote is a moment older than this,
            // so this one lands on top of it — same shape as RequestDownload's race, with an
            // update instead of a read-back because the two did not ask for the same thing.
            _db.Entry(stored).State = EntityState.Detached;

            var winner = await _db.ReadingPositions
                .FirstOrDefaultAsync(p => p.UserId == userId && p.WorkId == id, ct);

            // No winner means the write failed for a reason other than the race this catch is for.
            if (winner is null) throw;

            Apply(winner, request, now);
            await _db.SaveChangesAsync(ct);
            stored = winner;
        }

        return Ok(new ReadingPositionDto(stored.ChapterIndex, stored.BlockIndex, stored.Progress, stored.UpdatedAt));
    }

    private static void Apply(ReadingPosition position, SetReadingPositionRequest request, DateTime now)
    {
        position.ChapterIndex = request.ChapterIndex;
        position.BlockIndex = request.BlockIndex;
        position.Progress = request.Progress;
        position.UpdatedAt = now;
    }

    /// <summary>
    /// Reachable rather than in the library, for the reason the download request gives: a work
    /// that has left a followed tag is still one this reader may be halfway through.
    /// </summary>
    private Task<bool> IsReachableAsync(string userId, long workId, CancellationToken ct) =>
        WorkQueries.Reachable(_db, userId, shipId: null).AnyAsync(w => w.Id == workId, ct);
}
