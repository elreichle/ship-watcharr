using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Services.Downloads;

/// <summary>
/// What a request for one format of one work comes to, once it has been written down: the row,
/// and the sizes of the file it reports and the copy it still holds. The sizes ride beside the row
/// rather than on it because neither navigation is loaded on this path, and reading them off
/// <c>download.File</c> would report every request as fileless.
/// </summary>
/// <param name="SizeBytes">The size of the file this request now points at, or null where it
/// points at none — which is every status but Complete.</param>
/// <param name="PreviousSizeBytes">The size of the copy the reader is still holding from before
/// the request was re-armed, on the same terms.</param>
public sealed record DownloadRequestResult(Download Download, long? SizeBytes, long? PreviousSizeBytes);

/// <summary>
/// Asking for a downloadable copy of a work, as a rule rather than an endpoint.
///
/// Two callers make the same request: the EPUB button on a work's page, through
/// <c>DownloadsController</c>, and the favorite mark, when the reader has asked for favorites to
/// fetch themselves. Both have to land on the same row and make the same four-way decision about
/// what is already there, so the decision lives here and the controller keeps what is an HTTP
/// concern — who may ask, and whether the format named is one this library can fetch.
///
/// Nothing here fetches anything. A request is a row, drained later by the download worker
/// through the same rate gate every other outbound request goes through. The one case that
/// completes inside the call is the one that needs no fetch at all: the bytes for this exact
/// version are already on disk, put there by this reader's earlier request or by another reader's.
/// </summary>
public class DownloadRequests
{
    private readonly AppDbContext _db;
    private readonly DownloadWakeSignal _wake;
    private readonly StoragePaths _paths;
    private readonly ILogger<DownloadRequests> _logger;

