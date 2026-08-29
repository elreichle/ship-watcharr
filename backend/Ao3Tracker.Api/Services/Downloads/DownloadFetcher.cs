using System.Net;
using System.Security.Cryptography;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Downloads;

/// <summary>What one queued request turned into.</summary>
public enum DownloadFetchOutcome
{
    /// <summary>The row was taken, deleted, or was never this worker's to touch.</summary>
    Skipped,

    /// <summary>The file is on disk and the request points at it.</summary>
    Completed,

    /// <summary>The request is <see cref="DownloadStatus.Failed"/> and carries a message saying why.</summary>
    Failed,

    /// <summary>
    /// Nothing was attempted or the attempt was abandoned before the file was fetched, and the
    /// request is back in the queue. The drain stops here: the budget it ran out of is shared by
    /// everything behind it.
    /// </summary>
    Held,
}

/// <summary>
/// Turns one queued <see cref="Download"/> into bytes on disk. See <see cref="DownloadWorker"/> for
/// what drives it.
/// </summary>
public interface IDownloadFetcher
{
    Task<DownloadFetchOutcome> FetchAsync(int downloadId, ScrapeBudget budget, CancellationToken ct = default);
}

/// <summary>
/// The fetch itself: two rate-gated requests, a temp file, and a row naming what came back.
///
/// It is two requests rather than one because AO3's download addresses cannot be constructed — see
/// <see cref="Ao3DownloadLinks"/>. So this reads the work's own page for the link, then fetches the
/// link, and either half can fail on its own. Both failures are the same thing to a reader: one
/// request that says <see cref="DownloadStatus.Failed"/> with a message naming which half. Never a
/// retry loop — a work AO3 has removed would otherwise be asked for on every poll for ever.
/// </summary>
public sealed class DownloadFetcher : IDownloadFetcher
{
    private readonly AppDbContext _db;
    private readonly IRateLimitedHttpClient _http;
    private readonly StoragePaths _paths;
    private readonly Ao3HttpClientOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DownloadFetcher> _logger;

