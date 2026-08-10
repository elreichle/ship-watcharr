using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// In-process job scheduler: polls due ScrapeJobs, runs each one through its configured
/// IAo3Scraper, and records a ScrapeRun per execution.
/// This is a BackgroundService (not an OS-level service) because everything — API and
/// worker — ships as one container/process.
///
/// The poll is a floor, not the only trigger: following a tag makes its job due immediately and
/// signals <see cref="ScrapeWakeSignal"/>, so a first pass starts without waiting out a tick.
/// </summary>
public class ScrapeWorker : BackgroundService
{
    /// <summary>
    /// How long the worker sleeps when nothing wakes it. Only jobs that came due while it slept
    /// depend on this — a newly followed ship arrives by signal instead.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a run may go without a heartbeat before startup treats it as dead. Generous
    /// because a single page fetch can legitimately take a while behind the rate limiter.
    /// </summary>
    private static readonly TimeSpan StaleRunThreshold = TimeSpan.FromMinutes(30);

    /// <summary>Fraction by which each job's next-run time is spread. See <see cref="NextRunAfter"/>.</summary>
    private const double ScheduleJitterFactor = 0.1;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScrapeWorker> _logger;
    private readonly Ao3HttpClientOptions _httpOptions;
    private readonly ScrapeWakeSignal _wake;

    /// <summary>Last logged scraping-enabled state; null until the first check. See RunDueJobsAsync.</summary>
    private bool? _scrapingEnabled;

