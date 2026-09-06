using System.Security.Claims;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Books;
using Ao3Tracker.Api.Services.Downloads;
using Ao3Tracker.Api.Services.Html;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// Reading a downloaded EPUB inside the app: the book's chapters, and any one chapter's text.
/// </summary>
/// <remarks>
/// Hangs off the download request rather than the work, because the bytes do: which file a reader
/// may open is decided by the same rules that decide which file they may save
/// (<see cref="StoredCopyResolver"/>), and a work can have an EPUB on disk that this reader never
/// asked for. Nothing here fetches anything — a work with no EPUB yet is asked for through the
/// download queue like any other format, and read once it lands.
///
/// The archive is opened per request and never cached. An AO3 EPUB is hundreds of KB to a few MB
/// and opening one is cheap; a cache keyed by download id would have to be invalidated every time
/// a request was re-armed onto a newer version, which is a second copy of a rule the file endpoint
/// already keeps by answering <c>no-store</c>.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/downloads/{id:int}/book")]
public class BookController : ControllerBase
{
    private readonly StoredCopyResolver _copies;

    public BookController(StoredCopyResolver copies)
    {
        _copies = copies;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request missing user id claim.");

    /// <summary>The book behind the caller's request: its title and its chapters, without their text.</summary>
    [HttpGet]
    public async Task<ActionResult<BookDto>> GetBook(int id, CancellationToken ct)
    {
        var (copy, refused) = await OpenableAsync(id, ct);
        if (refused is not null) return refused;

        try
        {
            using var book = Open(copy!);

            return Ok(new BookDto(
                id,
                copy!.WorkId,
                book.Title ?? copy.WorkTitle,
                copy.IsEarlierCopy,
                book.Chapters.Select(c => new ChapterHeadingDto(c.Index, c.Title)).ToList()));
        }
        catch (EpubFormatException e)
        {
            return Unreadable(e);
        }
    }

    /// <summary>One chapter's text, sanitized. An index the book has no chapter at is a 404.</summary>
    [HttpGet("chapters/{index:int}")]
    public async Task<ActionResult<ChapterDto>> GetChapter(int id, int index, CancellationToken ct)
    {
        var (copy, refused) = await OpenableAsync(id, ct);
        if (refused is not null) return refused;

        try
        {
            using var book = Open(copy!);

            if (index < 0 || index >= book.Chapters.Count) return NotFound();

            // A chapter of nothing — no words, no image — is still a chapter the reader asked for,
            // and an empty page says that better than an error would.
            var html = WorkChapterHtml.Sanitize(book.ReadChapter(index)) ?? "";

            return Ok(new ChapterDto(index, book.Chapters[index].Title, html));
        }
        catch (EpubFormatException e)
        {
            return Unreadable(e);
        }
    }

    /// <summary>
    /// The file behind the request, where it is one this endpoint can open — or the answer that
    /// says why not. The four refusals are the file endpoint's, with the same status for each, plus
    /// one of this endpoint's own: a request for any format but EPUB, which is a 409 because the
    /// request is real and the reader's, it just is not a book this reader opens.
    /// </summary>
    private async Task<(StoredCopy.Found? Copy, ActionResult? Refused)> OpenableAsync(int id, CancellationToken ct)
    {
        switch (await _copies.ResolveAsync(id, CurrentUserId, ct))
        {
            case StoredCopy.Found { Format: Ao3DownloadFormat.Epub } found:
                return (found, null);

            case StoredCopy.Found other:
                return (null, Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    detail: $"Only an EPUB can be read in the app; this request is for a "
                        + $"{other.Format.ToString().ToUpperInvariant()}. Ask for the EPUB to read it here."));

            case StoredCopy.NoFile:
                return (null, Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    detail: "This download has no file yet. It is still queued, or the fetch failed."));

            case StoredCopy.Gone:
                return (null, Problem(
                    statusCode: StatusCodes.Status410Gone,
                    detail: "The stored copy of this file is no longer on disk. Ask for it again to "
                        + "have it fetched."));

            case StoredCopy.Unsafe:
                return (null, Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    detail: "This download names a file outside this instance's data directory, so it "
                        + "was not opened. Check the server log."));

            default:
                return (null, NotFound());
        }
    }

    private EpubBook Open(StoredCopy.Found copy)
    {
        // Private and no-store, for the reason the file endpoint gives: the answer is this reader's
        // own, and a request re-armed onto a newer version answers the same address with a
        // different book.
        Response.Headers.CacheControl = "private, no-store";

        return EpubBook.Open(System.IO.File.OpenRead(copy.AbsolutePath));
    }

    /// <summary>
    /// A file that is on disk under the right name and is not an EPUB this reader can open. A
    /// 422 rather than a 500: the request was fine and the server is fine, it is the file that is
    /// not what its name says — which is what the message tells the reader.
    /// </summary>
    private ObjectResult Unreadable(EpubFormatException e) => Problem(
        statusCode: StatusCodes.Status422UnprocessableEntity,
        detail: $"This file cannot be opened as an EPUB: {e.Message} Save it and open it "
            + "elsewhere, or ask for it again.");
}
