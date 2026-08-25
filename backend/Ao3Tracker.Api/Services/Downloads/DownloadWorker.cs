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

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DownloadWorker> _logger;
    private readonly Ao3HttpClientOptions _httpOptions;
    private readonly DownloadWakeSignal _wake;
    private readonly StoragePaths _paths;

    /// <summary>Last logged held/allowed state; null until the first drain with something queued.</summary>
    private bool? _allowed;

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

        await ReleaseInterruptedFetchesAsync(stoppingToken);

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

        var discarded = 0;

        foreach (var path in Directory.GetFiles(partials))
        {
            try
            {
                File.Delete(path);
                discarded++;
            }
            catch (IOException ex)
            {
                // One undeletable file is not worth failing startup over, and saying so is what
                // stops the directory growing unnoticed.
                _logger.LogWarning(ex, "Could not delete the abandoned partial download {Path}", path);
            }
            catch (UnauthorizedAccessException ex)
            {
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
                await MarkFailedAsync(downloadId, ex, ct);
                continue;
            }

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

            // Deleted while it was being fetched, or already recorded by the fetcher itself.
            if (download is null || download.Status != DownloadStatus.Downloading) return;

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
