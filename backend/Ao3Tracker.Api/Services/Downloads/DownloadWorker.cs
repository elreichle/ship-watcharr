using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Downloads;

/// <summary>
/// Drains the download queue, one request at a time, behind the same rate gate as every scrape.
///
/// It exists because a fetch cannot happen inside the request that asks for it: outbound requests
/// are spaced 5-8 seconds apart and a download costs two of them, so a page that waited would be a
/// hung page. So <c>POST /api/works/{id}/downloads</c> writes a row, signals
/// <see cref="DownloadWakeSignal"/>, and this picks it up.
///
/// A sibling of <see cref="ScrapeWorker"/> rather than a mode of it: what is due to be scraped is
/// decided by a schedule, what is due to be downloaded by someone having asked. They share the one
/// thing that matters — the global rate gate inside <see cref="IRateLimitedHttpClient"/> — so a
/// download and a scrape queue behind each other rather than doubling this instance's load on AO3.
/// </summary>
public class DownloadWorker : BackgroundService
{
    /// <summary>
    /// How long the worker sleeps when nothing wakes it. A request arrives by signal instead; this
    /// is what catches one released by a drain that ran out of budget, and anything a lost signal
    /// would otherwise have stranded.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many polls in a row a request may fail on before it is recorded as failed rather than
    /// re-queued.
    /// </summary>
    /// <remarks>
    /// Everything that escapes the fetcher is infrastructure — the archive unreachable, the
    /// database locked, the disk refusing — and all of it may work on the next poll, which is why
    /// none of it settles the request on one attempt. But a permanent one must not cycle the queue
    /// for ever: with nothing counting, an instance whose configured archive does not resolve spends
    /// <see cref="Ao3HttpClientOptions.MaxConsecutiveFailures"/> requests every poll indefinitely —
    /// thousands a day at an endpoint that is not answering — while the reader is shown a request
    /// that says only "queued". Three attempts, then a failure they can see and act on.
    /// </remarks>
    private const int MaxAttemptsPerRequest = 3;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DownloadWorker> _logger;
    private readonly Ao3HttpClientOptions _httpOptions;
    private readonly DownloadWakeSignal _wake;
    private readonly StoragePaths _paths;

    /// <summary>Last logged held/allowed state; null until the first drain with something queued.</summary>
    private bool? _allowed;

    /// <summary>
    /// Consecutive polls each queued request has failed on, for <see cref="MaxAttemptsPerRequest"/>.
    /// </summary>
    /// <remarks>
    /// In memory rather than a column, and so forgotten by a restart — which is the same thing a
    /// restart already does to a claimed row, since <see cref="ReleaseInterruptedFetchesAsync"/>
    /// re-queues everything it finds. What it costs is that an instance restarted often enough
    /// re-attempts a hopeless request; what a column would cost is two migrations for a count
    /// nothing outside this loop reads. Only failing requests appear here, and each is forgotten
    /// when it leaves the queue.
    /// </remarks>
    private readonly Dictionary<int, int> _attempts = [];