    public DownloadFetcher(
        AppDbContext db,
        IRateLimitedHttpClient http,
        StoragePaths paths,
        IOptions<Ao3HttpClientOptions> options,
        TimeProvider time,
        ILogger<DownloadFetcher> logger)
    {
        _db = db;
        _http = http;
        _paths = paths;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<DownloadFetchOutcome> FetchAsync(
        int downloadId, ScrapeBudget budget, CancellationToken ct = default)
    {
        var download = await _db.Downloads
            .Include(d => d.Work)
            .FirstOrDefaultAsync(d => d.Id == downloadId, ct);

        // Pending is the only status this may claim. Complete and Failed are settled, and
        // Downloading means someone else already holds the row — which is the same rule the
        // controller applies from the other side when it declines to re-arm an in-flight fetch.
        if (download is null || download.Status != DownloadStatus.Pending) return DownloadFetchOutcome.Skipped;

        download.Status = DownloadStatus.Downloading;
        await _db.SaveChangesAsync(ct);

        return await RunAsync(download, budget, ct);
    }

    private async Task<DownloadFetchOutcome> RunAsync(
        Download download, ScrapeBudget budget, CancellationToken ct)
    {
        var work = download.Work;

        // Another reader may have asked for the same format of the same version while this request
        // sat in the queue, and their fetch may already have finished. Checked here as well as in
        // the controller because the window between queueing and draining is exactly where that
        // happens — and the whole point of the shared file is that identical bytes are fetched once.
        var onDisk = await FindUsableFileAsync(work, download.Format, ct);
        if (onDisk is not null) return await CompleteAsync(download, onDisk, ct);

        if (!budget.CanContinue(out var reason))
        {
            _logger.LogInformation(
                "Download {DownloadId} left queued: this drain stopped on {Reason}", download.Id, reason);

            return await ReleaseAsync(download, ct);
        }

        var pageUrl = WorkPageUrl(work.Id);
        var page = await GetPageAsync(pageUrl, budget, ct);

        // A cached page read before the work was revised is the *previous* version's page, and the
        // address on it is the previous version's file — which this would then store keyed to the
        // version the row holds now, reporting the old bytes as a copy of the new work. Nothing on
        // the page says so: AO3 stamps its own clock into the address, which cannot be compared
        // against Work.UpdatedAt. What can be compared is when this instance saw the revision
        // against when the copy in front of us came off the wire.
        if (ReadBeforeTheCurrentVersion(page, work))
        {
            // The re-read is a real request, unlike the cache hit that got us here, so it is the
            // budget's to allow. Released rather than failed when it is not: there is nothing wrong
            // with this request, and the next drain starts it again — by which time the cached copy
            // may well have expired on its own.
            if (!budget.CanContinue(out reason))
            {
                _logger.LogInformation(
                    "Download {DownloadId} left queued: its work page is older than the version being "
                    + "fetched and this drain stopped on {Reason}", download.Id, reason);

                return await ReleaseAsync(download, ct);
            }

            _logger.LogInformation(
                "The cached page for work {WorkId} was read at {FetchedAt}, before this instance saw "
                + "the version it now stands at. Reading it again.", work.Id, page.FetchedAt);

            page = await GetPageAsync(pageUrl, budget, ct, fresh: true);
        }

        if (page.StatusCode != HttpStatusCode.OK)
        {
            return await FailAsync(
                download,
                $"AO3 answered {(int)page.StatusCode} for this work's page, so there was no download "
                + "address to read. Nothing was fetched.",
                ct);
        }

        var links = Ao3DownloadLinks.Parse(page.Content, page.FinalUrl ?? pageUrl);
        if (!links.TryGetValue(download.Format, out var fileUrl))
        {
            return await FailAsync(
                download,
                $"AO3's page for this work offers no {download.Format} download. It may be "
                + "restricted, or AO3 may have stopped offering that format.",
                ct);
        }

        // The second request is not free and the budget may have been spent on the first. Released
        // rather than failed: nothing is wrong with this request, it simply arrived at the end of a
        // drain, and the next poll starts it again from a page that is still in the response cache.
        if (!budget.CanContinue(out reason))
        {
            _logger.LogInformation(
                "Download {DownloadId} left queued after reading its address: this drain stopped on {Reason}",
                download.Id, reason);

            return await ReleaseAsync(download, ct);
        }

        return await FetchFileAsync(download, work, fileUrl, budget, ct);
    }

    private async Task<DownloadFetchOutcome> FetchFileAsync(
        Download download, Work work, string fileUrl, ScrapeBudget budget, CancellationToken ct)
    {
        var relativePath = DownloadPaths.Relative(work.Id, download.Format, work.UpdatedAt);
        var destination = DownloadPaths.Absolute(_paths.DataDirectory, relativePath);

        // Written under a name nothing reads and moved into place only once it is whole. A crash or
        // a truncated response mid-fetch would otherwise leave a partial file sitting at the exact
        // path a row calls a complete copy of the work.
        var partialPath = DownloadPaths.Absolute(
            _paths.DataDirectory, $"{DownloadPaths.PartialsRoot}/{Guid.NewGuid():n}.part");

        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);

        try
        {
            ScrapeDownloadResponse result;
            string sha256;

            try
            {
                await using var file = File.Create(partialPath);
                result = await DownloadFileAsync(fileUrl, file, budget, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Not a shutdown — nobody asked this to stop, so it is the download's own deadline
                // or the transport's timeout. Recorded as a failure with a message a reader can act
                // on rather than left as the bare "operation was canceled" the worker would write.
                return await FailAsync(
                    download, "AO3 stopped sending the file before it was complete.", ct);
            }

            if (result.ExceededSizeLimit)
            {
                return await FailAsync(
                    download,
                    $"The file AO3 served was larger than this instance will store "
                    + $"({_options.MaxDownloadBytes} bytes) and was abandoned part-way.",
                    ct);
            }

            if (!result.IsSuccess)
            {
                return await FailAsync(
                    download, $"AO3 answered {(int)result.StatusCode} for the file itself.", ct);
            }

            if (!LandedOnTheFile(result.FinalUrl, download.Format))
            {
                return await FailAsync(
                    download,
                    "AO3 answered the download with a different page — most likely the login form, "
                    + "which is what a session that died mid-fetch looks like. Nothing was stored.",
                    ct);
            }

            await using (var written = File.OpenRead(partialPath))
            {
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(written, ct));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(partialPath, destination, overwrite: true);

            var stored = await StoreFileAsync(work, download.Format, relativePath, result.BytesWritten, sha256, ct);
            return await CompleteAsync(download, stored, ct);
        }
        finally
        {
            // Only ever present when something went wrong between creating it and the move.
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
    }

    /// <summary>
    /// Records the shared file, or finds the one another fetch of the same bytes just recorded.
    /// </summary>
    /// <remarks>
    /// The unique index on (work, format, version) is what decides this, not a read beforehand: two
    /// readers asking for the same format of the same work at the same moment both find nothing on
    /// disk and both fetch. The loser's bytes are identical to the winner's — they were written to
    /// the same deterministic path — so the row it keeps is the winner's.
    /// </remarks>
    private async Task<WorkDownloadFile> StoreFileAsync(
        Work work,
        Ao3DownloadFormat format,
        string relativePath,
        long sizeBytes,
        string sha256,
        CancellationToken ct)
    {
        var file = new WorkDownloadFile
        {
            WorkId = work.Id,
            Format = format,
            WorkUpdatedAt = work.UpdatedAt,
            RelativePath = relativePath,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            FetchedAt = _time.GetUtcNow().UtcDateTime,
        };

        _db.WorkDownloadFiles.Add(file);

        try
        {
            await _db.SaveChangesAsync(ct);
            return file;
        }
        catch (DbUpdateException)
        {
            _db.Entry(file).State = EntityState.Detached;

            var winner = await FindFileAsync(work, format, ct);

            // No winner means the write failed for a reason other than the race this catch is for,
            // and the bytes are already at their destination: the move happens before this. Left
            // there they would be up to MaxDownloadBytes that no row names, that DiscardPartialFiles
            // does not sweep — it only knows the partials directory — and that nothing collects
            // until the same version of the same work is fetched again. Deleted here rather than in
            // the caller because this is the one place that knows no row survived to name them.
            if (winner is null)
            {
                DiscardStoredFile(relativePath);
                throw;
            }

            // The path is derived from (work, format, version), so the winner's row already names
            // the bytes just written. Where the winner is a row whose file had gone missing — the
            // case FindUsableFileAsync sends back here — its size and checksum describe bytes that
            // no longer exist, and the row is only true again once they describe these.
            if (winner.SizeBytes != sizeBytes || winner.Sha256 != sha256)
            {
                winner.SizeBytes = sizeBytes;
                winner.Sha256 = sha256;
                winner.FetchedAt = _time.GetUtcNow().UtcDateTime;

                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex)
                {
                    // The bytes are right and the row names them, so the reader gets their file;
                    // what is stale is the size this instance reports for it. Said out loud rather
                    // than thrown: failing here would fail a request whose file is on disk, and the
                    // request that replaced it would complete off this same row anyway.
                    _logger.LogWarning(
                        ex, "Could not refresh the stored size and checksum of {Path}", relativePath);
                }
            }

            return winner;
        }
    }

    /// <summary>
    /// The row for this work's current version, where the bytes it names are still there.
    /// </summary>
    /// <remarks>
    /// A row alone is not evidence of a file. A data directory that lost one while keeping its row
    /// — a remounted volume, a hand-cleaned disk, a partial restore — would otherwise answer every
    /// future request for that work and format with Complete and a path to nothing, and the reader
    /// could not get out of it by asking again: the controller reads the same row to decide there
    /// is nothing to fetch. Re-fetching writes to the same deterministic path, so the fetch this
    /// returns null for is also what repairs the row.
    /// </remarks>
    private async Task<WorkDownloadFile?> FindUsableFileAsync(
        Work work, Ao3DownloadFormat format, CancellationToken ct)
    {
        var file = await FindFileAsync(work, format, ct);
        if (file is null) return null;

        if (File.Exists(DownloadPaths.Absolute(_paths.DataDirectory, file.RelativePath))) return file;

        _logger.LogWarning(
            "The stored {Format} of work {WorkId} is recorded at {Path}, which is not on disk. "
            + "Fetching it again.", format, work.Id, file.RelativePath);

        return null;
    }

    private Task<WorkDownloadFile?> FindFileAsync(Work work, Ao3DownloadFormat format, CancellationToken ct) =>
        _db.WorkDownloadFiles.FirstOrDefaultAsync(
            f => f.WorkId == work.Id && f.Format == format && f.WorkUpdatedAt == work.UpdatedAt, ct);

    /// <summary>Removes bytes no row survived to name. Never a reason to fail anything further.</summary>
    private void DiscardStoredFile(string relativePath)
    {
        var path = DownloadPaths.Absolute(_paths.DataDirectory, relativePath);

        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Path}, which no download row names", path);
        }
    }

    private async Task<DownloadFetchOutcome> CompleteAsync(
        Download download, WorkDownloadFile file, CancellationToken ct)
    {
        download.WorkDownloadFileId = file.Id;
        download.Status = DownloadStatus.Complete;
        download.CompletedAt = _time.GetUtcNow().UtcDateTime;
        download.ErrorMessage = null;

        // The replacement is on disk, which is the one thing that supersedes the copy the reader
        // was holding through this fetch. Kept any longer it would be a second file offered beside
        // the current one, for a version nobody asked for.
        download.PreviousWorkDownloadFileId = null;

        await _db.SaveChangesAsync(ct);
        return DownloadFetchOutcome.Completed;
    }

    private async Task<DownloadFetchOutcome> FailAsync(Download download, string message, CancellationToken ct)
    {
        _logger.LogWarning(
            "Download {DownloadId} ({Format} of work {WorkId}) failed: {Message}",
            download.Id, download.Format, download.WorkId, message);

        download.Status = DownloadStatus.Failed;
        download.ErrorMessage = message;
        download.CompletedAt = _time.GetUtcNow().UtcDateTime;

        // Both file references are left exactly as they were found. For a request the controller
        // re-armed that means WorkDownloadFileId is null — this fetch was the thing that would have
        // filled it — while PreviousWorkDownloadFileId still names the copy the reader had before
        // they asked for a newer one. Failing to fetch is not a reason to take that away.

        await _db.SaveChangesAsync(ct);
        return DownloadFetchOutcome.Failed;
    }

    /// <summary>Puts a claimed request back in the queue, exactly as it was found.</summary>
    private async Task<DownloadFetchOutcome> ReleaseAsync(Download download, CancellationToken ct)
    {
        download.Status = DownloadStatus.Pending;
        await _db.SaveChangesAsync(ct);

        return DownloadFetchOutcome.Held;
    }

    /// <summary>
    /// Whether the response we are about to call a copy of the work is really the file we asked
    /// for, judged by where the request ended up.
    /// </summary>
    /// <remarks>
    /// A 200 is not enough. This transport follows redirects, so an AO3 that declines a download —
    /// a restricted work whose session died in the seconds between reading the page and fetching
    /// the link — answers by redirecting to the login form, which is a 200 carrying HTML. Stored,
    /// that becomes a login page on disk under a name saying it is an EPUB, with a row and a
    /// checksum agreeing.
    ///
    /// Judged on the extension rather than on the whole address, so a redirect that still serves
    /// the file (a mirror, a CDN) is not refused for moving it; and judged not at all when the
    /// transport reported no final URL, because "no evidence" is not evidence — the same rule the
    /// session reading follows.
    /// </remarks>
    private static bool LandedOnTheFile(string? finalUrl, Ao3DownloadFormat format)
    {
        if (finalUrl is null) return true;
        if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri)) return true;

        var extension = Path.GetExtension(uri.AbsolutePath).TrimStart('.');
        return string.Equals(extension, DownloadPaths.Extension(format), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The work's page, with what it cost recorded against the budget.
    /// </summary>
    /// <remarks>
    /// A request that threw counts as a failure rather than as nothing: it reached AO3 or failed
    /// trying, and the circuit breaker exists to stop a drain grinding through its whole allowance
    /// against an archive that is plainly down. A cache hit counts as neither — nothing left this
    /// process, which is what makes a second format of the same work cheap.
    /// </remarks>
    private async Task<ScrapeHttpResponse> GetPageAsync(
        string url, ScrapeBudget budget, CancellationToken ct, bool fresh = false)
    {
        ScrapeHttpResponse page;

        try
        {
            page = fresh ? await _http.GetFreshAsync(url, ct) : await _http.GetAsync(url, ct);
        }
        catch
        {
            budget.RecordFailure();
            throw;
        }

        if (page.FromCache) budget.RecordCacheHit();
        else if (page.StatusCode == HttpStatusCode.OK) budget.RecordSuccess();
        else budget.RecordFailure();

        return page;
    }

    /// <summary>The file itself, recorded against the budget the same way.</summary>
    private async Task<ScrapeDownloadResponse> DownloadFileAsync(
        string url, Stream destination, ScrapeBudget budget, CancellationToken ct)
    {
        ScrapeDownloadResponse result;

        try
        {
            result = await _http.DownloadAsync(url, destination, ct);
        }
        catch
        {
            budget.RecordFailure();
            throw;
        }

        // A file abandoned for passing this instance's own size ceiling counted as a failure here,
        // and three oversized files in one drain would trip the breaker and hold the rest of the
        // queue on the grounds that AO3 was plainly down — when AO3 had served every one of them
        // perfectly. The breaker is about the archive's health, and a limit chosen at this end is
        // not evidence about it.
        if (result.IsSuccess || result.ExceededSizeLimit) budget.RecordSuccess();
        else budget.RecordFailure();

        return result;
    }

    /// <summary>
    /// Whether the page in front of us was read before this instance saw the version being fetched.
    /// </summary>
    /// <remarks>
    /// Only ever true of a cached copy — a page just read off the wire cannot predate anything
    /// already recorded — but the comparison is made on the timestamps rather than on
    /// <see cref="ScrapeHttpResponse.FromCache"/>, because it is the timestamps that decide it.
    ///
    /// False whenever either stamp is missing, which is the direction that keeps the cache doing its
    /// job: a work whose revision this instance has never watched move, or a response nothing
    /// stamped, is no evidence that the page is stale, and treating "unknown" as stale would put a
    /// request behind every download of an unchanged work.
    /// </remarks>
    private static bool ReadBeforeTheCurrentVersion(ScrapeHttpResponse page, Work work) =>
        work.UpdatedAtObservedAt is { } observed && page.FetchedAt is { } read && read < observed;

    /// <summary>
    /// The work's own page — where the download addresses are.
    /// </summary>
    /// <remarks>
    /// <c>view_adult</c> is AO3's own "Proceed" link. Without it an explicit work answers with an
    /// interstitial that carries no download menu, and the request would fail saying AO3 offers no
    /// such format when what it offered was a warning. It is a parameter the archive publishes,
    /// not a way around anything: the instance is logged in and the page is one this account may
    /// read either way.
    /// </remarks>
    /// <summary>
    /// Shared with the detail pass, which reads the same page for what a blurb does not carry — so
    /// the <c>view_adult</c> parameter that keeps AO3 from answering with its content interstitial
    /// is decided in one place. See <see cref="Ao3WorkPageUrl"/>.
    /// </summary>
    private string WorkPageUrl(long workId) => Ao3WorkPageUrl.For(_options.BaseUrl, workId);
}