    public DownloadRequests(
        AppDbContext db,
        DownloadWakeSignal wake,
        StoragePaths paths,
        ILogger<DownloadRequests> logger)
    {
        _db = db;
        _wake = wake;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// Asks for one format of one work on a reader's behalf. Answers with the request as it now
    /// stands — queued, or already complete — and wakes the worker where there is something for it
    /// to do.
    /// </summary>
    /// <remarks>
    /// Idempotent per (reader, work, format), which is what the unique index behind it enforces:
    /// asking twice must not queue two fetches of identical bytes. Which of four things it does
    /// depends on what is already there:
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
    /// The caller has already settled that <paramref name="work"/> is one this reader may ask for;
    /// nothing here re-checks it.
    /// </remarks>
    public async Task<DownloadRequestResult> RequestAsync(
        string userId, Work work, Ao3DownloadFormat format, CancellationToken ct)
    {
        var workId = work.Id;

        // Untracked, because every write on this path is the conditional update below rather than
        // a save of what was read: the row can be claimed by a worker in between, and a change set
        // built from the earlier read would overwrite that claim.
        var existing = await _db.Downloads.AsNoTracking()
            .FirstOrDefaultAsync(d => d.UserId == userId && d.WorkId == workId && d.Format == format, ct);

        var onDisk = await UsableFileAsync(workId, format, work.UpdatedAt, ct);

        if (existing is not null)
        {
            var holdsThisVersion = existing.Status == DownloadStatus.Complete
                && onDisk is not null
                && existing.WorkDownloadFileId == onDisk.Id;

            if (existing.Status == DownloadStatus.Downloading || holdsThisVersion)
            {
                return new(
                    existing,
                    holdsThisVersion ? onDisk!.SizeBytes : null,
                    await StoredSizeAsync(existing.PreviousWorkDownloadFileId, ct));
            }

            // What this reader is left holding, established on disk before it is written down. One
            // of the reasons control reaches here is that the bytes this row already names have
            // gone — UsableFileAsync answers null for a file that has left the disk exactly as it
            // does for a version the work has moved past — and a reference carried across on the
            // strength of the row alone would have the queue offering a copy that is not there.
            var held = await HeldFileAsync(
                existing.WorkDownloadFileId ?? existing.PreviousWorkDownloadFileId, ct);

            // A request still queued falls through to here rather than being left alone: re-arming
            // it changes nothing unless the bytes have appeared on disk since it was made, in which
            // case it stops being a fetch anyone has to perform.
            Arm(existing, onDisk, held);

            if (await TryArmAsync(existing, ct))
            {
                WakeTheWorker(existing);

                // Whether the held copy survives this re-arm is Arm's decision, so the size
                // reported follows the row it wrote rather than the lookup it was made from: a
                // request that has just completed off a replacement is holding nothing.
                return new(
                    existing,
                    onDisk?.SizeBytes,
                    existing.PreviousWorkDownloadFileId is null ? null : held?.SizeBytes);
            }

            // The guard above is a read, and a worker can claim the row between it and the write —
            // which is the same state the guard exists to refuse, reached from the other side of
            // it. Re-read rather than assumed: the row is either in flight, and this caller is told
            // so, or it was dropped while they asked, and a fresh request is what they wanted.
            var current = await _db.Downloads.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == existing.Id, ct);

            if (current is not null)
            {
                long? size = current.WorkDownloadFileId is not null
                    && current.WorkDownloadFileId == onDisk?.Id ? onDisk.SizeBytes : null;

                return new(current, size, await StoredSizeAsync(current.PreviousWorkDownloadFileId, ct));
            }
        }

        var download = new Download
        {
            UserId = userId,
            WorkId = workId,
            Format = format,
            RequestedAt = DateTime.UtcNow,
        };

        // A row that does not exist yet has never reported a file, so there is nothing for it to
        // be holding.
        Arm(download, onDisk, held: null);
        _db.Downloads.Add(download);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two requests from this reader for this format of this work, in flight together — two
            // clicks on one button, or a click and a favorite mark landing at once. The unique
            // index caught the loser. What the winner queued is what this caller asked for, so its
            // row is this request's answer rather than an error: same shape as the state-insert
            // race in WorksController.SetWorkState, without the merge, because the two requests
            // cannot have asked for different things.
            _db.Entry(download).State = EntityState.Detached;

            var winner = await _db.Downloads.AsNoTracking().FirstOrDefaultAsync(
                d => d.UserId == userId && d.WorkId == workId && d.Format == format, ct);

            // No winner means the write failed for a reason other than the race this catch is for.
            // Rethrowing keeps that a 500 carrying its own cause.
            if (winner is null) throw;

            WakeTheWorker(winner);

            return new(winner, onDisk?.SizeBytes, await StoredSizeAsync(winner.PreviousWorkDownloadFileId, ct));
        }

        WakeTheWorker(download);