    public DownloadWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<DownloadWorker> logger,
        IOptions<Ao3HttpClientOptions> httpOptions,
        DownloadWakeSignal wake,
        StoragePaths paths)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _httpOptions = httpOptions.Value;
        _wake = wake;
        _paths = paths;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Download worker starting, polling every {PollInterval}", PollInterval);

        try
        {
            await ReleaseInterruptedFetchesAsync(stoppingToken);
        }
        catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, stoppingToken))
        {
            // The same rule as the loop below, stated again because this runs before it and so is
            // not covered by it: tidying up after a restart is worth nothing next to starting, and
            // an exception escaping here would stop the host before the queue was drained once.
            _logger.LogError(ex, "Could not tidy up after a restart");
        }

        while (true)
        {
            try
            {
                await DrainQueueAsync(stoppingToken);
            }
            catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, stoppingToken))
            {
                // Nothing may end this loop. A BackgroundService that throws stops the host, so an
                // escaping exception would not merely lose one download — it would take the API
                // down with it. Same rule as ScrapeWorker's loop.
                _logger.LogError(ex, "Unhandled error while draining the download queue");
            }

            await _wake.WaitAsync(PollInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Puts back anything left <see cref="DownloadStatus.Downloading"/> by a crash or a hard
    /// shutdown.
    /// </summary>
    /// <remarks>
    /// Necessary because <c>Downloading</c> means "a worker holds this row", which is what stops the
    /// controller from re-arming a fetch in flight. Nothing holds it after a restart, so without
    /// this the request is stranded in a state no request and no worker will ever touch again —
    /// and the reader's only way out would be to delete it and ask again.
    ///
    /// Every such row is stale by definition: the app is one process, and this runs before the
    /// first drain claims anything.
    /// </remarks>
    internal async Task ReleaseInterruptedFetchesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var interrupted = await db.Downloads
                .Where(d => d.Status == DownloadStatus.Downloading)
                .ToListAsync(ct);

            if (interrupted.Count == 0) return;

            foreach (var download in interrupted) download.Status = DownloadStatus.Pending;

            await db.SaveChangesAsync(ct);
            _logger.LogWarning("Re-queued {Count} download(s) interrupted by a restart", interrupted.Count);
        }
        catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
        {
            _logger.LogError(ex, "Failed to re-queue interrupted downloads");
        }
        finally
        {
            DiscardPartialFiles();
        }
    }

    /// <summary>
    /// Deletes whatever is left in the partials directory.
    /// </summary>
    /// <remarks>
    /// The fetcher removes its own part-file when a fetch fails, but a hard crash or a killed
    /// container leaves one behind with nothing to clean it up — and each is worth up to
    /// <see cref="Ao3HttpClientOptions.MaxDownloadBytes"/>. Nothing reads this directory and nothing
    /// resumes a part-file, so anything in it at startup is rubbish by definition: the run that
    /// created it is gone, and the row it belonged to has just been re-queued to start again.
    /// </remarks>
    private void DiscardPartialFiles()
    {
        var partials = DownloadPaths.Absolute(_paths.DataDirectory, DownloadPaths.PartialsRoot);
        if (!Directory.Exists(partials)) return;

        string[] abandoned;

        try
        {
            abandoned = Directory.GetFiles(partials);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory this process cannot read at all — a volume mounted with the wrong
            // ownership, or a permissions change an image update brought with it. Saying so is the
            // whole of what can be done about it: the files behind it are wasted space, and wasted
            // space is not a reason to refuse to start.
            _logger.LogWarning(ex, "Could not read the partial downloads directory {Path}", partials);

            return;
        }

        var discarded = 0;

        foreach (var path in abandoned)
        {
            try
            {
                File.Delete(path);
                discarded++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One undeletable file is not worth failing startup over, and saying so is what
                // stops the directory growing unnoticed.
                _logger.LogWarning(ex, "Could not delete the abandoned partial download {Path}", path);
            }
        }

        if (discarded > 0)
            _logger.LogWarning("Discarded {Count} partial download(s) left by a restart", discarded);
    }

    internal async Task DrainQueueAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Ids, not entities: each request is fetched in a scope of its own below, and an entity
        // tracked by this scope's context has no business being written through that one.
        var queued = await db.Downloads
            .Where(d => d.Status == DownloadStatus.Pending)
            // Oldest first, tie-broken by id: requests made in the same tick share a timestamp, and
            // without a total order a drain could keep picking the same one.
            .OrderBy(d => d.RequestedAt)
            .ThenBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct);

        // Nothing queued, so nothing needs a session or a gate check. Same order as ScrapeWorker,
        // and for the same reason: an idle instance must not re-authenticate on a timer.
        if (queued.Count == 0) return;

        ForgetRequestsNoLongerQueued(queued);

        if (!await MayFetchAsync(scope.ServiceProvider, ct)) return;

        // One budget for the drain rather than one per request. A queue of two hundred files is a
        // real amount of load however it was asked for, and the cap is what turns it into a few
        // polls' worth instead of one long burst. Requests left over stay Pending.
        var budget = new ScrapeBudget(_httpOptions);

        foreach (var downloadId in queued)
        {
            ct.ThrowIfCancellationRequested();

            // One scope per request, which is what ScrapeWorker does per job and for the same
            // reason: sharing a context means a change set one request's database refused is still
            // tracked when the next one saves.
            using var itemScope = _scopeFactory.CreateScope();

            DownloadFetchOutcome outcome;

            try
            {
                outcome = await itemScope.ServiceProvider
                    .GetRequiredService<IDownloadFetcher>()
                    .FetchAsync(downloadId, budget, ct);
            }
            catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
            {
                // The fetcher records the failures it can see coming. Anything still escaping
                // happened around the fetch rather than inside it — including a save the database
                // refused, which is exactly the case its own context cannot write the failure
                // through. Hence a fresh scope.
                _logger.LogError(ex, "Download {DownloadId} could not be fetched", downloadId);
                await RecordAttemptAsync(downloadId, ex, ct);

                continue;
            }

            // Anything the fetcher answered for itself — completed, failed, or held — settles the
            // request as far as this counter is concerned.
            _attempts.Remove(downloadId);

            // The budget this request ran out of is the same one everything behind it would spend.
            if (outcome == DownloadFetchOutcome.Held) break;
        }
    }

    /// <summary>
    /// Whether this instance may make an AO3 request at all: configured to identify itself, and
    /// logged in.
    /// </summary>
    /// <remarks>
    /// The same two gates the scrape worker applies, because they are about the instance rather
    /// than about scraping — a download is an outbound request like any other, and one made without
    /// an honest User-Agent is the thing this project refuses to send. Queued requests are held
    /// exactly as due jobs are: nothing attempted, nothing recorded, nothing failed. A reader whose
    /// download waits sees it still Pending, which is what it is.
    /// </remarks>
    private async Task<bool> MayFetchAsync(IServiceProvider services, CancellationToken ct)
    {
        var gate = await services.GetRequiredService<ScrapingGate>().EvaluateAsync(ct);

        if (!gate.CanScrape)
        {
            // Logged on transition only, and at a lower level than the scrape worker's version of
            // the same message: that worker reports the identical blockers in full every time they
            // change, and repeating them here would say the same thing twice a minute.
            if (_allowed != false)
            {
                _logger.LogWarning("Downloads are held.\n\n{Problem}", gate.Problem);
                _allowed = false;
            }

            return false;
        }

        var session = await services.GetRequiredService<IAo3SessionProvider>().EnsureSessionAsync(ct);

        if (!session.Success)
        {
            if (_allowed != false)
            {
                _logger.LogWarning(
                    "Downloads are held: this instance is not logged in to AO3.\n\n{Error}", session.Error);
                _allowed = false;
            }

            return false;
        }

        _allowed = true;
        return true;
    }

    /// <summary>Drops the attempt counts of requests that are no longer in the queue.</summary>
    private void ForgetRequestsNoLongerQueued(IReadOnlyCollection<int> queued)
    {
        if (_attempts.Count == 0) return;

        var stillQueued = queued.ToHashSet();

        foreach (var downloadId in _attempts.Keys.Where(id => !stillQueued.Contains(id)).ToList())
            _attempts.Remove(downloadId);
    }

    /// <summary>
    /// Records that this poll's attempt at a request failed: back in the queue, or recorded as
    /// failed once it has spent <see cref="MaxAttemptsPerRequest"/> polls doing so.
    /// </summary>
    /// <remarks>
    /// Re-queueing rather than failing on the first throw is the point. The transport retries
    /// *responses* — 429 and 5xx — so a DNS failure, a reset socket or a connect timeout has had
    /// exactly one attempt, and the ship walk deliberately re-asks in the same case; a database
    /// that was briefly locked is the same kind of evidence. The fetcher's "never a retry loop"
    /// rule is about AO3's answers, a work it has taken down, not about failing to reach it. What
    /// stops that becoming a queue cycling against a dead endpoint is the count, since the drain's
    /// circuit breaker only bounds one poll and is rebuilt by the next.
    /// </remarks>
    private async Task RecordAttemptAsync(int downloadId, Exception cause, CancellationToken ct)
    {
        var attempts = _attempts.GetValueOrDefault(downloadId) + 1;

        if (attempts >= MaxAttemptsPerRequest)
        {
            _attempts.Remove(downloadId);
            await MarkFailedAsync(downloadId, cause, ct);

            return;
        }

        _attempts[downloadId] = attempts;
        await ReleaseAsync(downloadId, ct);
    }

    /// <summary>
    /// Puts a claimed request back in the queue, through a context that never saw the fetch. A row
    /// the fetch never got as far as claiming is already there, and is left alone.
    /// </summary>
    private async Task ReleaseAsync(int downloadId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var download = await db.Downloads.FirstOrDefaultAsync(d => d.Id == downloadId, ct);

            // Only a row this drain still holds. Anything else was settled by the fetcher or by
            // another request while this one was failing.
            if (download is null || download.Status != DownloadStatus.Downloading) return;

            download.Status = DownloadStatus.Pending;

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
        {
            // The row stays Downloading and the next restart re-queues it — the same fallback
            // MarkFailedAsync has, and for the same reason.
            _logger.LogError(ex, "Could not re-queue download {DownloadId}", downloadId);
        }
    }

    /// <summary>
    /// Records a fetch that failed in a way the fetcher could not write down, through a context
    /// that never saw it.
    /// </summary>
    private async Task MarkFailedAsync(int downloadId, Exception cause, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var download = await db.Downloads.FirstOrDefaultAsync(d => d.Id == downloadId, ct);

            // Complete and Failed are the fetcher's own record of what happened, written through
            // its own context; overwriting one would replace an answer with a guess. Everything
            // else is recordable — Downloading is the ordinary case, a row this drain claimed, and
            // Pending is a fetch that threw before it could claim one at all. Leaving that second
            // case alone is what let a request whose read of its own row failed be re-selected,
            // throw and record nothing on every poll, while the reader went on being told it was
            // queued.
            if (download is null || download.Status is DownloadStatus.Complete or DownloadStatus.Failed)
                return;

            download.Status = DownloadStatus.Failed;
            download.ErrorMessage = $"The fetch did not complete: {cause.Message}";
            download.CompletedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
        {
            // Out of ways to record it. The row stays Downloading and the next restart re-queues
            // it, which is the outcome this method exists to make rare rather than routine.
            _logger.LogError(ex, "Could not record the failure of download {DownloadId}", downloadId);
        }
    }
}
