using System.Security.Claims;
using System.Text;
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
/// hung page. What a request does to that row is <see cref="DownloadRequests"/>'s decision, shared
/// with the favorite mark that can make one; this controller keeps the HTTP half — who may ask,
/// and for what.
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
    private readonly DownloadRequests _requests;
    private readonly StoredCopyResolver _copies;

    public DownloadsController(AppDbContext db, DownloadRequests requests, StoredCopyResolver copies)
    {
        _db = db;
        _requests = requests;
        _copies = copies;
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
                d.PreviousFile == null ? null : d.PreviousFile.SizeBytes,
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
    /// the difference. What it does with the row is <see cref="DownloadRequests.RequestAsync"/>'s
    /// four-way decision; this method settles only who may ask and for what.
    /// </remarks>
    [HttpPost("works/{workId:long}/downloads")]
    public async Task<ActionResult<DownloadDto>> RequestDownload(
        long workId,
        RequestDownloadRequest request,
        CancellationToken ct)
    {
        var userId = CurrentUserId;

        // A work under no ship this reader follows is a 404, which is what stops this being used
        // to discover what other people's instances hold. Reachable rather than the list's Library:
        // a work that has left a followed tag can still be asked for, since a reader who wants a
        // copy of something AO3 may be about to lose is exactly who is asking.
        var work = await WorkQueries.Reachable(_db, userId, shipId: null)
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

        var made = await _requests.RequestAsync(userId, work, format, ct);

        return Ok(ToDto(made.Download, work.Title, made.SizeBytes, made.PreviousSizeBytes));
    }

    /// <summary>
    /// Hands the caller the bytes of one of their own completed requests.
    /// </summary>
    /// <remarks>
    /// Streamed from disk rather than read into memory: an EPUB is small and a PDF of a long work
    /// is not, and this endpoint is the one place in the app where a whole file passes through it.
    ///
    /// Which file, if any, is <see cref="StoredCopyResolver"/>'s decision, shared with the in-app
    /// reader. The answers mean different things. A request that is not this reader's — or that
    /// never existed — is a bare 404 alike, the same rule <see cref="DeleteDownload"/> follows, so
    /// that no id can be probed for whose it is. The other two are about the caller's own row and
    /// therefore say what is wrong with it: a request with nothing behind it at all is a 409, and
    /// one whose file has left the disk under it is a 410 rather than the unhandled exception that
    /// opening a missing path would otherwise be.
    ///
    /// A request that is not Complete is still served where the reader is holding a copy from
    /// before it was re-armed (<see cref="Download.PreviousWorkDownloadFileId"/>); the Downloads
    /// page labels what it offers here as the earlier copy rather than as the fetch that has not
    /// happened.
    /// </remarks>
    [HttpGet("downloads/{id:int}/file")]
    public async Task<IActionResult> GetDownloadFile(int id, CancellationToken ct)
    {
        StoredCopy.Found copy;

        switch (await _copies.ResolveAsync(id, CurrentUserId, ct))
        {
            case StoredCopy.Found found:
                copy = found;
                break;

            case StoredCopy.NoFile:
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    detail: "This download has no file yet. It is still queued, or the fetch failed.");

            case StoredCopy.Gone:
                return Problem(
                    statusCode: StatusCodes.Status410Gone,
                    detail: "The stored copy of this file is no longer on disk. Ask for it again to "
                        + "have it fetched.");

            case StoredCopy.Unsafe:
                return Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    detail: "This download names a file outside this instance's data directory, so it "
                        + "was not served. Check the server log.");

            default:
                return NotFound();
        }

        // Private, because the file is served against this reader's request row and a shared cache
        // holding it would serve it to whoever asked next; no-store, because a request re-armed
        // onto a newer version of the work answers the same address with different bytes.
        Response.Headers.CacheControl = "private, no-store";
        // The Html format is served as bytes rather than as what it is (see ContentTypeFor), and a
        // browser sniffing it back into a document would undo that.
        Response.Headers.XContentTypeOptions = "nosniff";

        return PhysicalFile(
            copy.AbsolutePath,
            ContentTypeFor(copy.Format),
            FileNameFor(copy.WorkId, copy.WorkTitle, copy.Format));
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
    /// <param name="previousSizeBytes">The size of the copy the reader is still holding, on the
    /// same terms and for the same reason.</param>
    private static DownloadDto ToDto(
        Download download, string workTitle, long? sizeBytes, long? previousSizeBytes) => new(
        download.Id,
        download.WorkId,
        workTitle,
        download.Format.ToString(),
        download.Status.ToString(),
        sizeBytes,
        previousSizeBytes,
        download.ErrorMessage,
        download.RequestedAt,
        download.CompletedAt);
}