        // A row this request just created has never reported a file, so there is no earlier copy
        // for it to be holding.
        return new(download, onDisk?.SizeBytes, PreviousSizeBytes: null);
    }

    /// <summary>
    /// Writes an armed request back, but only while no worker holds it.
    /// </summary>
    /// <remarks>
    /// A conditional update rather than a concurrency token, which keeps T11's decision that the
    /// in-flight guard is checked rather than locked: what must not happen is one specific
    /// overwrite, and <c>Status != Downloading</c> in the <c>WHERE</c> is that guard applied at the
    /// instant of the write instead of a few milliseconds before it. Without it a fetcher claiming
    /// the row between the read and the save was reset to Pending mid-fetch — or, when the save
    /// landed after the fetch finished, a Complete request was reset with its
    /// <see cref="Download.WorkDownloadFileId"/> cleared, orphaning the file the worker had just
    /// recorded and costing another drain.
    /// </remarks>
    private async Task<bool> TryArmAsync(Download armed, CancellationToken ct) =>
        await _db.Downloads
            .Where(d => d.Id == armed.Id && d.Status != DownloadStatus.Downloading)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(d => d.Status, armed.Status)
                    .SetProperty(d => d.WorkDownloadFileId, armed.WorkDownloadFileId)
                    .SetProperty(d => d.PreviousWorkDownloadFileId, armed.PreviousWorkDownloadFileId)
                    .SetProperty(d => d.CompletedAt, armed.CompletedAt)
                    .SetProperty(d => d.ErrorMessage, armed.ErrorMessage),
                ct) > 0;

    /// <summary>
    /// The stored file for this work's current version, where its bytes are still on disk.
    /// </summary>
    /// <remarks>
    /// The version identity of the bytes is the row: a file fetched when the work said it was last
    /// updated at some earlier time is a copy of a work that has since changed, not of this one.
    /// The file's continued existence is not the row, and a data directory that lost bytes while
    /// keeping their row would otherwise answer this request Complete with a path to nothing — and
    /// go on doing it, since this is also what decides there is nothing to fetch. The same check
    /// the fetcher makes, for the same reason; a request that gets past it repairs the row.
    /// </remarks>
    private async Task<WorkDownloadFile?> UsableFileAsync(
        long workId, Ao3DownloadFormat format, DateTime workUpdatedAt, CancellationToken ct)
    {
        var file = await _db.WorkDownloadFiles.AsNoTracking().FirstOrDefaultAsync(
            f => f.WorkId == workId && f.Format == format && f.WorkUpdatedAt == workUpdatedAt, ct);

        if (file is null) return null;

        if (File.Exists(DownloadPaths.Absolute(_paths.DataDirectory, file.RelativePath)))
            return file;

        _logger.LogWarning(
            "The stored {Format} of work {WorkId} is recorded at {Path}, which is not on disk. "
            + "The request will be queued for a fresh fetch.", format, workId, file.RelativePath);

        return null;
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
    /// A request re-armed onto no file stops <i>reporting</i> the copy it previously held — a row
    /// saying Complete beside bytes of a version the work has moved past is worse than one saying
    /// Pending — but it does not let go of it: the bytes are still on disk and the reader still has
    /// them, so the reference moves to <see cref="Download.PreviousWorkDownloadFileId"/> and a
    /// re-fetch that fails leaves them with the old file rather than with nothing. It is dropped
    /// only where a replacement has landed, which is the one thing that supersedes it.
    /// </remarks>
    /// <param name="held">The copy this reader still has, already checked to be on disk — see
    /// <see cref="HeldFileAsync"/>. Null where they have none, which is also every brand-new
    /// request.</param>
    private static void Arm(Download download, WorkDownloadFile? onDisk, WorkDownloadFile? held)
    {
        // Nothing to hold once a replacement is on disk: that copy supersedes it.
        download.PreviousWorkDownloadFileId = onDisk is null ? held?.Id : null;

        // The foreign key rather than the navigation: File is not loaded on this path, and
        // assigning it would have EF treat "not loaded" as "no file" on the row that wins.
        download.WorkDownloadFileId = onDisk?.Id;
        download.Status = onDisk is null ? DownloadStatus.Pending : DownloadStatus.Complete;
        download.CompletedAt = onDisk is null ? null : DateTime.UtcNow;
        download.ErrorMessage = null;
    }

    /// <summary>
    /// The stored file a request would go on holding, where its bytes are still there to hold.
    /// </summary>
    /// <remarks>
    /// By id and with no version in the lookup, which is what makes it a different question from
    /// <see cref="UsableFileAsync"/>: this file is deliberately <i>not</i> the work's current
    /// version. The disk check is the same one, and for the same reason — a reference is a claim
    /// that the reader has these bytes, and writing one for bytes that have gone puts a download
    /// link on the queue that cannot answer.
    /// </remarks>
    private async Task<WorkDownloadFile?> HeldFileAsync(int? fileId, CancellationToken ct)
    {
        if (fileId is null) return null;

        var file = await _db.WorkDownloadFiles.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId, ct);

        if (file is null) return null;

        return File.Exists(DownloadPaths.Absolute(_paths.DataDirectory, file.RelativePath))
            ? file
            : null;
    }

    /// <summary>
    /// The size of one stored file, by id. For the copy a request is holding rather than reporting:
    /// unlike the file it reports, that one was never looked up on the way in.
    /// </summary>
    private async Task<long?> StoredSizeAsync(int? fileId, CancellationToken ct) =>
        fileId is null
            ? null
            : await _db.WorkDownloadFiles.AsNoTracking()
                .Where(f => f.Id == fileId)
                .Select(f => (long?)f.SizeBytes)
                .FirstOrDefaultAsync(ct);
}
