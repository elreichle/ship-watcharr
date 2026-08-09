using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Credentials;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// In-process job scheduler: polls due ScrapeJobs on a fixed tick, runs each one through
/// its configured IAo3Scraper, and persists a ScrapeRun + ScrapedItems per execution.
/// This is a BackgroundService (not an OS-level service) because everything — API and
/// worker — ships as one container/process.
/// </summary>
public class ScrapeWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScrapeWorker> _logger;

    public ScrapeWorker(IServiceScopeFactory scopeFactory, ILogger<ScrapeWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Scrape worker starting, polling every {PollInterval}", PollInterval);

        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await RunDueJobsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unhandled error while polling scrape jobs");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunDueJobsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTimeOffset.UtcNow;
        var dueJobs = await db.ScrapeJobs
            .Include(j => j.User)
            .Where(j => j.IsEnabled && (j.NextRunAt == null || j.NextRunAt <= now))
            .ToListAsync(ct);

        foreach (var job in dueJobs)
        {
            ct.ThrowIfCancellationRequested();
            await RunJobAsync(scope.ServiceProvider, job, ct);
        }
    }

    private async Task RunJobAsync(IServiceProvider services, ScrapeJob job, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var registry = services.GetRequiredService<ScraperRegistry>();
        var credentialStore = services.GetRequiredService<IAo3CredentialStore>();

        var scraper = registry.TryGet(job.ScraperKey);
        if (scraper is null)
        {
            _logger.LogWarning("ScrapeJob {JobId} references unknown scraper key {ScraperKey}", job.Id, job.ScraperKey);
            job.NextRunAt = DateTimeOffset.UtcNow + job.Interval;
            await db.SaveChangesAsync(ct);
            return;
        }

        var run = new ScrapeRun { ScrapeJobId = job.Id, Status = ScrapeRunStatus.Running };
        db.ScrapeRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            var credential = await credentialStore.GetDecryptedCredentialAsync(job.UserId, ct);
            var results = await scraper.ScrapeAsync(job.User, credential, ct);

            foreach (var result in results)
            {
                run.Items.Add(new ScrapedItem
                {
                    ScrapeRunId = run.Id,
                    SourceUrl = result.SourceUrl,
                    Title = result.Title,
                    PayloadJson = result.PayloadJson,
                });
            }

            run.Status = ScrapeRunStatus.Succeeded;
            run.ItemsScraped = results.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ScrapeJob {JobId} ({ScraperKey}) failed", job.Id, job.ScraperKey);
            run.Status = ScrapeRunStatus.Failed;
            run.ErrorMessage = ex.Message;
        }
        finally
        {
            run.CompletedAt = DateTimeOffset.UtcNow;
            job.LastRunAt = run.CompletedAt;
            job.NextRunAt = DateTimeOffset.UtcNow + job.Interval;
            await db.SaveChangesAsync(ct);
        }
    }
}
