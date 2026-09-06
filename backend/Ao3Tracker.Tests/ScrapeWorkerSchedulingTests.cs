using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// When the worker puts a job back for, beyond "one interval from now".
///
/// Two departures from the plain interval, both about spending the instance's one outbound
/// channel where it counts. A run AO3 throttled comes back just past the wait AO3 named, not six
/// hours later — the interval was what a new ship's first page cost when it drew a 429, and one
/// penalty window took fifteen ships that way. And a ship whose tag has gone quiet is checked
/// less often, so the channel goes to the ships that are moving.
/// </summary>
public class ScrapeWorkerSchedulingTests : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly OutcomeScraper _scraper = new();
    private readonly LibraryTestHost _host;

    public ScrapeWorkerSchedulingTests() => _host = new LibraryTestHost(_scraper);

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- throttling ----------------------------------------------------------------------------

    [Fact]
    public async Task A_throttled_run_comes_back_just_past_the_hold_rather_than_an_interval_later()
    {
        await AQuietShipAsync();
        _host.Gate.Hold(TimeSpan.FromMinutes(5));
        _scraper.Outcome = ScrapeOutcome.Empty(ScrapeStopReason.Throttled) with
        {
            ErrorMessage = "AO3 asked this instance to slow down (429)",
        };

        var now = _host.Clock.Now.UtcDateTime;
        await _host.NewScrapeWorker().RunDueJobsAsync(default);

        await using var db = _host.NewContext();
        var run = await db.ScrapeRuns.SingleAsync();
        var job = await db.ScrapeJobs.SingleAsync();

        // Recorded, and as the failure it is: the run history is where an operator sees AO3
        // throttling the instance.
        Assert.Equal(ScrapeRunStatus.Failed, run.Status);
        Assert.Equal(ScrapeStopReason.Throttled, run.StopReason);

        AssertWithin(TimeSpan.FromMinutes(5), job.NextRunAt!.Value - now);
    }

    [Fact]
    public async Task A_due_job_is_deferred_untouched_while_AO3_has_asked_for_more_quiet_than_a_request_waits()
    {
        // Thirty minutes asked, fifteen the most a request holds itself open for. Running the job
        // would park its first request at the gate for the rest of the hold; deferring is the same
        // wait, with nothing attempted and nothing recorded.
        await AQuietShipAsync();
        _host.Gate.Hold(TimeSpan.FromMinutes(30));

        var now = _host.Clock.Now.UtcDateTime;
        await _host.NewScrapeWorker().RunDueJobsAsync(default);

        await using var db = _host.NewContext();
        Assert.Empty(await db.ScrapeRuns.ToListAsync());
        Assert.Empty(_scraper.Contexts);

        var job = await db.ScrapeJobs.SingleAsync();
        AssertWithin(TimeSpan.FromMinutes(30), job.NextRunAt!.Value - now);
    }

    [Fact]
    public async Task A_hold_a_request_can_wait_out_itself_does_not_defer_the_job()
    {
        // The other side of the ceiling: five minutes is inside what a request waits for, and the
        // gate sees to that wait. The job runs.
        await AQuietShipAsync();
        _host.Gate.Hold(TimeSpan.FromMinutes(5));

        await _host.NewScrapeWorker().RunDueJobsAsync(default);

        Assert.Single(_scraper.Contexts);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>
    /// Runs the one due job and answers how many intervals away it was put back, to the nearest
    /// whole one — the ±10% spread never reaches the next multiple. Moves the clock to just past the
    /// new due time, so the next call finds the job due again.
    /// </summary>
    private async Task<int> WaitAfterNextRunAsync(ScrapeWorker worker)
    {
        var before = _scraper.Contexts.Count;
        var now = _host.Clock.Now.UtcDateTime;

        await worker.RunDueJobsAsync(default);
        Assert.Equal(before + 1, _scraper.Contexts.Count);

        await using var db = _host.NewContext();
        var job = await db.ScrapeJobs.SingleAsync();
        var wait = job.NextRunAt!.Value - now;

        _host.Clock.Now = new DateTimeOffset(job.NextRunAt.Value, TimeSpan.Zero).AddSeconds(1);

        return (int)Math.Round(wait / Interval);
    }

    /// <summary>
    /// A followed ship past its backfill and freshly swept, so every run here is an incremental
    /// pass, with the scraper answering "nothing new" until a test says otherwise.
    /// </summary>
    private async Task AQuietShipAsync()
    {
        var emma = _host.SeedUser();
        var result = await _host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default);
        Assert.IsType<CreatedAtActionResult>(result.Result);
        await _host.SaveAo3LoginAsync();

        await using var db = _host.NewContext();
        var ship = await db.Ships.SingleAsync();
        ship.BackfillState = ShipBackfillState.Complete;
        ship.LastFullSweepStartedAt = _host.Clock.Now.UtcDateTime;
        await db.SaveChangesAsync();

        _scraper.Outcome = ScrapeOutcome.Empty(ScrapeStopReason.Watermark);
    }

    private static void AssertWithin(TimeSpan expected, TimeSpan actual)
    {
        // NextRunAfter spreads by ±10%; anything inside that is the expected wait.
        Assert.InRange(actual, expected * 0.89, expected * 1.11);
    }

    /// <summary>A scraper answering whatever a test last told it to, and remembering what it was asked.</summary>
    private sealed class OutcomeScraper : IAo3Scraper
    {
        public ScrapeOutcome Outcome { get; set; } = ScrapeOutcome.Empty(ScrapeStopReason.Watermark);

        public List<ScrapeContext> Contexts { get; } = [];

        public string Key => Ao3ScraperKeys.ShipIndex;

        public bool Supports(ScrapeRunMode mode) => true;

        public Task<ScrapeOutcome> ExecuteAsync(ScrapeContext context, CancellationToken ct = default)
        {
            Contexts.Add(context);
            return Task.FromResult(Outcome);
        }
    }
}
