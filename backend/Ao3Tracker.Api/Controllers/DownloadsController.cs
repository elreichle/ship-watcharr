using System.Security.Claims;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Downloads;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// Requesting downloadable copies of works, and the queue of what has been asked for.
///
/// Nothing here fetches anything. A request is a row, drained later by the download worker through
/// the same rate gate every other outbound request goes through — so a 5-8s gate never becomes a
/// hung page. The one case that completes inside the request is the one that needs no fetch at
/// all: the bytes for this exact version are already on disk, put there by this reader's earlier
/// request or by another reader's.
/// </summary>
/// <remarks>
/// Routed at <c>api</c> rather than <c>api/downloads</c> because the request that creates one hangs
/// off the work it is for (<c>POST /api/works/{workId}/downloads</c>) while the queue it joins is a
/// list of the caller's own. Splitting those across two controllers would put one feature's
/// scoping rules in two files.
/// </remarks>
[ApiController]
[Authorize]
[Route("api")]
public class DownloadsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DownloadWakeSignal _wake;

    public DownloadsController(AppDbContext db, DownloadWakeSignal wake)
    {
        _db = db;
        _wake = wake;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request missing user id claim.");

    /// <summary>
    /// Everything the caller has asked for, newest first.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> scoped to the caller's watched ships, unlike every other list in
    /// this app. A download is a request this reader made, not a view of the library: unfollowing
    /// the ship a work came under would otherwise hide the row and with it the only way to delete
    /// it, stranding a file nobody can reach on disk.
    /// </remarks>
    [HttpGet("downloads")]
    public async Task<ActionResult<IReadOnlyList<DownloadDto>>> GetDownloads(CancellationToken ct)
    {
        var userId = CurrentUserId;

        var rows = await _db.Downloads
            .Where(d => d.UserId == userId)
            // Tie-broken by id for the same reason the library list is: requests made in the same
            // tick share a timestamp, and without a total order they can swap places between calls.
            .OrderByDescending(d => d.RequestedAt)
            .ThenByDescending(d => d.Id)
            .Select(d => new DownloadDto(
                d.Id,
                d.WorkId,
                d.Work.Title,
                d.Format.ToString(),
                d.Status.ToString(),
                d.File == null ? null : d.File.SizeBytes,
                d.ErrorMessage,
                d.RequestedAt,
                d.CompletedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>
    /// Asks for one format of one work. Answers with the request as it now stands — queued, or
    /// already complete.
    /// </summary>
    /// <remarks>
    /// Idempotent per (caller, work, format), which is what the unique index behind it enforces:
    /// clicking a format button twice must not queue two fetches of identical bytes. So this
    /// returns 200 rather than 201 — a re-request is not a creation, and the caller cannot act on
    /// the difference. Which of four things it does depends on what is already there:
    /// <list type="bullet">
    /// <item>Nothing, and no file for this version — queued.</item>
    /// <item>Nothing, but another reader already fetched this exact version — complete at once,
    /// with no request to AO3.</item>
    /// <item>A fetch already in flight — left alone: that row belongs to the worker until it
    /// finishes with it.</item>
    /// <item>A request that failed, or that holds a copy of a version the work has since moved
    /// past — re-armed, which completes it on the spot if the bytes for the current version have
    /// turned up meanwhile.</item>
    /// </list>
    /// </remarks>
    [HttpPost("works/{workId:long}/downloads")]
    public async Task<ActionResult<DownloadDto>> RequestDownload(
        long workId,
        RequestDownloadRequest request,
        CancellationToken ct)
    {
        var userId = CurrentUserId;

        // The same scoping the library list applies, so a work under no ship this reader follows
        // is a 404 here exactly as it is there — which is also what stops this being used to
        // discover what other people's instances hold.
        var work = await WorkQueries.Library(_db, userId, shipId: null)
            .FirstOrDefaultAsync(w => w.Id == workId, ct);

        if (work is null) return NotFound();

        // Enum.TryParse alone is not the check: handed a number it returns whatever byte was
        // asked for, defined or not, so "0" or "99" would otherwise be written to the column.
        if (!Enum.TryParse(request.Format, ignoreCase: true, out Ao3DownloadFormat format)
            || !Enum.IsDefined(format))
        {
            ModelState.AddModelError(
                nameof(request.Format),
                $"'{request.Format}' is not a format this library can fetch.");
            return ValidationProblem(ModelState);
        }

        var existing = await _db.Downloads
            .FirstOrDefaultAsync(d => d.UserId == userId && d.WorkId == workId && d.Format == format, ct);

        // The version identity of the bytes: a file fetched when the work said it was last updated
        // at some earlier time is a copy of a work that has since changed, not of this one.
        var onDisk = await _db.WorkDownloadFiles.FirstOrDefaultAsync(
            f => f.WorkId == workId && f.Format == format && f.WorkUpdatedAt == work.UpdatedAt, ct);

        if (existing is not null)
        {
            var holdsThisVersion = existing.Status == DownloadStatus.Complete
                && onDisk is not null
                && existing.WorkDownloadFileId == onDisk.Id;

            if (existing.Status == DownloadStatus.Downloading || holdsThisVersion)
            {
                return Ok(ToDto(existing, work.Title, holdsThisVersion ? onDisk!.SizeBytes : null));
            }

            // A request still queued falls through to here rather than being left alone: re-arming
            // it changes nothing unless the bytes have appeared on disk since it was made, in which
            // case it stops being a fetch anyone has to perform.
            Arm(existing, onDisk);
            await _db.SaveChangesAsync(ct);
            WakeTheWorker(existing);

            return Ok(ToDto(existing, work.Title, onDisk?.SizeBytes));
        }

        var download = new Download
        {
            UserId = userId,
            WorkId = workId,
            Format = format,
            RequestedAt = DateTime.UtcNow,
        };

        Arm(download, onDisk);
        _db.Downloads.Add(download);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two requests from this reader for this format of this work, in flight together — two
            // clicks on one button. The unique index caught the loser. What the winner queued is
            // what this caller asked for, so its row is this request's answer rather than an error:
            // same shape as the state-insert race in WorksController.SetWorkState, without the
            // merge, because the two requests cannot have asked for different things.
            _db.Entry(download).State = EntityState.Detached;

            var winner = await _db.Downloads.FirstOrDefaultAsync(
                d => d.UserId == userId && d.WorkId == workId && d.Format == format, ct);

            // No winner means the write failed for a reason other than the race this catch is for.
            // Rethrowing keeps that a 500 carrying its own cause.
            if (winner is null) throw;

            WakeTheWorker(winner);
            return Ok(ToDto(winner, work.Title, onDisk?.SizeBytes));
        }

        WakeTheWorker(download);
        return Ok(ToDto(download, work.Title, onDisk?.SizeBytes));
    }

    /// <summary>
    /// Drops the caller's request. Someone else's, or one that never existed, is a 404 alike —
    /// answering differently would report whether a given id belongs to another reader.
    /// </summary>
    /// <remarks>
    /// Removes the request only. The file it pointed at is shared and stays: another reader's
    /// request may name the same bytes, and the fetch that produced them cost AO3 a page load that
    /// deleting them would spend again. Reclaiming files nothing references is a separate job and
    /// deliberately not this one.
    /// </remarks>
    [HttpDelete("downloads/{id:int}")]
    public async Task<IActionResult> DeleteDownload(int id, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var download = await _db.Downloads.FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId, ct);
        if (download is null) return NotFound();

        _db.Downloads.Remove(download);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A second delete from this same reader removed the row between the read above and this
            // write. The state it asked for is the state that now holds, so this request succeeded.
            _db.Entry(download).State = EntityState.Detached;
        }

        return NoContent();
    }

    /// <summary>
    /// Tells <see cref="DownloadWorker"/> there is something to fetch, when there is.
    /// </summary>
    /// <remarks>
    /// Without this a queued request waits out the worker's poll interval before it even starts
    /// waiting on the rate gate. Nothing is sent for a request that completed off a file already on
    /// disk, and nothing for one already in flight — the worker holds that row and re-reading the
    /// queue would tell it nothing new.
    /// </remarks>
    private void WakeTheWorker(Download download)
    {
        if (download.Status == DownloadStatus.Pending) _wake.Wake();
    }

    /// <summary>
    /// Points a request at the bytes for the work's current version, or at the queue when there are
    /// none. The one place a request becomes complete without a fetch.
    /// </summary>
    /// <remarks>
    /// A request re-armed onto no file loses the reference to the copy it previously held, which is
    /// deliberate — a row reporting Complete beside bytes of a different version is worse than one
    /// reporting Pending — but it does mean a reader whose refetch then fails is left with neither.
    /// T59 owns that trade.
    /// </remarks>
    private static void Arm(Download download, WorkDownloadFile? onDisk)
    {
        // The foreign key rather than the navigation: File is not loaded on this path, and
        // assigning it would have EF treat "not loaded" as "no file" on the row that wins.
        download.WorkDownloadFileId = onDisk?.Id;
        download.Status = onDisk is null ? DownloadStatus.Pending : DownloadStatus.Complete;
        download.CompletedAt = onDisk is null ? null : DateTime.UtcNow;
        download.ErrorMessage = null;
    }

    /// <param name="sizeBytes">The size of the file this request now points at, or null where it
    /// points at none. Passed in rather than read off <c>download.File</c>, which is not loaded on
    /// these paths and would report every request as fileless.</param>
    private static DownloadDto ToDto(Download download, string workTitle, long? sizeBytes) => new(
        download.Id,
        download.WorkId,
        workTitle,
        download.Format.ToString(),
        download.Status.ToString(),
        sizeBytes,
        download.ErrorMessage,
        download.RequestedAt,
        download.CompletedAt);
}