    public ScrapeWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ScrapeWorker> logger,
        IOptions<Ao3HttpClientOptions> httpOptions,
        ScrapeWakeSignal wake)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _httpOptions = httpOptions.Value;
        _wake = wake;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Scrape worker starting, polling every {PollInterval}, request spacing {Min}-{Max}",
            PollInterval, _httpOptions.MinDelayBetweenRequests, _httpOptions.MaxDelayBetweenRequests);

        await ReconcileInterruptedRunsAsync(stoppingToken);

        // Sweep, then sleep until woken or until the interval expires — rather than a PeriodicTimer,
        // whose tick cannot be brought forward by a follow. Measuring the wait from the end of a
        // sweep also stops a long backfill from stacking up ticks it owed while it ran.
        while (true)
        {
            try
            {
                await RunDueJobsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unhandled error while polling scrape jobs");
            }

            await _wake.WaitAsync(PollInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Closes out runs left mid-flight by a crash or a hard shutdown. Without this they stay
    /// Running forever, and since backfills legitimately run for hours there is no timeout that
    /// could distinguish "still working" from "died three days ago" at read time.
    /// </summary>
    private async Task ReconcileInterruptedRunsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var cutoff = DateTime.UtcNow - StaleRunThreshold;
            var stale = await db.ScrapeRuns
                .Where(r => r.Status == ScrapeRunStatus.Running)
                .Where(r => (r.HeartbeatAt ?? r.StartedAt) < cutoff)
                .ToListAsync(ct);

            if (stale.Count == 0) return;

            foreach (var run in stale)
            {
                run.Status = ScrapeRunStatus.Interrupted;
                run.CompletedAt = DateTime.UtcNow;
                run.StopReason = "interrupted";
                run.ErrorMessage ??= "Run did not complete — the application stopped while it was in progress.";
            }

            await db.SaveChangesAsync(ct);
            _logger.LogWarning("Marked {Count} stale scrape run(s) as Interrupted on startup", stale.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to reconcile interrupted scrape runs");
        }
    }

    private async Task RunDueJobsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Re-checked every poll rather than once at startup. A fresh install boots with no contact
        // at all — one only appears once someone saves an email at Settings → Account or a contact
        // at System → Scraping — so a one-shot check would latch scraping off and never notice
        // that happening.
        var userAgents = scope.ServiceProvider.GetRequiredService<Ao3UserAgentProvider>();
        var (ok, userAgent, error) = await userAgents.TryGetUserAgentAsync(ct);

        if (!ok)
        {
            // Logged on transition only. This runs every minute, and a scraper that cannot
            // identify itself is a steady state, not an event worth repeating 1,440 times a day.
            if (_scrapingEnabled != false)
            {
                _logger.LogError("Scraping is disabled.\n\n{Error}", error);
                _scrapingEnabled = false;
            }
            return;
        }

        if (_scrapingEnabled != true)
        {
            // Logged verbatim so the operator can see exactly what this instance tells AO3 about
            // itself, rather than reconstructing it from three separate settings.
            _logger.LogInformation("Scraping enabled. Identifying to AO3 as: {UserAgent}", userAgent);
            _scrapingEnabled = true;
        }

        var now = DateTime.UtcNow;
        var dueJobs = await db.ScrapeJobs
            .Include(j => j.Ship)
            .Where(j => j.IsEnabled)
            .Where(j => !j.NextRunAt.HasValue || j.NextRunAt.Value <= now)
            .OrderBy(j => j.NextRunAt)
            .ToListAsync(ct);

        foreach (var job in dueJobs)
        {
            ct.ThrowIfCancellationRequested();
            await RunJobAsync(scope.ServiceProvider, job, ct);
        }
    }

    /// <summary>
    /// Schedules the next run at <c>now + interval</c>, spread by ±<see cref="ScheduleJitterFactor"/>.
    ///
    /// Without the jitter every job with the same interval converges: they all complete at roughly
    /// the same moment, all get the identical next-run time, and from then on fire together on one
    /// poll tick. Ten watched ships then queue ten scrapes behind the shared rate-limit gate at
    /// once, which is the load spike this is meant to avoid.
    /// </summary>
    internal static DateTime NextRunAfter(TimeSpan interval)
    {
        var multiplier = 1 + ((Random.Shared.NextDouble() * 2 - 1) * ScheduleJitterFactor);
        return DateTime.UtcNow + (interval * multiplier);
    }

    private async Task RunJobAsync(IServiceProvider services, ScrapeJob job, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var registry = services.GetRequiredService<ScraperRegistry>();

        var scraper = registry.TryGet(job.ScraperKey);
        if (scraper is null)
        {
            _logger.LogWarning("ScrapeJob {JobId} references unknown scraper key {ScraperKey}", job.Id, job.ScraperKey);
            job.NextRunAt = NextRunAfter(job.Interval);
            await db.SaveChangesAsync(ct);
            return;
        }

        // A ship still working through its back catalogue keeps backfilling; everything else
        // takes the cheap newest-first pass.
        var mode = job.Ship.BackfillState is ShipBackfillState.NotStarted or ShipBackfillState.InProgress
            ? ScrapeRunMode.Backfill
            : ScrapeRunMode.Incremental;

        if (!scraper.Supports(mode))
        {
            _logger.LogWarning(
                "Scraper {ScraperKey} does not support {Mode} for ScrapeJob {JobId}", job.ScraperKey, mode, job.Id);
            job.NextRunAt = NextRunAfter(job.Interval);
            await db.SaveChangesAsync(ct);
            return;
        }

        var run = new ScrapeRun
        {
            ScrapeJobId = job.Id,
            Status = ScrapeRunStatus.Running,
            Mode = mode,
            HeartbeatAt = DateTime.UtcNow,
        };
        db.ScrapeRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var budget = new ScrapeBudget(_httpOptions);

        try
        {
            var outcome = await scraper.ExecuteAsync(new ScrapeContext(job, job.Ship, mode, budget), ct);

            run.PagesFetched = outcome.PagesFetched;
            run.RequestsMade = outcome.RequestsMade;
            run.WorksSeen = outcome.WorksSeen;
            run.WorksAdded = outcome.WorksAdded;
            run.WorksUpdated = outcome.WorksUpdated;
            run.ParseWarnings = outcome.ParseWarnings;
            run.FirstPageFetched = outcome.FirstPage;
            run.LastPageFetched = outcome.LastPage;
            run.StopReason = outcome.StopReason;
            run.Status = ScrapeRunStatus.Succeeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ScrapeJob {JobId} ({ScraperKey}) failed", job.Id, job.ScraperKey);
            run.Status = ScrapeRunStatus.Failed;
            run.StopReason = ScrapeStopReason.Error;
            run.ErrorMessage = ex.Message;
        }
        finally
        {
            // Taken from the budget rather than the outcome: a run that threw part-way has no
            // outcome, and how much of its allowance it had already spent is exactly what the
            // run history needs to show.
            run.HitRequestCap = budget.HitRequestCap;
            run.HitTimeCap = budget.HitTimeCap;

            run.CompletedAt = DateTime.UtcNow;
            run.HeartbeatAt = run.CompletedAt;
            job.LastRunAt = run.CompletedAt;
            job.NextRunAt = NextRunAfter(job.Interval);
            await db.SaveChangesAsync(ct);
        }
    }
}
