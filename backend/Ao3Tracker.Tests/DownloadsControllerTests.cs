using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// Asking for a downloadable copy of a work, and the queue that answer joins.
///
/// Three things carry the risk here, and none of them is the fetching — nothing in this file
/// fetches anything. Whose request it is: the endpoints take no user id, so a reader can name a
/// work and a format and nothing else. Whether a second ask costs AO3 a second page load, which is
/// what the (work, format, version) key on the shared file is for. And whether dropping one
/// reader's request can take bytes another reader's request still names.
/// </summary>
public class DownloadsControllerTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";

    /// <summary>
    /// The version identity every file in these tests is keyed against. Fixed rather than
    /// <c>UtcNow</c> so that "the work has since changed" is a thing a test does on purpose.
    /// </summary>
    private static readonly DateTime FirstVersion = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- queueing ------------------------------------------------------------------------------

    [Fact]
    public async Task Queues_a_request_and_fetches_nothing_while_doing_it()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var queued = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        Assert.Equal(nameof(DownloadStatus.Pending), queued.Status);
        Assert.Equal(nameof(Ao3DownloadFormat.Epub), queued.Format);
        Assert.Equal(1, queued.WorkId);
        Assert.Equal("Work 1", queued.WorkTitle);
        Assert.Null(queued.CompletedAt);
        Assert.Null(queued.SizeBytes);

        // The whole point of queueing: the request answers without waiting on the rate gate, so
        // nothing may have gone out while it was being served.
        Assert.Empty(_host.Http.Requested);

        var listed = Assert.Single(Downloads(await _host.NewDownloadsRequest(emma).GetDownloads(default)));
        Assert.Equal(queued.Id, listed.Id);
    }

    [Fact]
    public async Task Refuses_a_work_no_ship_the_reader_watches_carries()
    {
        var emma = _host.SeedUser();
        var mercy = _host.SeedUser("mercy");

        // Watched by someone else, so the work exists and is reachable through their library.
        await SeedWorksAsync(await WatchAsync(Lexa, mercy), 1);

        var refused = await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default);

        Assert.IsType<NotFoundResult>(refused.Result);
        Assert.Empty(await DownloadRowsAsync());
    }

    [Fact]
    public async Task Refuses_a_format_it_cannot_fetch()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var refused = await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Doc"), default);

        Assert.True(Rejected(refused, nameof(RequestDownloadRequest.Format)));
        Assert.Empty(await DownloadRowsAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    public async Task Refuses_a_format_number_no_format_has(string format)
    {
        // A word cannot reach the defined-ness check — Enum.TryParse has already rejected it. Only
        // a number gets that far, and TryParse hands back whatever byte it was given.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var refused = await _host.NewDownloadsRequest(emma).RequestDownload(1, new(format), default);

        Assert.True(Rejected(refused, nameof(RequestDownloadRequest.Format)));
        Assert.Empty(await DownloadRowsAsync());
    }

    [Fact]
    public async Task Refuses_a_request_naming_no_format_at_all()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var refused = await _host.NewDownloadsRequest(emma).RequestDownload(1, new(), default);

        Assert.True(Rejected(refused, nameof(RequestDownloadRequest.Format)));
    }

    [Fact]
    public async Task Queues_one_request_however_many_times_it_is_asked_for()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var first = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));
        var second = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("epub"), default));

        Assert.Equal(first.Id, second.Id);
        Assert.Single(Downloads(await _host.NewDownloadsRequest(emma).GetDownloads(default)));
    }

    [Fact]
    public async Task Keeps_two_formats_of_one_work_apart()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default);
        await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Pdf"), default);

        var listed = Downloads(await _host.NewDownloadsRequest(emma).GetDownloads(default));

        Assert.Equal(
            [nameof(Ao3DownloadFormat.Epub), nameof(Ao3DownloadFormat.Pdf)],
            listed.Select(d => d.Format).Order());
    }

    // ---- what is already on disk ---------------------------------------------------------------

    [Fact]
    public async Task Completes_at_once_when_this_version_is_already_on_disk()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion, sizeBytes: 4096);

        var queued = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        Assert.Equal(nameof(DownloadStatus.Complete), queued.Status);
        Assert.Equal(4096, queued.SizeBytes);
        Assert.NotNull(queued.CompletedAt);
        Assert.Empty(_host.Http.Requested);

        // The queue reports the same thing the request did, off its own projection.
        var listed = Assert.Single(Downloads(await _host.NewDownloadsRequest(emma).GetDownloads(default)));
        Assert.Equal(nameof(DownloadStatus.Complete), listed.Status);
        Assert.Equal(4096, listed.SizeBytes);
        Assert.Equal("Work 1", listed.WorkTitle);
    }

    [Fact]
    public async Task Re_arms_a_stale_request_onto_the_version_another_reader_already_has()
    {
        // The case a "is it Complete?" check alone gets wrong: the request holds bytes and the work
        // has moved past them, but the bytes for where it moved to are already on disk. Reading the
        // status without reading which file it names would serve the old copy as the new one.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion, sizeBytes: 4096);

        await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default);

        var secondVersion = FirstVersion.AddDays(7);
        await MoveWorkOnAsync(1, secondVersion);
        var newer = await SeedFileAsync(1, Ao3DownloadFormat.Epub, secondVersion, sizeBytes: 8192);

        var rearmed = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        Assert.Equal(nameof(DownloadStatus.Complete), rearmed.Status);
        Assert.Equal(8192, rearmed.SizeBytes);
        Assert.Empty(_host.Http.Requested);

        await using var db = _host.NewContext();
        Assert.Equal(newer, await db.Downloads.Select(d => d.WorkDownloadFileId).SingleAsync());
    }

    [Fact]
    public async Task Completes_a_queued_request_the_bytes_have_arrived_for()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var queued = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));
        Assert.Equal(nameof(DownloadStatus.Pending), queued.Status);

        await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion, sizeBytes: 4096);

        var again = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        Assert.Equal(queued.Id, again.Id);
        Assert.Equal(nameof(DownloadStatus.Complete), again.Status);
        Assert.Equal(4096, again.SizeBytes);
    }

    [Fact]
    public async Task Queues_a_fetch_when_the_copy_on_disk_is_of_an_older_version()
    {
        // Same work, same format, bytes from before the author last updated it. Serving those
        // would be answering a request for this work with a copy of a different one.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion.AddDays(-30), sizeBytes: 4096);

        var queued = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        Assert.Equal(nameof(DownloadStatus.Pending), queued.Status);
        Assert.Null(queued.SizeBytes);
    }

    [Fact]
    public async Task Queues_a_fetch_when_the_copy_on_disk_is_of_another_format()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileAsync(1, Ao3DownloadFormat.Pdf, FirstVersion, sizeBytes: 4096);

        var queued = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        Assert.Equal(nameof(DownloadStatus.Pending), queued.Status);
    }

    [Fact]
    public async Task Serves_one_readers_file_to_another_reader_without_a_second_fetch()
    {
        var emma = _host.SeedUser();
        var mercy = _host.SeedUser("mercy");

        var shipId = await WatchAsync(Lexa, emma);
        await WatchAsync(Lexa, mercy);
        await SeedWorksAsync(shipId, 1);

        var fileId = await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion, sizeBytes: 4096);

        await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default);
        var hers = Download(await _host.NewDownloadsRequest(mercy).RequestDownload(1, new("Epub"), default));

        Assert.Equal(nameof(DownloadStatus.Complete), hers.Status);
        Assert.Empty(_host.Http.Requested);

        // One file, two requests: the shared row is what stops the same bytes being fetched twice.
        await using var db = _host.NewContext();
        Assert.Equal(
            new int?[] { fileId, fileId },
            await db.Downloads.OrderBy(d => d.Id).Select(d => d.WorkDownloadFileId).ToListAsync());
        Assert.Equal(1, await db.WorkDownloadFiles.CountAsync());
    }

    [Fact]
    public async Task Re_arms_a_request_whose_copy_the_work_has_moved_past()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion, sizeBytes: 4096);

        var complete = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));
        Assert.Equal(nameof(DownloadStatus.Complete), complete.Status);

        await MoveWorkOnAsync(1, FirstVersion.AddDays(7));

        var requeued = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        Assert.Equal(complete.Id, requeued.Id);
        Assert.Equal(nameof(DownloadStatus.Pending), requeued.Status);
        Assert.Null(requeued.SizeBytes);
        Assert.Null(requeued.CompletedAt);

        // The stale bytes are let go of, not served as though they were the new version.
        await using var db = _host.NewContext();
        Assert.Null(await db.Downloads.Select(d => d.WorkDownloadFileId).SingleAsync());
    }

    [Fact]
    public async Task Leaves_a_completed_request_alone_while_the_work_stands_still()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion, sizeBytes: 4096);

        var first = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));
        var again = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        Assert.Equal(nameof(DownloadStatus.Complete), again.Status);
        Assert.Equal(first.CompletedAt, again.CompletedAt);
        Assert.Equal(4096, again.SizeBytes);
    }

    [Fact]
    public async Task Re_arms_a_request_whose_fetch_failed()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var queued = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));
        await FailAsync(queued.Id, "AO3 answered 503.");

        var retried = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        Assert.Equal(queued.Id, retried.Id);
        Assert.Equal(nameof(DownloadStatus.Pending), retried.Status);
        Assert.Null(retried.ErrorMessage);
    }

    [Fact]
    public async Task Leaves_a_fetch_already_in_flight_alone()
    {
        // Re-arming this would hand the worker a Pending row it is already downloading, which is
        // how one request becomes two fetches of the same bytes.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var queued = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));
        await SetStatusAsync(queued.Id, DownloadStatus.Downloading);

        // The window between the worker writing the file and marking the row Complete: the bytes
        // exist, and this request does not hold them yet. Reporting their size would be reporting
        // a file the reader cannot ask for.
        await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion, sizeBytes: 4096);

        var again = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        Assert.Equal(nameof(DownloadStatus.Downloading), again.Status);
        Assert.Null(again.SizeBytes);
    }

    // ---- the queue -----------------------------------------------------------------------------

    [Fact]
    public async Task Lists_only_the_callers_own_requests()
    {
        var emma = _host.SeedUser();
        var mercy = _host.SeedUser("mercy");

        var shipId = await WatchAsync(Lexa, emma);
        await WatchAsync(Lexa, mercy);
        await SeedWorksAsync(shipId, 1, 2);

        await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default);
        await _host.NewDownloadsRequest(mercy).RequestDownload(2, new("Epub"), default);

        Assert.Equal(1, Assert.Single(Downloads(await _host.NewDownloadsRequest(emma).GetDownloads(default))).WorkId);
        Assert.Equal(2, Assert.Single(Downloads(await _host.NewDownloadsRequest(mercy).GetDownloads(default))).WorkId);
    }

    [Fact]
    public async Task Lists_the_newest_request_first()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1, 2, 3);

        foreach (var workId in new long[] { 1, 2, 3 })
            await _host.NewDownloadsRequest(emma).RequestDownload(workId, new("Epub"), default);

        // Stamped against the order they were made in, so an ordering that has quietly fallen back
        // on insertion order cannot pass this by accident.
        await StampAsync(1, FirstVersion.AddHours(3));
        await StampAsync(2, FirstVersion.AddHours(1));
        await StampAsync(3, FirstVersion.AddHours(2));

        var listed = Downloads(await _host.NewDownloadsRequest(emma).GetDownloads(default));

        Assert.Equal(new long[] { 1, 3, 2 }, listed.Select(d => d.WorkId));
    }

    [Fact]
    public async Task Breaks_a_tie_between_requests_made_in_the_same_tick()
    {
        // Two format buttons clicked together share a timestamp. Without a total order the two can
        // swap places between calls, which is the same defect the library list's tie-break is for.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1, 2);

        foreach (var workId in new long[] { 1, 2 })
            await _host.NewDownloadsRequest(emma).RequestDownload(workId, new("Epub"), default);

        await StampAsync(1, FirstVersion);
        await StampAsync(2, FirstVersion);

        var listed = Downloads(await _host.NewDownloadsRequest(emma).GetDownloads(default));

        Assert.Equal(new long[] { 2, 1 }, listed.Select(d => d.WorkId));
    }

    [Fact]
    public async Task Keeps_listing_a_request_whose_ship_the_reader_has_stopped_watching()
    {
        // A download is something this reader asked for, not a view of the library. Hiding it with
        // the ship would strand the file with no request left able to name it.
        var emma = _host.SeedUser();
        var shipId = await WatchAsync(Lexa, emma);
        await SeedWorksAsync(shipId, 1);

        await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default);
        await _host.Ships(emma).UnwatchShip(shipId, default);

        var listed = Assert.Single(Downloads(await _host.NewDownloadsRequest(emma).GetDownloads(default)));
        Assert.Equal(1, listed.WorkId);
    }

    // ---- dropping a request --------------------------------------------------------------------

    [Fact]
    public async Task Drops_the_callers_own_request()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var queued = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));
        var dropped = await _host.NewDownloadsRequest(emma).DeleteDownload(queued.Id, default);

        Assert.IsType<NoContentResult>(dropped);
        Assert.Empty(Downloads(await _host.NewDownloadsRequest(emma).GetDownloads(default)));
    }

    [Fact]
    public async Task Refuses_to_drop_another_readers_request()
    {
        var emma = _host.SeedUser();
        var mercy = _host.SeedUser("mercy");

        var shipId = await WatchAsync(Lexa, emma);
        await WatchAsync(Lexa, mercy);
        await SeedWorksAsync(shipId, 1);

        var hers = Download(await _host.NewDownloadsRequest(mercy).RequestDownload(1, new("Epub"), default));
        var refused = await _host.NewDownloadsRequest(emma).DeleteDownload(hers.Id, default);

        Assert.IsType<NotFoundResult>(refused);
        Assert.Single(Downloads(await _host.NewDownloadsRequest(mercy).GetDownloads(default)));
    }

    [Fact]
    public async Task Refuses_a_request_that_does_not_exist()
    {
        var emma = _host.SeedUser();

        Assert.IsType<NotFoundResult>(await _host.NewDownloadsRequest(emma).DeleteDownload(404, default));
    }

    [Fact]
    public async Task Keeps_the_shared_file_when_one_readers_request_is_dropped()
    {
        var emma = _host.SeedUser();
        var mercy = _host.SeedUser("mercy");

        var shipId = await WatchAsync(Lexa, emma);
        await WatchAsync(Lexa, mercy);
        await SeedWorksAsync(shipId, 1);

        var fileId = await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion, sizeBytes: 4096);

        var hers = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));
        await _host.NewDownloadsRequest(mercy).RequestDownload(1, new("Epub"), default);

        await _host.NewDownloadsRequest(emma).DeleteDownload(hers.Id, default);

        await using var db = _host.NewContext();
        Assert.True(await db.WorkDownloadFiles.AnyAsync(f => f.Id == fileId));
        Assert.Equal(fileId, await db.Downloads.Select(d => d.WorkDownloadFileId).SingleAsync());
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Follows a tag through the real endpoint, returning the ship it resolved to.</summary>
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

    /// <summary>Bytes on disk, as the download worker will leave them.</summary>
    private async Task<int> SeedFileAsync(long workId, Ao3DownloadFormat format, DateTime version, long sizeBytes)
    {
        await using var db = _host.NewContext();

        var file = new WorkDownloadFile
        {
            WorkId = workId,
            Format = format,
            WorkUpdatedAt = version,
            RelativePath = $"downloads/{workId}/{format}.bin",
            SizeBytes = sizeBytes,
            FetchedAt = version,
        };

        db.WorkDownloadFiles.Add(file);
        await db.SaveChangesAsync();

        return file.Id;
    }

    /// <summary>The author updates the work, which is what makes a stored copy a stale one.</summary>
    private async Task MoveWorkOnAsync(long workId, DateTime updatedAt)
    {
        await using var db = _host.NewContext();

        var work = await db.Works.FirstAsync(w => w.Id == workId);
        work.UpdatedAt = updatedAt;

        await db.SaveChangesAsync();
    }

    private async Task FailAsync(int downloadId, string message)
    {
        await using var db = _host.NewContext();

        var download = await db.Downloads.FirstAsync(d => d.Id == downloadId);
        download.Status = DownloadStatus.Failed;
        download.ErrorMessage = message;
        download.CompletedAt = FirstVersion;

        await db.SaveChangesAsync();
    }

    /// <summary>Backdates when a work's request was made, so an ordering test can construct one.</summary>
    private async Task StampAsync(long workId, DateTime requestedAt)
    {
        await using var db = _host.NewContext();

        var download = await db.Downloads.FirstAsync(d => d.WorkId == workId);
        download.RequestedAt = requestedAt;

        await db.SaveChangesAsync();
    }

    private async Task SetStatusAsync(int downloadId, DownloadStatus status)
    {
        await using var db = _host.NewContext();

        var download = await db.Downloads.FirstAsync(d => d.Id == downloadId);
        download.Status = status;

        await db.SaveChangesAsync();
    }

    private async Task<List<Download>> DownloadRowsAsync()
    {
        await using var db = _host.NewContext();
        return await db.Downloads.ToListAsync();
    }

    private static DownloadDto Download(ActionResult<DownloadDto> result) =>
        Assert.IsType<DownloadDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static IReadOnlyList<DownloadDto> Downloads(ActionResult<IReadOnlyList<DownloadDto>> result) =>
        Assert.IsType<List<DownloadDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static bool Rejected<T>(ActionResult<T> result, string field)
    {
        var problem = Assert.IsType<ValidationProblemDetails>(
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        return problem.Errors.ContainsKey(field);
    }
}
