using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Downloads;
using Microsoft.AspNetCore.Http;
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

    // ---- serving the file ----------------------------------------------------------------------

    [Fact]
    public async Task Streams_the_completed_file_to_the_reader_who_asked_for_it()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var bytes = "not really an epub"u8.ToArray();
        var path = await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, bytes);
        var complete = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));
        Assert.Equal(nameof(DownloadStatus.Complete), complete.Status);

        var controller = _host.NewDownloadsRequest(emma);
        var served = Assert.IsType<PhysicalFileResult>(await controller.GetDownloadFile(complete.Id, default));

        // The path, not the bytes: a PhysicalFileResult is the thing that streams rather than
        // buffers, and asserting on the content would mean having read the file into memory here.
        Assert.Equal(path, served.FileName);
        Assert.Equal("application/epub+zip", served.ContentType.ToString());
        Assert.Equal("Work 1.epub", served.FileDownloadName);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(served.FileName));

        Assert.Equal("private, no-store", controller.Response.Headers.CacheControl);
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions);
    }

    [Fact]
    public async Task Refuses_the_file_behind_another_readers_request()
    {
        var emma = _host.SeedUser();
        var mercy = _host.SeedUser("mercy");

        var shipId = await WatchAsync(Lexa, emma);
        await WatchAsync(Lexa, mercy);
        await SeedWorksAsync(shipId, 1);
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, [1, 2, 3]);

        var hers = Download(await _host.NewDownloadsRequest(mercy).RequestDownload(1, new("Epub"), default));

        // A bare 404, the same answer a request id that never existed gets, so that no id can be
        // probed for whose it is — even though this reader can see the work itself.
        Assert.IsType<NotFoundResult>(await _host.NewDownloadsRequest(emma).GetDownloadFile(hers.Id, default));
    }

    [Fact]
    public async Task Refuses_the_file_of_a_request_that_does_not_exist()
    {
        var emma = _host.SeedUser();

        Assert.IsType<NotFoundResult>(await _host.NewDownloadsRequest(emma).GetDownloadFile(404, default));
    }

    [Fact]
    public async Task Will_not_serve_a_request_that_is_still_queued()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var queued = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));
        Assert.Equal(nameof(DownloadStatus.Pending), queued.Status);

        var refused = Assert.IsType<ObjectResult>(
            await _host.NewDownloadsRequest(emma).GetDownloadFile(queued.Id, default));

        // The caller's own row, so this one says what is wrong with it rather than hiding behind
        // the 404 someone else's request gets.
        Assert.Equal(StatusCodes.Status409Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task Will_not_serve_a_request_whose_fetch_failed()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var queued = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));
        await FailAsync(queued.Id, "AO3 answered 404 for this work's page.");

        var refused = Assert.IsType<ObjectResult>(
            await _host.NewDownloadsRequest(emma).GetDownloadFile(queued.Id, default));

        Assert.Equal(StatusCodes.Status409Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task Will_not_serve_a_request_that_is_queued_while_still_naming_a_copy()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, [1, 2, 3]);

        var complete = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        // Constructed rather than reached: `Arm` nulls the file reference when it re-queues a
        // request, so nothing in the app writes this state today. T59 is the open question of
        // whether it should keep it — a reader whose refetch fails currently loses the copy still
        // on disk — and if the answer is yes, a queued request starts naming the previous version's
        // bytes. This endpoint must not hand those over as the answer to the request being retried.
        await RequeueKeepingFileAsync(complete.Id);

        var refused = Assert.IsType<ObjectResult>(
            await _host.NewDownloadsRequest(emma).GetDownloadFile(complete.Id, default));

        Assert.Equal(StatusCodes.Status409Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task Reports_a_stored_file_that_has_left_the_disk_rather_than_throwing()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var path = await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, [1, 2, 3]);
        var complete = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        // The row still says Complete. Something outside this app — a pruned volume, an operator
        // clearing space — took the bytes, which is the case a path opened blind would 500 on.
        File.Delete(path);

        var gone = Assert.IsType<ObjectResult>(
            await _host.NewDownloadsRequest(emma).GetDownloadFile(complete.Id, default));

        Assert.Equal(StatusCodes.Status410Gone, gone.StatusCode);
    }

    [Fact]
    public async Task Refuses_to_serve_a_stored_path_that_points_outside_the_data_directory()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, [1, 2, 3]);

        var complete = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        // Nothing in the app writes a path like this — DownloadPaths.Relative builds one out of a
        // work id and an enum. It is what a restored database, a hand-edited row or a future writer
        // with a different idea of that column could put there, and this endpoint is the one place
        // where a value out of the database becomes a file handed to whoever asked for it.
        await RepointAsync(1, "../../../../../../etc/passwd");

        var refused = Assert.IsType<ObjectResult>(
            await _host.NewDownloadsRequest(emma).GetDownloadFile(complete.Id, default));

        Assert.Equal(StatusCodes.Status500InternalServerError, refused.StatusCode);
    }

    [Theory]
    [InlineData(Ao3DownloadFormat.Epub, "application/epub+zip", "epub")]
    [InlineData(Ao3DownloadFormat.Mobi, "application/x-mobipocket-ebook", "mobi")]
    [InlineData(Ao3DownloadFormat.Pdf, "application/pdf", "pdf")]
    [InlineData(Ao3DownloadFormat.Azw3, "application/vnd.amazon.ebook", "azw3")]
    // AO3's HTML download is a whole document of author-supplied markup. Named as what it is and
    // served from this app's own origin, it would be one slipped Content-Disposition away from
    // running as script in a logged-in session, so it is bytes to save instead.
    [InlineData(Ao3DownloadFormat.Html, "application/octet-stream", "html")]
    public async Task Names_the_type_of_every_format_it_serves(
        Ao3DownloadFormat format, string contentType, string extension)
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await SeedFileOnDiskAsync(1, format, FirstVersion, [1, 2, 3]);

        var complete = Download(
            await _host.NewDownloadsRequest(emma).RequestDownload(1, new(format.ToString()), default));

        var served = Assert.IsType<PhysicalFileResult>(
            await _host.NewDownloadsRequest(emma).GetDownloadFile(complete.Id, default));

        Assert.Equal(contentType, served.ContentType.ToString());
        Assert.Equal($"Work 1.{extension}", served.FileDownloadName);
    }

    [Theory]
    // Path separators and traversal, on both filesystems this app runs on.
    [InlineData("../../etc/passwd", "etc passwd.epub")]
    [InlineData("C:\\Windows\\System32", "C Windows System32.epub")]
    // A quote closes the filename token in a Content-Disposition header, and a newline ends the
    // header itself.
    [InlineData("A \"quoted\" work", "A quoted work.epub")]
    [InlineData("Split\r\nHeader: injected", "Split Header injected.epub")]
    [InlineData("Null\u0000byte", "Null byte.epub")]
    // Trailing dots and spaces are dropped by Windows, so a name ending in one is not the name the
    // reader sees; a leading dot hides the file on Unix.
    [InlineData("...hidden...", "hidden.epub")]
    [InlineData("  padded  ", "padded.epub")]
    // Not everything that is not a letter is dangerous, and a title stripped to initials would be
    // worse than one carrying its own punctuation.
    [InlineData("Don't Look Back (Part 1) [Remix] & Co.!", "Don't Look Back (Part 1) [Remix] & Co.epub")]
    // Titles are not all English, and RFC 5987 is what the header encoding exists for.
    [InlineData("Ярость и надежда", "Ярость и надежда.epub")]
    // Letters outside the basic plane are two UTF-16 units each. Judged one unit at a time, both
    // halves fail every test a letter passes and the whole title would come out as the work id.
    [InlineData("\U00010330\U00010339\U0001033E", "\U00010330\U00010339\U0001033E.epub")]
    public async Task Names_the_file_after_the_work_with_nothing_AO3_wrote_left_in_it(
        string title, string expected)
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await RetitleAsync(1, title);
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, [1, 2, 3]);

        var complete = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        var served = Assert.IsType<PhysicalFileResult>(
            await _host.NewDownloadsRequest(emma).GetDownloadFile(complete.Id, default));

        Assert.Equal(expected, served.FileDownloadName);
    }

    [Fact]
    public async Task Falls_back_to_the_works_id_when_a_title_survives_as_nothing()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await RetitleAsync(1, "«/\\»");
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, [1, 2, 3]);

        var complete = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        var served = Assert.IsType<PhysicalFileResult>(
            await _host.NewDownloadsRequest(emma).GetDownloadFile(complete.Id, default));

        // A work titled entirely in punctuation is a work, not an error — so it gets a name it can
        // be saved under rather than a refusal.
        Assert.Equal("work-1.epub", served.FileDownloadName);
    }

    [Fact]
    public async Task Cuts_a_long_title_between_letters_rather_than_through_one()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        // One ASCII letter then Gothic ones, which puts the 120-unit cut exactly halfway through a
        // letter: taken at face value it would leave a lone surrogate at the end of the name, which
        // is not a character any filesystem or header encoder can do anything with.
        await RetitleAsync(1, "x" + string.Concat(Enumerable.Repeat("\U00010330", 200)));
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, [1, 2, 3]);

        var complete = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        var served = Assert.IsType<PhysicalFileResult>(
            await _host.NewDownloadsRequest(emma).GetDownloadFile(complete.Id, default));

        // 1 + 59 * 2 = 119 units of name, one short of the cap, because taking the 120th would
        // have taken half of the sixtieth letter.
        Assert.Equal(
            "x" + string.Concat(Enumerable.Repeat("\U00010330", 59)) + ".epub",
            served.FileDownloadName);
    }

    [Fact]
    public async Task Cuts_a_title_too_long_to_be_a_filename_down_to_one()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await RetitleAsync(1, new string('a', 400));
        await SeedFileOnDiskAsync(1, Ao3DownloadFormat.Epub, FirstVersion, [1, 2, 3]);

        var complete = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        var served = Assert.IsType<PhysicalFileResult>(
            await _host.NewDownloadsRequest(emma).GetDownloadFile(complete.Id, default));

        // Under the 255 bytes most filesystems stop at, with room left for the extension — and the
        // extension is still there, which is what the reader's e-reader goes by.
        Assert.Equal($"{new string('a', 120)}.epub", served.FileDownloadName);
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

    /// <summary>
    /// Bytes actually on disk, at the path the fetcher would have written them to, with the row
    /// that names them. Returns the absolute path, so a test can take the file away again.
    /// </summary>
    private async Task<string> SeedFileOnDiskAsync(
        long workId, Ao3DownloadFormat format, DateTime version, byte[] bytes)
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

    /// <summary>
    /// Puts a request back in the queue without dropping the file it names — the state T59 would
    /// introduce, and one no path in the app produces today.
    /// </summary>
    private async Task RequeueKeepingFileAsync(int downloadId)
    {
        await using var db = _host.NewContext();

        var download = await db.Downloads.FirstAsync(d => d.Id == downloadId);
        download.Status = DownloadStatus.Pending;
        download.CompletedAt = null;

        await db.SaveChangesAsync();
    }

    /// <summary>Points a stored file's row at somewhere else, which nothing in the app does.</summary>
    private async Task RepointAsync(long workId, string relativePath)
    {
        await using var db = _host.NewContext();

        var file = await db.WorkDownloadFiles.FirstAsync(f => f.WorkId == workId);
        file.RelativePath = relativePath;

        await db.SaveChangesAsync();
    }

    /// <summary>Gives a work the title AO3 carried, which is text nobody here wrote.</summary>
    private async Task RetitleAsync(long workId, string title)
    {
        await using var db = _host.NewContext();

        var work = await db.Works.FirstAsync(w => w.Id == workId);
        work.Title = title;

        await db.SaveChangesAsync();
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
