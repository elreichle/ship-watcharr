using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Downloads;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// Opening a downloaded EPUB in the app.
///
/// The risk is the same as the file endpoint's, because the file is the same: whose request it is,
/// and which bytes stand behind it. Both are decided by the resolver the two endpoints share, so
/// the refusals here are the file endpoint's refusals — plus the one of this endpoint's own, a
/// request for a format that is not a book.
/// </summary>
public class BookControllerTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";

    private static readonly DateTime FirstVersion = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly EpubFixture.Chapter[] Chapters =
    [
        new("preface.xhtml", "Preface", "<p>By someone.</p>"),
        new("chapter1.xhtml", "Chapter 1: Woods",
            "<h2 class=\"heading\">Woods</h2><p>They <em>meet</em>.</p><script>alert(1)</script>"),
    ];

    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Opens_the_book_and_lists_its_chapters()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, EpubFixture.Book("The Woods", Chapters));

        var complete = await RequestAsync(emma, 1, "Epub");

        var book = Book(await _host.NewBookRequest(emma).GetBook(complete.Id, default));

        Assert.Equal(complete.Id, book.DownloadId);
        Assert.Equal(1, book.WorkId);
        Assert.Equal("The Woods", book.Title);
        Assert.False(book.IsEarlierCopy);
        Assert.Equal(["Preface", "Chapter 1: Woods"], book.Chapters.Select(c => c.Title));
        Assert.Equal([0, 1], book.Chapters.Select(c => c.Index));
    }

    [Fact]
    public async Task Serves_a_chapter_sanitized()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, EpubFixture.Book("The Woods", Chapters));

        var complete = await RequestAsync(emma, 1, "Epub");

        var chapter = Chapter(await _host.NewBookRequest(emma).GetChapter(complete.Id, 1, default));

        Assert.Equal(1, chapter.Index);
        Assert.Equal("Chapter 1: Woods", chapter.Title);
        Assert.Equal("<h2>Woods</h2><p>They <em>meet</em>.</p>", chapter.Html);
    }

    [Fact]
    public async Task Answers_with_nothing_to_cache()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, EpubFixture.Book("The Woods", Chapters));

        var complete = await RequestAsync(emma, 1, "Epub");

        var controller = _host.NewBookRequest(emma);
        Book(await controller.GetBook(complete.Id, default));

        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Refuses_a_chapter_the_book_does_not_have()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, EpubFixture.Book("The Woods", Chapters));

        var complete = await RequestAsync(emma, 1, "Epub");

        Assert.IsType<NotFoundResult>((await _host.NewBookRequest(emma).GetChapter(complete.Id, 2, default)).Result);
        Assert.IsType<NotFoundResult>((await _host.NewBookRequest(emma).GetChapter(complete.Id, -1, default)).Result);
    }

    [Fact]
    public async Task Refuses_another_readers_download_as_if_it_did_not_exist()
    {
        var emma = _host.SeedUser();
        var mercy = _host.SeedUser("mercy");
        var ship = await WatchAsync(Lexa, emma);
        await WatchAsync(Lexa, mercy);
        await SeedWorksAsync(ship, 1);
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, EpubFixture.Book("The Woods", Chapters));

        var mercys = await RequestAsync(mercy, 1, "Epub");

        Assert.IsType<NotFoundResult>((await _host.NewBookRequest(emma).GetBook(mercys.Id, default)).Result);
        Assert.IsType<NotFoundResult>((await _host.NewBookRequest(emma).GetChapter(mercys.Id, 0, default)).Result);
        Assert.IsType<NotFoundResult>((await _host.NewBookRequest(emma).GetBook(999, default)).Result);
    }

    [Fact]
    public async Task Refuses_a_download_that_is_not_an_epub()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Mobi, FirstVersion, [1, 2, 3]);

        var complete = await RequestAsync(emma, 1, "Mobi");

        var refused = Problem(await _host.NewBookRequest(emma).GetBook(complete.Id, default));

        Assert.Equal(StatusCodes.Status409Conflict, refused.Status);
        Assert.Contains("MOBI", refused.Detail);
    }

    [Fact]
    public async Task Refuses_a_request_with_no_file_behind_it()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var queued = await RequestAsync(emma, 1, "Epub");
        Assert.Equal(nameof(DownloadStatus.Pending), queued.Status);

        var refused = Problem(await _host.NewBookRequest(emma).GetBook(queued.Id, default));

        Assert.Equal(StatusCodes.Status409Conflict, refused.Status);
    }

    [Fact]
    public async Task Answers_gone_when_the_file_has_left_the_disk()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        var path = await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, EpubFixture.Book("The Woods", Chapters));

        var complete = await RequestAsync(emma, 1, "Epub");
        File.Delete(path);

        var gone = Problem(await _host.NewBookRequest(emma).GetBook(complete.Id, default));

        Assert.Equal(StatusCodes.Status410Gone, gone.Status);
    }

    [Fact]
    public async Task Refuses_a_file_that_is_not_an_epub_as_the_files_fault()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        // A login page stored under an EPUB's name — the shape the fetcher guards against, and the
        // one a restored data directory can still hold.
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, "<html>log in</html>"u8.ToArray());

        var complete = await RequestAsync(emma, 1, "Epub");

        var refused = Problem(await _host.NewBookRequest(emma).GetBook(complete.Id, default));

        Assert.Equal(StatusCodes.Status422UnprocessableEntity, refused.Status);
        Assert.Contains("not a zip", refused.Detail);
    }

    [Fact]
    public async Task Reads_the_earlier_copy_while_a_newer_version_is_being_fetched_and_says_so()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, EpubFixture.Book("The Woods", Chapters));

        var complete = await RequestAsync(emma, 1, "Epub");
        Assert.Equal(nameof(DownloadStatus.Complete), complete.Status);

        await MoveWorkOnAsync(1, FirstVersion.AddDays(7));

        var requeued = await RequestAsync(emma, 1, "Epub");
        Assert.Equal(nameof(DownloadStatus.Pending), requeued.Status);

        var book = Book(await _host.NewBookRequest(emma).GetBook(requeued.Id, default));

        Assert.True(book.IsEarlierCopy);
        Assert.Equal("The Woods", book.Title);
        Assert.Equal("<p>By someone.</p>", Chapter(await _host.NewBookRequest(emma).GetChapter(requeued.Id, 0, default)).Html);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<int> WatchAsync(string tagName, ApplicationUser watcher)
    {
        var result = await _host.Ships(watcher).WatchShip(new(tagName), default);
        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    private async Task SeedWorksAsync(int shipId, params long[] ids)
    {
        await using var db = _host.NewContext();

        foreach (var id in ids)
        {
            db.Works.Add(new Work { Id = id, Title = $"Work {id}", UpdatedAt = FirstVersion });
            db.ShipWorks.Add(new ShipWork { ShipId = shipId, WorkId = id });
        }

        await db.SaveChangesAsync();
    }

    private async Task<string> SeedFileOnDiskAsync(long workId, Ao3DownloadFormat format, DateTime version, byte[] bytes)
    {
        var relativePath = DownloadPaths.Relative(workId, format, version);
        var absolutePath = DownloadPaths.Absolute(_host.DataDirectory, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, bytes);

        await using var db = _host.NewContext();

        db.WorkDownloadFiles.Add(new WorkDownloadFile
        {
            WorkId = workId,
            Format = format,
            WorkUpdatedAt = version,
            RelativePath = relativePath,
            SizeBytes = bytes.Length,
            FetchedAt = version,
        });
        await db.SaveChangesAsync();

        return absolutePath;
    }

    private async Task MoveWorkOnAsync(long workId, DateTime updatedAt)
    {
        await using var db = _host.NewContext();

        var work = await db.Works.FirstAsync(w => w.Id == workId);
        work.UpdatedAt = updatedAt;

        await db.SaveChangesAsync();
    }

    private async Task<DownloadDto> RequestAsync(ApplicationUser reader, long workId, string format)
    {
        var result = await _host.NewDownloadsRequest(reader).RequestDownload(workId, new(format), default);
        return Assert.IsType<DownloadDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private static BookDto Book(ActionResult<BookDto> result) =>
        Assert.IsType<BookDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static ChapterDto Chapter(ActionResult<ChapterDto> result) =>
        Assert.IsType<ChapterDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static ProblemDetails Problem<T>(ActionResult<T> result) =>
        Assert.IsType<ProblemDetails>(Assert.IsType<ObjectResult>(result.Result).Value);
}
