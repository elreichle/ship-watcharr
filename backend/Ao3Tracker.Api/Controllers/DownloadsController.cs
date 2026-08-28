using System.Security.Claims;
using System.Text;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Downloads;
using Ao3Tracker.Api.Services.Storage;
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
    private readonly StoragePaths _paths;
    private readonly ILogger<DownloadsController> _logger;

    public DownloadsController(
        AppDbContext db,
        DownloadWakeSignal wake,
        StoragePaths paths,
        ILogger<DownloadsController> logger)
    {
        _db = db;
        _wake = wake;
        _paths = paths;
        _logger = logger;
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
                return Ok(ToDto(existing, work.Title, holdsThisVersion ? onDisk!.SizeBytes : null));
            }

            // A request still queued falls through to here rather than being left alone: re-arming
            // it changes nothing unless the bytes have appeared on disk since it was made, in which
            // case it stops being a fetch anyone has to perform.
            Arm(existing, onDisk);

            if (await TryArmAsync(existing, ct))
            {
                WakeTheWorker(existing);
                return Ok(ToDto(existing, work.Title, onDisk?.SizeBytes));
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

                return Ok(ToDto(current, work.Title, size));
            }
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
    /// Hands the caller the bytes of one of their own completed requests.
    /// </summary>
    /// <remarks>
    /// Streamed from disk rather than read into memory: an EPUB is small and a PDF of a long work
    /// is not, and this endpoint is the one place in the app where a whole file passes through it.
    ///
    /// Three answers, and they mean different things. A request that is not this reader's — or that
    /// never existed — is a bare 404 alike, the same rule <see cref="DeleteDownload"/> follows, so
    /// that no id can be probed for whose it is. The other two are about the caller's own row and
    /// therefore say what is wrong with it: a request still queued or failed is a 409, and one
    /// whose file has left the disk under it is a 410 rather than the unhandled exception that
    /// opening a missing path would otherwise be.
    /// </remarks>
    [HttpGet("downloads/{id:int}/file")]
    public async Task<IActionResult> GetDownloadFile(int id, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var request = await _db.Downloads
            .Where(d => d.Id == id && d.UserId == userId)
            .Select(d => new
            {
                d.WorkId,
                d.Format,
                d.Status,
                WorkTitle = d.Work.Title,
                RelativePath = d.File == null ? null : d.File.RelativePath,
            })
            .FirstOrDefaultAsync(ct);

        if (request is null) return NotFound();

        if (request.Status != DownloadStatus.Complete || request.RelativePath is null)
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                detail: "This download has no file yet. It is still queued, or the fetch failed.");
        }

        // Resolved through DownloadPaths rather than treated as a path: what is stored is relative
        // to the data directory precisely so the volume can be mounted somewhere else tomorrow.
        var dataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.DataDirectory));
        var absolutePath = Path.GetFullPath(DownloadPaths.Absolute(dataDirectory, request.RelativePath));

        // Nothing writes a RelativePath today but DownloadPaths.Relative, which builds it out of a
        // work id and an enum and can no more escape the data directory than it can misspell it. It
        // is checked anyway because this is the one place in the app where a value out of the
        // database becomes a file handed to whoever asked: a row saying "../../etc/passwd" — from a
        // migration, from a restored database, from a future writer with a different idea of what
        // belongs in that column — would otherwise be served in full to any signed-in reader.
        if (!absolutePath.StartsWith(dataDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            _logger.LogError(
                "Download {DownloadId} names {RelativePath}, which is not inside the data directory. "
                + "Nothing was served.", id, request.RelativePath);

            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                detail: "This download names a file outside this instance's data directory, so it "
                    + "was not served. Check the server log.");
        }

        if (!System.IO.File.Exists(absolutePath))
        {
            return Problem(
                statusCode: StatusCodes.Status410Gone,
                detail: "The stored copy of this file is no longer on disk. Ask for it again to "
                    + "have it fetched.");
        }

        // Private, because the file is served against this reader's request row and a shared cache
        // holding it would serve it to whoever asked next; no-store, because a request re-armed
        // onto a newer version of the work answers the same address with different bytes.
        Response.Headers.CacheControl = "private, no-store";
        // The Html format is served as bytes rather than as what it is (see ContentTypeFor), and a
        // browser sniffing it back into a document would undo that.
        Response.Headers.XContentTypeOptions = "nosniff";

        return PhysicalFile(
            absolutePath,
            ContentTypeFor(request.Format),
            FileNameFor(request.WorkId, request.WorkTitle, request.Format));
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

        if (System.IO.File.Exists(DownloadPaths.Absolute(_paths.DataDirectory, file.RelativePath)))
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

    /// <summary>
    /// What the file is, as far as a browser and whatever opens it next are concerned.
    /// </summary>
    /// <remarks>
    /// Every format but one is named as itself, which is what lets a phone hand an EPUB to a
    /// reading app. <see cref="Ao3DownloadFormat.Html"/> is not: AO3's HTML download is a whole
    /// document of author-supplied markup, and served from this app's own origin under its real
    /// type it would be one slipped <c>Content-Disposition</c> away from running as script inside
    /// a logged-in session. Bytes to save, with <c>nosniff</c> beside it so the browser does not
    /// decide otherwise.
    /// </remarks>
    private static string ContentTypeFor(Ao3DownloadFormat format) => format switch
    {
        Ao3DownloadFormat.Epub => "application/epub+zip",
        Ao3DownloadFormat.Mobi => "application/x-mobipocket-ebook",
        Ao3DownloadFormat.Pdf => "application/pdf",
        Ao3DownloadFormat.Azw3 => "application/vnd.amazon.ebook",
        Ao3DownloadFormat.Html => "application/octet-stream",
        // Named rather than left to the arm below, so that the one deliberate exception reads as a
        // decision and the fallback stays what it says it is: a format nobody has typed yet.
        _ => "application/octet-stream",
    };

    /// <summary>
    /// What the reader's browser saves the file as: the work's title, made safe, plus the
    /// extension the format is addressed by.
    /// </summary>
    /// <remarks>
    /// A title is text AO3 carried and an author wrote, and it is about to go into a response
    /// header and then be used as a filename by whatever receives it. So this keeps letters,
    /// digits and a handful of punctuation and turns everything else — separators, quotes,
    /// newlines, control characters, the reserved characters of two filesystems — into spaces,
    /// rather than removing a list of things known to be bad today.
    ///
    /// Non-ASCII letters are kept: titles are not all English, and ASP.NET Core encodes the header
    /// as RFC 5987 for exactly this. A title that survives none of it is not an error — a work
    /// titled entirely in punctuation is a work — so the id stands in, which is also the fallback
    /// for a title of nothing but spaces.
    /// </remarks>
    private static string FileNameFor(long workId, string workTitle, Ao3DownloadFormat format)
    {
        const int maxStemLength = 120;

        var stem = new StringBuilder(workTitle.Length);

        // Runes rather than chars, so a letter written as a surrogate pair is one thing to decide
        // about. Judged char by char, both halves of such a letter fail every test below and become
        // spaces, which would quietly reduce a title in any script outside the basic plane to the
        // work id — safe, but not what the fallback is for.
        var previous = Rune.ReplacementChar;

        foreach (var rune in workTitle.EnumerateRunes())
        {
            // A dot only after something a name is made of, which is what tells "Co." apart from
            // the "../.." a title can just as easily carry.
            var keep = Rune.IsLetterOrDigit(rune)
                || rune.Value is ' ' or '-' or '_' or '\'' or ',' or '(' or ')' or '[' or ']' or '&'
                || (rune.Value == '.' && stem.Length > 0 && Rune.IsLetterOrDigit(previous));

            // Collapsed as it is built: a title of runs of punctuation would otherwise become a
            // filename of runs of spaces.
            var next = keep ? rune : new Rune(' ');
            if (next.Value == ' ' && (stem.Length == 0 || previous.Value == ' ')) continue;

            stem.Append(next);
            previous = next;
        }

        // Trailing dots and spaces are not part of a filename on Windows — a name ending in one is
        // silently truncated there — and a leading dot hides the file on Unix.
        var name = stem.ToString().Trim().Trim('.').Trim();

        if (name.Length > maxStemLength)
        {
            // Never between the two halves of one letter: the cut is a UTF-16 index, and a lone
            // surrogate is not a character any filesystem or header encoder can do anything with.
            var cut = char.IsHighSurrogate(name[maxStemLength - 1]) ? maxStemLength - 1 : maxStemLength;

            // Dots as well as spaces: the trim above removed a trailing dot because Windows
            // silently truncates a name ending in one, and a title whose 120th character is a
            // period rebuilds exactly that shape. Only these two, since everything the loop kept
            // is a letter, a digit or one of a handful of characters it names.
            name = name[..cut].TrimEnd(' ', '.');
        }

        if (name.Length == 0) name = $"work-{workId}";

        return $"{name}.{DownloadPaths.Extension(format)}";
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
