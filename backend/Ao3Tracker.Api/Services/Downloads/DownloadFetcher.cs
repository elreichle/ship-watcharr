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
        var onDisk = await FindFileAsync(work, download.Format, ct);
        if (onDisk is not null) return await CompleteAsync(download, onDisk, ct);

        if (!budget.CanContinue(out var reason))
        {
            _logger.LogInformation(
                "Download {DownloadId} left queued: this drain stopped on {Reason}", download.Id, reason);

            return await ReleaseAsync(download, ct);
        }

        var pageUrl = WorkPageUrl(work.Id);
        var page = await GetPageAsync(pageUrl, budget, ct);

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

            // No winner means the write failed for a reason other than the race this catch is for.
            if (winner is null) throw;

            return winner;
        }
    }

    private Task<WorkDownloadFile?> FindFileAsync(Work work, Ao3DownloadFormat format, CancellationToken ct) =>
        _db.WorkDownloadFiles.FirstOrDefaultAsync(
            f => f.WorkId == work.Id && f.Format == format && f.WorkUpdatedAt == work.UpdatedAt, ct);

    private async Task<DownloadFetchOutcome> CompleteAsync(
        Download download, WorkDownloadFile file, CancellationToken ct)
    {
        download.WorkDownloadFileId = file.Id;
        download.Status = DownloadStatus.Complete;
        download.CompletedAt = _time.GetUtcNow().UtcDateTime;
        download.ErrorMessage = null;

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

        // WorkDownloadFileId is left exactly as it was found, which for a request the controller
        // re-armed means null — see T59 in .devloop/tasks.md, which owns whether re-arming should
        // hold on to the reader's previous copy until a replacement exists.

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
    private async Task<ScrapeHttpResponse> GetPageAsync(string url, ScrapeBudget budget, CancellationToken ct)
    {
        ScrapeHttpResponse page;

        try
        {
            page = await _http.GetAsync(url, ct);
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

        if (result.IsSuccess) budget.RecordSuccess();
        else budget.RecordFailure();

        return result;
    }

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
    private string WorkPageUrl(long workId) =>
        $"{_options.BaseUrl.TrimEnd('/')}/works/{workId}?view_adult=true";
}
