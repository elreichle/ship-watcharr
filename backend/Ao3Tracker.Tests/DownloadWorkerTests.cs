using System.Net;
using System.Security.Cryptography;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Downloads;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ao3Tracker.Tests;

/// <summary>
/// What draining the download queue does — two rate-gated requests per file, and a row saying how
/// it went.
///
/// The shape everything here turns on is that AO3's download addresses cannot be constructed. The
/// slug is a truncation of the title whose rule the archive states nowhere and <c>updated_at</c> is
/// AO3's own stamp, so the worker reads the work's page for the link and only then fetches it.
/// That is what gives a download two halves that fail independently, and why "which half failed" is
/// something a reader can see.
/// </summary>
public class DownloadWorkerTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";

    /// <summary>Fixed, so "the work has since changed" is only ever something a test does on purpose.</summary>
    private static readonly DateTime FirstVersion = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The address the captured work page offers, in the shape nothing in the library could have
    /// produced: a slug that is not the title and a timestamp that is not <c>Work.UpdatedAt</c>.
    /// </summary>
    private const string EpubUrl =
        "https://ao3.test/downloads/1/we_chose_to_wait.epub?updated_at=1767140797";

    /// <summary>
    /// The address the <em>previous</em> version's page carried. Same work, same slug, a different
    /// stamp — because the stamp is AO3's own and moves with the revision, which is exactly why a
    /// page read before the revision offers a file this library must not store as the current one.
    /// </summary>
    private const string PreviousEpubUrl =
        "https://ao3.test/downloads/1/we_chose_to_wait.epub?updated_at=1766000000";

    private static readonly byte[] FileBody = "EPUB bytes"u8.ToArray();

    private LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- the ordinary path ---------------------------------------------------------------------

    [Fact]
    public async Task Fetches_a_queued_download_and_records_where_it_put_it()
    {
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);

        await DrainAsync();

        var download = await DownloadAsync();
        Assert.Equal(DownloadStatus.Complete, download.Status);
        Assert.Null(download.ErrorMessage);
        Assert.NotNull(download.CompletedAt);

        var file = await FileAsync();
        Assert.Equal(FileBody.Length, file.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(FileBody)), file.Sha256);
        Assert.Equal(download.WorkDownloadFileId, file.Id);

        // Keyed to the version it is a copy of, which is what lets the next request for an unchanged
        // work be answered without asking AO3 for anything.
        Assert.Equal(FirstVersion, file.WorkUpdatedAt);
    }

    [Fact]
    public async Task Stores_the_bytes_under_a_path_that_survives_the_volume_moving()
    {
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);

        await DrainAsync();

        var file = await FileAsync();

        // Relative, never absolute: the data directory is a Docker volume, and a database carrying
        // absolute paths names files under a directory that need not exist on the next machine.
        Assert.False(Path.IsPathRooted(file.RelativePath));

        var onDisk = Path.Combine(_host.DataDirectory, file.RelativePath);
        Assert.True(File.Exists(onDisk));
        Assert.Equal(FileBody, await File.ReadAllBytesAsync(onDisk));
    }

    [Fact]
    public async Task Reads_the_address_off_the_work_page_rather_than_building_one()
    {
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);

        await DrainAsync();

        // Two requests, in this order, and the second is the href the first carried. A worker that
        // computed "/downloads/1/1.epub" from the work id would pass every other assertion here.
        Assert.Equal("https://ao3.test/works/1?view_adult=true", Assert.Single(_host.Http.Requested));
        Assert.Equal(EpubUrl, Assert.Single(_host.Http.FilesRequested));
    }

    [Fact]
    public async Task Fetches_one_copy_for_two_readers_who_want_the_same_thing()
    {
        // The whole reason the file is keyed by (work, format, version) rather than living on the
        // per-reader row: identical bytes must cost AO3 one page load, not one per reader.
        var emma = await ReaderWithAWorkAsync();
        var mercy = _host.SeedUser("mercy");
        await WatchAsync(Lexa, mercy);

        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);
        await QueueAsync(mercy);

        await DrainAsync();

        Assert.Single(_host.Http.FilesRequested);

        var downloads = await DownloadsAsync();
        Assert.Equal(2, downloads.Count);
        Assert.All(downloads, d => Assert.Equal(DownloadStatus.Complete, d.Status));
        Assert.Single(downloads.Select(d => d.WorkDownloadFileId).Distinct());
    }

    [Fact]
    public async Task Asks_AO3_for_nothing_when_the_bytes_turned_up_while_the_request_waited()
    {
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);

        // Another reader's fetch landed between the request being made and this drain reaching it.
        var fileId = await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion);

        await DrainAsync();

        Assert.Empty(_host.Http.Requested);
        Assert.Empty(_host.Http.FilesRequested);

        var download = await DownloadAsync();
        Assert.Equal(DownloadStatus.Complete, download.Status);
        Assert.Equal(fileId, download.WorkDownloadFileId);
    }

    // ---- the two halves that can fail ----------------------------------------------------------

    [Fact]
    public async Task Fails_a_request_whose_work_page_AO3_will_not_serve()
    {
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = url => new ScrapeHttpResponse("Not found", HttpStatusCode.NotFound, false, url);
        await QueueAsync(emma);

        await DrainAsync();

        var download = await DownloadAsync();
        Assert.Equal(DownloadStatus.Failed, download.Status);

        // The half that failed, said out loud: without the page there is no address, so nothing was
        // fetched — which is a different thing from the file itself being refused.
        Assert.Contains("404", download.ErrorMessage);
        Assert.Contains("page", download.ErrorMessage);
        Assert.Empty(_host.Http.FilesRequested);
    }

    [Fact]
    public async Task Fails_a_request_for_a_format_the_page_does_not_offer()
    {
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage("https://ao3.test/downloads/1/we_chose_to_wait.pdf?updated_at=1");
        await QueueAsync(emma);

        await DrainAsync();

        var download = await DownloadAsync();
        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.Contains("Epub", download.ErrorMessage);
        Assert.Empty(_host.Http.FilesRequested);
    }

    [Fact]
    public async Task Fails_a_request_whose_file_AO3_will_not_serve_and_stores_nothing()
    {
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        _host.Http.RespondsToDownload = _ => (HttpStatusCode.NotFound, "Not found"u8.ToArray());
        await QueueAsync(emma);

        await DrainAsync();

        var download = await DownloadAsync();
        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.Contains("404", download.ErrorMessage);

        // No row claiming bytes that are not there, and no half-written file left where a later
        // request would find it and call it a copy of the work.
        Assert.Empty(await FilesAsync());
        Assert.Empty(FilesUnder(DownloadPaths.Root));
    }

    [Fact]
    public async Task Refuses_a_200_that_is_not_the_file_it_asked_for()
    {
        // The transport follows redirects, so AO3 declining a download — a restricted work whose
        // session died in the seconds between reading the page and fetching the link — answers by
        // redirecting to the login form. That is a 200 carrying HTML, and stored it becomes a login
        // page on disk under a name saying it is an EPUB, with a row and a checksum agreeing.
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        _host.Http.DownloadsLandOn = "https://ao3.test/users/login";
        await QueueAsync(emma);

        await DrainAsync();

        var download = await DownloadAsync();
        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.Contains("login", download.ErrorMessage);

        Assert.Empty(await FilesAsync());
        Assert.Empty(FilesUnder(DownloadPaths.Root));
    }

    [Fact]
    public async Task Accepts_a_download_that_moved_but_still_serves_the_file()
    {
        // Judged on the extension rather than the whole address, so a redirect that still serves
        // the file is not refused merely for having moved it.
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        _host.Http.DownloadsLandOn = "https://mirror.ao3.test/files/we_chose_to_wait.epub";
        await QueueAsync(emma);

        await DrainAsync();

        Assert.Equal(DownloadStatus.Complete, (await DownloadAsync()).Status);
    }

    // ---- an address that has gone stale ----------------------------------------------------------
    //
    // The link a work page offers carries AO3's own `updated_at` stamp, so an address read off a
    // page the response cache is still holding can name a version the archive has moved past. What
    // AO3 does with such a link is not knowable from a capture — it may refuse it, or it may
    // redirect to the current file — so both answers are settled here rather than assumed. See the
    // 2026-08-25 T13 entry in .devloop/DECISIONS.md.
    //
    // The refusing answer needs nothing of its own: a refused address is a refused file, which
    // `Fails_a_request_whose_file_AO3_will_not_serve_and_stores_nothing` already pins, and
    // `Does_not_ask_again_for_something_it_has_already_failed` pins that it is not then requested
    // for ever. Only the redirecting answer says something new.

    [Fact]
    public async Task Keys_a_redirected_download_to_the_version_the_library_holds()
    {
        // AO3 answering a stale address by serving the current file is the benign half of the
        // question — but only if the row records the version this library knows about rather than
        // the stamp in whichever address the request ended at. A row keyed off the address would
        // claim to be a copy of a version no scrape has ever seen, and the next request for the
        // work as the library holds it would fetch the same bytes all over again.
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        _host.Http.DownloadsLandOn =
            "https://ao3.test/downloads/1/we_chose_to_wait.epub?updated_at=1799999999";
        await QueueAsync(emma);

        await DrainAsync();

        Assert.Equal(DownloadStatus.Complete, (await DownloadAsync()).Status);

        var file = await FileAsync();
        Assert.Equal(FirstVersion, file.WorkUpdatedAt);
        Assert.Equal(DownloadPaths.Relative(1, Ao3DownloadFormat.Epub, FirstVersion), file.RelativePath);
    }

    [Fact]
    public async Task Never_asks_for_a_download_anonymously()
    {
        // The other question a capture cannot answer is whether these addresses work logged out —
        // the page they were captured from was fetched with a session. This app never finds out,
        // and that is the settlement: both halves go through the authenticated transport, so a
        // deployment's downloads are as identified as its scrapes. `GetLoggedOutAsync` exists only
        // for the login page itself, and a download reaching for it would be this instance asking
        // AO3 for a work as nobody in particular.
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);

        // Authenticated up front, so that the only logged-out request this drain could make is one
        // it made for a download. The login page is the one page this app is right to fetch that
        // way, and leaving it to happen inside the drain would hide the thing being asserted.
        await _host.EnsureAo3SessionAsync();
        _host.Http.LoginPagesRequested.Clear();

        await DrainAsync();

        Assert.Single(_host.Http.Requested);
        Assert.Single(_host.Http.FilesRequested);
        Assert.Empty(_host.Http.LoginPagesRequested);
    }

    [Fact]
    public async Task Re_queues_a_request_the_archive_could_not_be_reached_for()
    {
        // A DNS failure, a reset socket or a connect timeout is not AO3's answer about this work —
        // it is no answer at all, and the transport retries responses only, so this has had exactly
        // one attempt. Failing it settles the request on one blip and makes the reader notice and
        // ask again; the "never a retry loop" rule is about a work AO3 has taken down.
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);

        // Logged in first, so that what fails below is the fetch rather than the login. A login that
        // cannot be performed holds the queue instead, which is the test above this one.
        await _host.EnsureAo3SessionAsync();
        _host.Http.Fails = new HttpRequestException("Connection refused");

        await DrainAsync();

        var download = await DownloadAsync();
        Assert.Equal(DownloadStatus.Pending, download.Status);

        // And nothing left in flight: a request stuck at Downloading is one the controller
        // deliberately will not re-arm, so nothing but a restart could ever free it.
        Assert.Null(download.ErrorMessage);
        Assert.Empty(FilesUnder(DownloadPaths.Root));

        // The next poll finds the archive back and finishes it, with no second request from anyone.
        // Three consecutive failures would have settled it; this is the first.
        _host.Http.Fails = null;
        await DrainAsync();

        Assert.Equal(DownloadStatus.Complete, (await DownloadAsync()).Status);
    }

    [Fact]
    public async Task Stops_re_queueing_once_the_archive_is_plainly_down()
    {
        // What bounds the rule above. Every transport failure is recorded against the drain's
        // budget before it escapes, so the breaker opens on the third and the rest of the queue is
        // held rather than attempted — a queue of two hundred does not become two hundred requests
        // at an archive that is not answering.
        var emma = await ReaderWithAWorkAsync(works: 5);
        await _host.EnsureAo3SessionAsync();

        for (long workId = 1; workId <= 5; workId++) await QueueAsync(emma, workId);

        _host.Http.Fails = new HttpRequestException("Connection refused");

        await DrainAsync();

        Assert.Equal(3, _host.Http.Requested.Count);
        Assert.All(await DownloadsAsync(), d => Assert.Equal(DownloadStatus.Pending, d.Status));
    }

    [Fact]
    public async Task Records_a_fetch_that_kept_throwing_before_it_could_claim_its_row()
    {
        // MarkFailedAsync used to record only rows it found Downloading, which covers "deleted" and
        // "the fetcher already wrote it" — and also covered a fetcher that threw before claiming
        // anything. Such a row stayed Pending, so every poll re-selected it, threw again and
        // recorded nothing, while the reader went on being told it was queued.
        var thrower = new ThrowingFetcher(new InvalidOperationException("The database is locked"));

        _host.Dispose();
        _host = new LibraryTestHost(services => services.AddScoped<IDownloadFetcher>(_ => thrower));

        var emma = await ReaderWithAWorkAsync();
        var id = await QueueAsync(emma);

        var worker = _host.NewDownloadWorker();
        await worker.DrainQueueAsync(default);

        // Not on the first: a store that was briefly locked is exactly the kind of failure the next
        // poll gets past, and settling the request on one throw would cost the reader their request
        // for it.
        Assert.Equal(DownloadStatus.Pending, (await DownloadAsync()).Status);

        await worker.DrainQueueAsync(default);
        await worker.DrainQueueAsync(default);

        var download = await DownloadAsync();
        Assert.Equal(id, download.Id);
        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.Contains("The database is locked", download.ErrorMessage);
    }

    [Fact]
    public async Task Gives_up_on_a_request_no_poll_has_ever_reached_the_archive_for()
    {
        // The bound on re-queueing. The drain's circuit breaker only ends one poll and the next
        // rebuilds it, so with nothing counting across polls an instance whose archive does not
        // resolve spends three requests a minute on it for ever — thousands a day at an endpoint
        // that is not answering — while the reader is shown a request that says only "queued".
        var emma = await ReaderWithAWorkAsync();
        await QueueAsync(emma);
        await _host.EnsureAo3SessionAsync();

        _host.Http.Fails = new HttpRequestException("Name or service not known");

        var worker = _host.NewDownloadWorker();

        await worker.DrainQueueAsync(default);
        await worker.DrainQueueAsync(default);
        Assert.Equal(DownloadStatus.Pending, (await DownloadAsync()).Status);

        await worker.DrainQueueAsync(default);

        var download = await DownloadAsync();
        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.Contains("Name or service not known", download.ErrorMessage);

        // And nothing asks again: three requests, made across three polls, and then a row the
        // reader can see and act on.
        Assert.Equal(3, _host.Http.Requested.Count);

        await worker.DrainQueueAsync(default);
        Assert.Equal(3, _host.Http.Requested.Count);
    }

    [Fact]
    public async Task Forgets_what_a_request_cost_once_it_leaves_the_queue()
    {
        // The count is per request and consecutive: a fetch that failed twice and then worked must
        // not leave the next failure of the same row one throw from being given up on.
        var emma = await ReaderWithAWorkAsync();
        await QueueAsync(emma);
        await _host.EnsureAo3SessionAsync();

        _host.Http.Fails = new HttpRequestException("Connection refused");
        var worker = _host.NewDownloadWorker();

        await worker.DrainQueueAsync(default);
        await worker.DrainQueueAsync(default);

        _host.Http.Fails = null;
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await worker.DrainQueueAsync(default);

        Assert.Equal(DownloadStatus.Complete, (await DownloadAsync()).Status);

        // Asked for again, and failing again: the reader gets the same three attempts they would
        // have got the first time.
        await MoveWorkOnAsync(1, FirstVersion.AddDays(1));
        await QueueAsync(emma);
        _host.Http.Fails = new HttpRequestException("Connection refused");

        await worker.DrainQueueAsync(default);

        Assert.Equal(DownloadStatus.Pending, (await DownloadAsync()).Status);
    }

    [Fact]
    public async Task Fails_a_request_for_a_file_larger_than_this_instance_will_store()
    {
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        _host.Http.DownloadsExceedTheSizeLimit = true;
        await QueueAsync(emma);

        await DrainAsync();

        var download = await DownloadAsync();
        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.Contains("larger than", download.ErrorMessage);

        // What arrived before the copy was abandoned is not a copy of anything, so nothing may name
        // it and nothing may keep it.
        Assert.Empty(await FilesAsync());
        Assert.Empty(FilesUnder(DownloadPaths.Root));
    }

    [Fact]
    public async Task Does_not_count_a_file_this_instance_refused_against_the_breaker()
    {
        // The breaker is about the archive's health, and a ceiling chosen at this end is not
        // evidence about it: AO3 served every one of these perfectly. Counted as failures, three of
        // them in a drain hold everything behind them on the grounds that the archive is down.
        //
        // The pages come from the response cache, which is what a queue of formats of one work — or
        // a second poll inside the cache window — actually looks like. A freshly read page records
        // a success between each file and resets the streak, which is what hides the arithmetic.
        var emma = await ReaderWithAWorkAsync(works: 4);
        _host.Http.Responds = _ => WorkPage(EpubUrl) with { FromCache = true };
        _host.Http.DownloadsExceedTheSizeLimit = true;

        for (long workId = 1; workId <= 4; workId++) await QueueAsync(emma, workId);

        await DrainAsync();

        Assert.Equal(4, _host.Http.FilesRequested.Count);
        Assert.All(await DownloadsAsync(), d => Assert.Equal(DownloadStatus.Failed, d.Status));
    }

    [Fact]
    public async Task Fetches_again_when_the_stored_copy_has_left_the_disk()
    {
        // A row is not a file. One left naming bytes that a remounted volume or a hand-cleaned disk
        // took away would answer every future request for this work and format with Complete and a
        // path to nothing — and the reader could not ask their way out of it, because the same row
        // is what says there is nothing to fetch.
        var emma = await ReaderWithAWorkAsync();
        var fileId = await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion);

        File.Delete(DownloadPaths.Absolute(
            _host.DataDirectory, DownloadPaths.Relative(1, Ao3DownloadFormat.Epub, FirstVersion)));

        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);

        await DrainAsync();

        Assert.Equal(DownloadStatus.Complete, (await DownloadAsync()).Status);
        Assert.Single(_host.Http.FilesRequested);

        // The row that was there is the row that is repaired — the path is derived from
        // (work, format, version), so the fetch wrote to exactly where it already pointed — and it
        // describes the bytes now on disk rather than the ones that went missing.
        var file = await FileAsync();
        Assert.Equal(fileId, file.Id);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(FileBody)), file.Sha256);
        Assert.Equal(FileBody.Length, file.SizeBytes);
        Assert.Single(FilesUnder(DownloadPaths.Root));
    }

    [Fact]
    public async Task Leaves_no_file_behind_when_nothing_can_be_written_to_name_it()
    {
        // The bytes are moved into place before the row is written, so a save the database refuses
        // leaves up to the whole size ceiling on disk that nothing accounts for: no row names it,
        // the partials sweep does not look there, and only a fetch of this same version of this
        // same work would ever overwrite it.
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);

        // The work leaves the library while its file is on the wire, taking the request with it.
        _host.Http.RespondsToDownload = _ =>
        {
            DeleteWork(1);
            return (HttpStatusCode.OK, FileBody);
        };

        await DrainAsync();

        Assert.Empty(await FilesAsync());
        Assert.Empty(FilesUnder(DownloadPaths.Root));
    }

    [Fact]
    public async Task Keeps_the_old_copy_when_a_new_version_is_fetched()
    {
        // Two versions of one work are two files, because a stored copy is only a copy of the
        // version it was taken from. A path that did not carry the version would have the second
        // fetch overwrite the first — and every request still pointing at the old row would then be
        // served the new work's bytes.
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);
        await DrainAsync();

        await MoveWorkOnAsync(1, FirstVersion.AddDays(1));
        await QueueAsync(emma);
        await DrainAsync();

        var files = await FilesAsync();
        Assert.Equal(2, files.Count);
        Assert.Equal(2, files.Select(f => f.RelativePath).Distinct().Count());
        Assert.Equal(2, FilesUnder(DownloadPaths.Root).Length);
    }

    [Fact]
    public async Task Leaves_the_reader_holding_their_old_copy_when_the_refetch_fails()
    {
        // The trade T59 settled: a re-fetch that fails must not be what costs a reader the file
        // they already had. The fetcher does that by leaving both file references as it found
        // them — the request reports nothing, and still holds what the controller handed it.
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);
        await DrainAsync();

        var held = (await DownloadAsync()).WorkDownloadFileId;
        Assert.NotNull(held);

        await MoveWorkOnAsync(1, FirstVersion.AddDays(1));
        await QueueAsync(emma);

        _host.Http.Responds = url => new ScrapeHttpResponse("Not found", HttpStatusCode.NotFound, false, url);
        await DrainAsync();

        var download = await DownloadAsync();

        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.Null(download.WorkDownloadFileId);
        Assert.Equal(held, download.PreviousWorkDownloadFileId);
    }

    [Fact]
    public async Task Lets_go_of_the_old_copy_once_the_new_version_is_fetched()
    {
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);
        await DrainAsync();

        var first = (await DownloadAsync()).WorkDownloadFileId;

        await MoveWorkOnAsync(1, FirstVersion.AddDays(1));
        await QueueAsync(emma);
        await DrainAsync();

        var download = await DownloadAsync();

        // The replacement is on disk, which is the whole of what the held copy was waiting on.
        Assert.Equal(DownloadStatus.Complete, download.Status);
        Assert.NotEqual(first, download.WorkDownloadFileId);
        Assert.Null(download.PreviousWorkDownloadFileId);
    }

    [Fact]
    public async Task Leaves_a_request_queued_when_the_budget_runs_out_between_its_two_halves()
    {
        // The address is read and then the file is fetched, so a drain can legitimately run out
        // between them. Released rather than failed: nothing is wrong with the request, and the
        // page it needs is still in the response cache when the next poll picks it up.
        _host.Dispose();
        _host = new LibraryTestHost(services =>
            services.Configure<Ao3HttpClientOptions>(o => o.MaxRequestsPerRun = 1));

        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma);

        await DrainAsync();

        Assert.Single(_host.Http.Requested);
        Assert.Empty(_host.Http.FilesRequested);
        Assert.Equal(DownloadStatus.Pending, (await DownloadAsync()).Status);
    }

    [Fact]
    public async Task Stops_asking_for_more_once_one_request_has_been_held()
    {
        // Whatever held one request holds every request behind it — they share the drain's budget.
        // Carrying on would claim and release each of them in turn for nothing.
        var fetcher = new HeldFetcher();

        _host.Dispose();
        _host = new LibraryTestHost(services => services.AddScoped<IDownloadFetcher>(_ => fetcher));

        var emma = await ReaderWithAWorkAsync(2);
        await QueueAsync(emma, workId: 1);
        await QueueAsync(emma, workId: 2);

        await DrainAsync();

        Assert.Equal(1, fetcher.Calls);
    }

    [Fact]
    public async Task Fails_a_request_whose_file_stops_arriving_part_way()
    {
        // A transfer that goes quiet is cancelled by the download's own deadline rather than left
        // holding the global rate gate. What reaches the reader has to say something they can act
        // on, not the bare "the operation was canceled" that a cancellation carries.
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        _host.Http.RespondsToDownload = _ => throw new TaskCanceledException("The request was canceled.");
        await QueueAsync(emma);

        await DrainAsync();

        var download = await DownloadAsync();
        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.Contains("stopped sending", download.ErrorMessage);
        Assert.Empty(FilesUnder(DownloadPaths.Root));
    }

    [Fact]
    public async Task Does_not_ask_again_for_something_it_has_already_failed()
    {
        // A work AO3 has taken down would otherwise be requested on every poll for ever, which is
        // exactly the load this project exists not to produce.
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = url => new ScrapeHttpResponse("Not found", HttpStatusCode.NotFound, false, url);
        await QueueAsync(emma);

        var worker = _host.NewDownloadWorker();
        await worker.DrainQueueAsync(default);
        await worker.DrainQueueAsync(default);

        Assert.Single(_host.Http.Requested);
    }

    // ---- what the worker may touch ---------------------------------------------------------------

    [Fact]
    public async Task Asks_AO3_for_nothing_when_the_queue_is_empty()
    {
        await _host.SaveAo3LoginAsync();

        await _host.NewDownloadWorker().DrainQueueAsync(default);

        // Including the login: an idle instance re-authenticating on a timer would be requests that
        // read nothing at all.
        Assert.Empty(_host.Http.Requested);
        Assert.Empty(_host.Http.LoginPagesRequested);
        Assert.Empty(_host.Http.Posted);
    }

    [Fact]
    public async Task Holds_the_queue_when_no_AO3_login_is_stored()
    {
        var emma = await ReaderWithAWorkAsync(logIn: false);
        await QueueAsync(emma);

        await _host.NewDownloadWorker().DrainQueueAsync(default);

        // Held, not failed: nothing was attempted, and a reader whose instance is half-configured
        // should see a request still waiting rather than one that failed for reasons of its own.
        Assert.Equal(DownloadStatus.Pending, (await DownloadAsync()).Status);
        Assert.Empty(_host.Http.Requested);
        Assert.Empty(_host.Http.FilesRequested);
    }

    [Fact]
    public async Task Holds_the_queue_when_this_instance_cannot_identify_itself()
    {
        var emma = await ReaderWithAWorkAsync();
        await QueueAsync(emma);

        // The same gate scraping is held by. A download is an outbound request like any other, and
        // one that cannot say who is making it is the thing this project refuses to send.
        _host.OperatorContact = null;

        await _host.NewDownloadWorker().DrainQueueAsync(default);

        Assert.Equal(DownloadStatus.Pending, (await DownloadAsync()).Status);
        Assert.Empty(_host.Http.Requested);
    }

    [Fact]
    public async Task Leaves_alone_a_row_that_is_not_queued()
    {
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        var id = await QueueAsync(emma);

        // Downloading means a worker already holds it — the same rule the controller applies when
        // it declines to re-arm a fetch in flight.
        await SetStatusAsync(id, DownloadStatus.Downloading);

        await DrainAsync();

        Assert.Equal(DownloadStatus.Downloading, (await DownloadAsync()).Status);
        Assert.Empty(_host.Http.Requested);
    }

    [Fact]
    public async Task Refuses_a_request_that_stopped_being_queued_while_it_waited()
    {
        // The worker asks for Pending rows and then fetches them one scope later, so a request can
        // settle in between: another reader's fetch of the same version lands, and their next
        // request completes this one off the bytes now on disk. Claiming it anyway would fetch a
        // file this instance already has and overwrite it with a second copy of itself.
        var emma = await ReaderWithAWorkAsync();
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        var id = await QueueAsync(emma);

        var fileId = await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion);
        await CompleteAsync(id, fileId);

        Assert.Equal(DownloadFetchOutcome.Skipped, await _host.FetchDownloadAsync(id));

        Assert.Equal(DownloadStatus.Complete, (await DownloadAsync()).Status);
        Assert.Empty(_host.Http.Requested);
    }

    [Fact]
    public async Task Discards_a_part_file_a_restart_left_behind()
    {
        // The fetcher deletes its own when a fetch fails; a killed container leaves one with
        // nothing to clean it up, and each is worth up to the whole size ceiling. Nothing resumes a
        // part-file, so anything still there at startup is rubbish by definition.
        var partial = Path.Combine(
            _host.DataDirectory, DownloadPaths.PartialsRoot.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(partial);
        await File.WriteAllTextAsync(Path.Combine(partial, "abandoned.part"), "half an epub");

        await _host.NewDownloadWorker().ReleaseInterruptedFetchesAsync(default);

        Assert.Empty(FilesUnder(DownloadPaths.PartialsRoot));
    }

    [Fact]
    public async Task Re_queues_a_fetch_a_restart_interrupted()
    {
        // The other side of the rule above. Downloading survives a crash, nothing holds it after a
        // restart, and the controller will not touch it — so without this the request is stranded
        // in a state no request and no worker will ever reach again.
        var emma = await ReaderWithAWorkAsync();
        var id = await QueueAsync(emma);
        await SetStatusAsync(id, DownloadStatus.Downloading);

        await _host.NewDownloadWorker().ReleaseInterruptedFetchesAsync(default);

        Assert.Equal(DownloadStatus.Pending, (await DownloadAsync()).Status);
    }

    // ---- how much one drain may spend ------------------------------------------------------------

    [Fact]
    public async Task Stops_the_drain_when_the_run_budget_is_spent_and_finishes_on_the_next_poll()
    {
        // A queue of two hundred files is a real amount of load however it was asked for. The cap
        // is what turns it into several polls rather than one long burst; what it stops must stay
        // queued rather than failing.
        _host.Dispose();
        _host = new LibraryTestHost(services =>
            services.Configure<Ao3HttpClientOptions>(o => o.MaxRequestsPerRun = 2));

        var emma = await ReaderWithAWorkAsync(2);
        _host.Http.Responds = _ => WorkPage(EpubUrl);
        await QueueAsync(emma, workId: 1);
        await QueueAsync(emma, workId: 2);

        var worker = _host.NewDownloadWorker();
        await worker.DrainQueueAsync(default);

        var afterOne = await DownloadsAsync();
        Assert.Equal(DownloadStatus.Complete, afterOne.Single(d => d.WorkId == 1).Status);
        Assert.Equal(DownloadStatus.Pending, afterOne.Single(d => d.WorkId == 2).Status);

        await worker.DrainQueueAsync(default);

        Assert.All(await DownloadsAsync(), d => Assert.Equal(DownloadStatus.Complete, d.Status));
    }

    [Fact]
    public async Task Fetches_the_oldest_request_first()
    {
        var emma = await ReaderWithAWorkAsync(2);
        _host.Http.Responds = _ => WorkPage(EpubUrl);

        await QueueAsync(emma, workId: 2);
        await QueueAsync(emma, workId: 1);
        await StampAsync(2, FirstVersion);

        await DrainAsync();

        Assert.Equal("https://ao3.test/works/2?view_adult=true", _host.Http.Requested[0]);
    }

    // ---- the wake ----------------------------------------------------------------------------

    [Fact]
    public async Task A_queued_request_wakes_the_worker_rather_than_waiting_out_a_poll()
    {
        var emma = await ReaderWithAWorkAsync();

        // Following the ship signalled the scrape worker, which is that worker's own business.
        // Taken here so what is asserted below is what the download request sent.
        await _host.ScrapeWake.WaitAsync(TimeSpan.Zero);

        await QueueAsync(emma);

        Assert.True(await _host.DownloadWake.WaitAsync(TimeSpan.Zero));

        // Its own signal: asking for a file must not send the scrape worker sweeping a schedule
        // nothing has changed.
        Assert.False(await _host.ScrapeWake.WaitAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task A_request_answered_from_disk_wakes_nobody()
    {
        var emma = await ReaderWithAWorkAsync();
        await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion);

        await QueueAsync(emma);

        // Nothing to fetch, so nothing to wake — a signal here would be a drain that finds an empty
        // queue every time someone re-downloads something they already have.
        Assert.False(await _host.DownloadWake.WaitAsync(TimeSpan.Zero));
    }

    // ---- a page the work has moved past --------------------------------------------------------

    [Fact]
    public async Task Reads_the_work_page_again_when_the_cached_copy_predates_the_version_it_is_fetching()
    {
        // The failure this exists for: a reader downloads a work, which puts its page in the
        // response cache for fifteen minutes; an incremental pass moves the work on inside that
        // window; the reader asks for it again. There is no file at the new version, so a fetch
        // runs — and reads the address off the copy of the page from before the revision. Stored,
        // those bytes are the previous version sitting under a row saying they are the current one,
        // which is the exact failure keying the file by version exists to prevent.
        var emma = await ReaderWithAWorkAsync();

        var cachedAt = _host.Clock.Now.UtcDateTime;
        await MoveWorkOnAsync(1, FirstVersion.AddDays(1), observedAt: cachedAt.AddMinutes(5));

        _host.Http.Responds = _ => WorkPage(PreviousEpubUrl) with { FromCache = true, FetchedAt = cachedAt };
        _host.Http.RespondsFresh = _ => WorkPage(EpubUrl);

        await QueueAsync(emma);
        await DrainAsync();

        // Read, recognised as older than the revision, and read again off the wire.
        Assert.Equal(2, _host.Http.Requested.Count);
        Assert.Equal("https://ao3.test/works/1?view_adult=true", Assert.Single(_host.Http.FreshRequested));

        // And the file fetched is the one the current page offered, not the one the stale copy did.
        Assert.Equal(EpubUrl, Assert.Single(_host.Http.FilesRequested));

        var file = await FileAsync();
        Assert.Equal(FirstVersion.AddDays(1), file.WorkUpdatedAt);
        Assert.Equal(DownloadStatus.Complete, (await DownloadAsync()).Status);
    }

    [Fact]
    public async Task Answers_from_the_cached_page_when_the_work_has_not_moved_since_it_was_read()
    {
        // The case the cache exists for — a second format of the same unchanged work inside the
        // fifteen-minute window — and it has to stay a cache hit. Re-reading on every download
        // would put a rate-gated request in front of each one and buy nothing: a page read after
        // the last revision this instance saw is a page about the version being fetched.
        var emma = await ReaderWithAWorkAsync();

        var observedAt = _host.Clock.Now.UtcDateTime;
        await MoveWorkOnAsync(1, FirstVersion, observedAt);

        _host.Http.Responds = _ => WorkPage(EpubUrl)
            with { FromCache = true, FetchedAt = observedAt.AddMinutes(1) };

        await QueueAsync(emma);
        await DrainAsync();

        Assert.Empty(_host.Http.FreshRequested);
        Assert.Single(_host.Http.Requested);
        Assert.Equal(DownloadStatus.Complete, (await DownloadAsync()).Status);
    }

    [Fact]
    public async Task Fails_rather_than_storing_the_previous_version_when_the_page_read_again_is_gone()
    {
        // "Fetches the current version's bytes, or fails" — and a failure here is the acceptable
        // half. What is not acceptable is falling back to the address the stale page carried.
        var emma = await ReaderWithAWorkAsync();

        var cachedAt = _host.Clock.Now.UtcDateTime;
        await MoveWorkOnAsync(1, FirstVersion.AddDays(1), observedAt: cachedAt.AddMinutes(5));

        _host.Http.Responds = _ => WorkPage(PreviousEpubUrl) with { FromCache = true, FetchedAt = cachedAt };
        _host.Http.RespondsFresh = url => new ScrapeHttpResponse("Not found", HttpStatusCode.NotFound, false, url);

        await QueueAsync(emma);
        await DrainAsync();

        var download = await DownloadAsync();
        Assert.Equal(DownloadStatus.Failed, download.Status);
        Assert.Contains("404", download.ErrorMessage);

        Assert.Empty(_host.Http.FilesRequested);
        Assert.Empty(await FilesAsync());
    }

    [Fact]
    public async Task Leaves_a_request_queued_when_the_drain_cannot_afford_to_read_the_page_again()
    {
        // The cache hit that got here cost AO3 nothing and so left the budget untouched, but the
        // re-read is a real request and has to be paid for. Released rather than fetched off the
        // stale copy: a drain that ran out is a reason to come back, never a reason to store the
        // previous version's bytes.
        var emma = await ReaderWithAWorkAsync();

        var cachedAt = _host.Clock.Now.UtcDateTime;
        await MoveWorkOnAsync(1, FirstVersion.AddDays(1), observedAt: cachedAt.AddMinutes(5));

        var budget = new ScrapeBudget(
            maxRequests: 10, maxConsecutiveFailures: 5, maxDuration: TimeSpan.FromMinutes(10), _host.Clock);

        // The wall-clock allowance runs out while the page is being read, which is the one way to
        // stand between the cache hit and the re-read it asks for.
        _host.Http.Responds = _ =>
        {
            _host.Clock.Now = _host.Clock.Now.AddMinutes(20);
            return WorkPage(PreviousEpubUrl) with { FromCache = true, FetchedAt = cachedAt };
        };

        var downloadId = await QueueAsync(emma);

        Assert.Equal(DownloadFetchOutcome.Held, await _host.FetchDownloadAsync(downloadId, budget));
        Assert.Empty(_host.Http.FreshRequested);
        Assert.Empty(_host.Http.FilesRequested);
        Assert.Equal(DownloadStatus.Pending, (await DownloadAsync()).Status);
    }

    // ---- seeding -------------------------------------------------------------------------------

    /// <summary>A reader watching one ship carrying <paramref name="works"/> works, with a login stored.</summary>
    private async Task<ApplicationUser> ReaderWithAWorkAsync(int works = 1, bool logIn = true)
    {
        var emma = _host.SeedUser();
        var shipId = await WatchAsync(Lexa, emma);

        await using (var db = _host.NewContext())
        {
            for (long id = 1; id <= works; id++)
            {
                db.Works.Add(new Work { Id = id, Title = $"Work {id}", UpdatedAt = FirstVersion });
                db.ShipWorks.Add(new ShipWork { ShipId = shipId, WorkId = id });
            }

            await db.SaveChangesAsync();
        }

        if (logIn) await _host.SaveAo3LoginAsync();
        return emma;
    }

    private async Task<int> WatchAsync(string tagName, ApplicationUser watcher)
    {
        var result = await _host.Ships(watcher).WatchShip(new(tagName), default);
        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    /// <summary>Asks for a copy the way a reader does — through the endpoint, not by inserting a row.</summary>
    private async Task<int> QueueAsync(ApplicationUser reader, long workId = 1)
    {
        var result = await _host.NewDownloadsRequest(reader).RequestDownload(workId, new("Epub"), default);
        return Assert.IsType<DownloadDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Id;
    }

    private Task DrainAsync() => _host.NewDownloadWorker().DrainQueueAsync(default);

    /// <summary>
    /// A work page carrying one download link, in the shape the captured page holds it: the menu
    /// item, not a bare anchor.
    /// </summary>
    private static ScrapeHttpResponse WorkPage(string href) => new(
        "<html><body><ul class=\"work navigation actions\"><li class=\"download\">"
        + "<button class=\"collapsed\">Download</button>"
        + $"<ul class=\"expandable secondary hidden\"><li><a href=\"{href}\">EPUB</a></li></ul>"
        + "</li></ul></body></html>",
        HttpStatusCode.OK,
        FromCache: false);

    /// <summary>
    /// A stored copy: the row, and the bytes it names actually on disk. Both, because a row whose
    /// file has gone is not a copy this instance has — the fetch runs again instead.
    /// </summary>
    private async Task<int> SeedFileAsync(long workId, Ao3DownloadFormat format, DateTime version)
    {
        var relativePath = DownloadPaths.Relative(workId, format, version);
        var absolutePath = DownloadPaths.Absolute(_host.DataDirectory, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, FileBody);

        await using var db = _host.NewContext();

        var file = new WorkDownloadFile
        {
            WorkId = workId,
            Format = format,
            WorkUpdatedAt = version,
            RelativePath = relativePath,
            SizeBytes = FileBody.Length,
            FetchedAt = version,
        };

        db.WorkDownloadFiles.Add(file);
        await db.SaveChangesAsync();

        return file.Id;
    }

    /// <summary>Settles a request off bytes that turned up on disk, the way the endpoint does.</summary>
    private async Task CompleteAsync(int downloadId, int fileId)
    {
        await using var db = _host.NewContext();

        var download = await db.Downloads.FirstAsync(d => d.Id == downloadId);
        download.Status = DownloadStatus.Complete;
        download.WorkDownloadFileId = fileId;
        download.CompletedAt = FirstVersion;

        await db.SaveChangesAsync();
    }

    private async Task SetStatusAsync(int downloadId, DownloadStatus status)
    {
        await using var db = _host.NewContext();

        var download = await db.Downloads.FirstAsync(d => d.Id == downloadId);
        download.Status = status;

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// The work leaves the library, taking every request for it with it — synchronous, so it can
    /// be done from inside a response the fetcher is in the middle of reading.
    /// </summary>
    private void DeleteWork(long workId)
    {
        using var db = _host.NewContext();

        db.Works.Remove(db.Works.First(w => w.Id == workId));
        db.SaveChanges();
    }

    /// <summary>The author updates the work, which is what makes a stored copy a stale one.</summary>
    /// <param name="observedAt">
    /// When this instance saw the move — our clock, which the ingestor stamps alongside AO3's. Left
    /// to now, the way a pass that has just run would leave it; given explicitly by a test that
    /// needs a page read on one side of it or the other.
    /// </param>
    private async Task MoveWorkOnAsync(long workId, DateTime updatedAt, DateTime? observedAt = null)
    {
        await using var db = _host.NewContext();

        var work = await db.Works.FirstAsync(w => w.Id == workId);
        work.UpdatedAt = updatedAt;
        work.UpdatedAtObservedAt = observedAt ?? _host.Clock.Now.UtcDateTime;

        await db.SaveChangesAsync();
    }

    /// <summary>Backdates a request, so an ordering test can construct one made earlier.</summary>
    private async Task StampAsync(long workId, DateTime requestedAt)
    {
        await using var db = _host.NewContext();

        var download = await db.Downloads.FirstAsync(d => d.WorkId == workId);
        download.RequestedAt = requestedAt;

        await db.SaveChangesAsync();
    }

    private async Task<Download> DownloadAsync()
    {
        await using var db = _host.NewContext();
        return await db.Downloads.OrderBy(d => d.Id).FirstAsync();
    }

    private async Task<List<Download>> DownloadsAsync()
    {
        await using var db = _host.NewContext();
        return await db.Downloads.OrderBy(d => d.Id).ToListAsync();
    }

    private async Task<WorkDownloadFile> FileAsync()
    {
        await using var db = _host.NewContext();
        return await db.WorkDownloadFiles.SingleAsync();
    }

    private async Task<List<WorkDownloadFile>> FilesAsync()
    {
        await using var db = _host.NewContext();
        return await db.WorkDownloadFiles.ToListAsync();
    }

    /// <summary>Everything actually on disk under the data directory's downloads folder.</summary>
    private string[] FilesUnder(string relativeDirectory)
    {
        var directory = Path.Combine(_host.DataDirectory, relativeDirectory);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            : [];
    }
}

/// <summary>
/// Throws before touching the row, the way a fetcher whose own read of it failed would.
/// </summary>
internal sealed class ThrowingFetcher : IDownloadFetcher
{
    private readonly Exception _cause;

    public ThrowingFetcher(Exception cause) => _cause = cause;

    public Task<DownloadFetchOutcome> FetchAsync(
        int downloadId, ScrapeBudget budget, CancellationToken ct = default) => throw _cause;
}

/// <summary>
/// Reports every request as held without touching it, and counts how many times it was asked.
/// Stands in for the real fetcher where what is under test is how far the drain gets.
/// </summary>
internal sealed class HeldFetcher : IDownloadFetcher
{
    public int Calls { get; private set; }

    public Task<DownloadFetchOutcome> FetchAsync(
        int downloadId, ScrapeBudget budget, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(DownloadFetchOutcome.Held);
    }
}
