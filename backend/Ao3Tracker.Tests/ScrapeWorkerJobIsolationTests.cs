using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ao3Tracker.Tests;

/// <summary>
/// What one job's failure costs the rest of a poll.
///
/// A scrape that fails part-way leaves the change the database rejected tracked on the context it
/// was writing through. The worker still has to close that run out — mark it failed, advance the
/// job's schedule — and that write must not be the same rejected change set a second time. Getting
/// it wrong costs far more than one lost run: the run stays Running, NextRunAt is never advanced so
/// the job is due again on the very next minute-poll asking AO3 for the same page, and every job
/// behind it in the tick is skipped. A tight retry loop against the archive is the one thing this
/// app's politeness rules exist to prevent.
/// </summary>
public class ScrapeWorkerJobIsolationTests : IDisposable
{
    private readonly ScraperScopeLog _log = new();
    private readonly LibraryTestHost _host;

    public ScrapeWorkerJobIsolationTests() =>
        _host = new LibraryTestHost(services =>
        {
            services.AddSingleton(_log);

            // Scoped, unlike the singleton StubScraper the other worker tests use: this one has to
            // write through the very AppDbContext the worker resolved for the job, because a
            // poisoned change set left on *that* context is the failure under test.
            services.AddScoped<IAo3Scraper, PoisoningScraper>();
        });

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_job_whose_save_fails_is_recorded_as_failed_and_rescheduled()
    {
        var emma = _host.SeedUser();
        await FollowAsync(emma, PoisoningScraper.PoisonTag);
        await _host.SaveAo3LoginAsync();

        await _host.NewScrapeWorker().RunDueJobsAsync(default);

        await using var db = _host.NewContext();
        var run = await db.ScrapeRuns.SingleAsync();
        var job = await db.ScrapeJobs.SingleAsync();

        // Recorded as what it was. A run left Running is only ever closed out by startup
        // reconciliation, half an hour after a restart that may never come.
        Assert.Equal(ScrapeRunStatus.Failed, run.Status);
        Assert.Equal(ScrapeStopReason.Error, run.StopReason);
        Assert.NotNull(run.CompletedAt);
        Assert.NotNull(run.ErrorMessage);

        // The half AO3 pays for: a job still carrying its old NextRunAt is due again one minute
        // later, and asks for the same page again.
        Assert.NotNull(job.LastRunAt);
        Assert.NotNull(job.NextRunAt);
        Assert.True(job.NextRunAt > _host.Clock.Now.UtcDateTime);
    }

    [Fact]
    public async Task A_failed_job_does_not_skip_the_rest_of_the_poll()
    {
        var emma = _host.SeedUser();
        await FollowAsync(emma, PoisoningScraper.PoisonTag);
        await FollowAsync(emma, "Clarke Griffin/Lexa");
        await _host.SaveAo3LoginAsync();

        await _host.NewScrapeWorker().RunDueJobsAsync(default);

        await using var db = _host.NewContext();
        var jobs = await db.ScrapeJobs.Include(j => j.Ship).ToListAsync();
        var runs = await db.ScrapeRuns.ToListAsync();

        Assert.Equal(2, runs.Count);

        var healthy = jobs.Single(j => j.Ship.CanonicalTagName == "Clarke Griffin/Lexa");
        Assert.Equal(ScrapeRunStatus.Succeeded, runs.Single(r => r.ScrapeJobId == healthy.Id).Status);

        // Both rescheduled, whichever order the poll happened to take them in.
        Assert.All(jobs, j => Assert.NotNull(j.NextRunAt));
    }

    [Fact]
    public async Task Each_job_in_a_poll_gets_its_own_scope()
    {
        var emma = _host.SeedUser();
        await FollowAsync(emma, "Clarke Griffin/Lexa");
        await FollowAsync(emma, "Bellamy Blake/Clarke Griffin");
        await _host.SaveAo3LoginAsync();

        await _host.NewScrapeWorker().RunDueJobsAsync(default);

        // Two jobs, two AppDbContext instances. One context shared across the tick is what lets the
        // first job's rejected change set still be sitting there when the second one saves — and it
        // is what Program.cs already claims the worker does not do.
        Assert.Equal(2, _log.Contexts.Count);
        Assert.NotSame(_log.Contexts[0], _log.Contexts[1]);
    }

    private async Task FollowAsync(ApplicationUser user, string tagName)
    {
        var result = await _host.Ships(user).WatchShip(new(tagName), default);
        Assert.IsType<CreatedAtActionResult>(result.Result);
    }
}

/// <summary>Which AppDbContext each scraper execution in a poll was handed.</summary>
internal sealed class ScraperScopeLog
{
    public List<AppDbContext> Contexts { get; } = [];
}

/// <summary>
/// A scraper that leaves its job's context unable to save, the way a page whose ingest collides
/// does, and then fails the way that reaches the worker.
/// </summary>
internal sealed class PoisoningScraper(AppDbContext db, ScraperScopeLog log) : IAo3Scraper
{
    /// <summary>Follow this tag to get a job whose scrape fails. Any other tag scrapes cleanly.</summary>
    public const string PoisonTag = "Poison/Ship";

    public string Key => Ao3ScraperKeys.ShipIndex;

    public bool Supports(ScrapeRunMode mode) => true;

    public async Task<ScrapeOutcome> ExecuteAsync(ScrapeContext context, CancellationToken ct = default)
    {
        log.Contexts.Add(db);

        if (context.Ship.CanonicalTagName != PoisonTag) return ScrapeOutcome.Empty("stub");

        // Two ships under one normalized tag: a unique-index violation, which is the shape of the
        // real thing — WorkIngestor saving a page that collides on a tag or a pseud. EF Core does
        // not detach a change set the database refused, so these two stay Added afterwards and the
        // next save through this context fails identically.
        db.Ships.Add(NewShip("Poisoned/One"));
        db.Ships.Add(NewShip("Poisoned/One"));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException("Ingest failed part-way through a page.", ex);
        }

        return ScrapeOutcome.Empty("stub");
    }

    private static Ship NewShip(string tagName) => new()
    {
        CanonicalTagName = tagName,
        CanonicalTagNameNormalized = tagName.ToUpperInvariant(),
        TagUrlSegment = tagName.Replace("/", "*s*"),
    };
}
