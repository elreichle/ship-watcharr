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

    // ---- quiet tags ----------------------------------------------------------------------------

    [Fact]
    public async Task Stretches_the_interval_once_a_tag_has_been_quiet_and_snaps_back_when_it_moves()
    {
        await AQuietShipAsync();
        var worker = _host.NewScrapeWorker();

        // The plain interval after the first quiet pass; doubling from the second, to a cap.
        Assert.Equal(1, await WaitAfterNextRunAsync(worker));
        Assert.Equal(2, await WaitAfterNextRunAsync(worker));
        Assert.Equal(4, await WaitAfterNextRunAsync(worker));
        Assert.Equal(4, await WaitAfterNextRunAsync(worker));

        // A pass that found a work: the tag is moving, and the next check is at the plain interval.
        _scraper.Outcome = ScrapeOutcome.Empty(ScrapeStopReason.Watermark) with { WorksAdded = 1 };
        Assert.Equal(1, await WaitAfterNextRunAsync(worker));

        // And the count starts again from there.
        _scraper.Outcome = ScrapeOutcome.Empty(ScrapeStopReason.Watermark);
        Assert.Equal(1, await WaitAfterNextRunAsync(worker));
        Assert.Equal(2, await WaitAfterNextRunAsync(worker));
    }

    [Fact]
    public async Task A_failed_pass_neither_counts_as_quiet_nor_stretches()
    {
        await AQuietShipAsync();
        var worker = _host.NewScrapeWorker();

        Assert.Equal(1, await WaitAfterNextRunAsync(worker));
        Assert.Equal(2, await WaitAfterNextRunAsync(worker));

        // An error says nothing about the tag: the next wait is the plain interval, and the quiet
        // streak behind it is broken — the pass after it starts counting from one again.
        _scraper.Outcome = ScrapeOutcome.Empty(ScrapeStopReason.Error) with { ErrorMessage = "AO3 returned 503" };
        Assert.Equal(1, await WaitAfterNextRunAsync(worker));

        _scraper.Outcome = ScrapeOutcome.Empty(ScrapeStopReason.Watermark);
        Assert.Equal(1, await WaitAfterNextRunAsync(worker));
        Assert.Equal(2, await WaitAfterNextRunAsync(worker));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 4)]
    [InlineData(40, 4)]
    public void The_stretch_doubles_from_the_second_quiet_pass_and_stops_at_four(int quietInARow, int expected) =>
        Assert.Equal(expected, ScrapeWorker.QuietStretch(quietInARow));

    // ---- spreading -----------------------------------------------------------------------------

    [Fact]
    public void With_nobody_else_due_the_next_run_is_one_interval_out()
    {
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.AddHours(6), ScrapeWorker.SpreadWithin(Interval, now, []));
    }

    [Fact]
    public void The_next_run_lands_as_far_as_the_window_allows_from_another_ships()
    {
        // Another ship is due exactly an interval out. The whole point: this one does not join it.
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        var other = now.AddHours(6);

        var next = ScrapeWorker.SpreadWithin(Interval, now, [other]);

        Assert.InRange(next, now.AddHours(6 * 0.9), now.AddHours(6 * 1.1));
        Assert.Equal(TimeSpan.FromMinutes(36), (next - other).Duration());
    }

    [Fact]
    public void The_next_run_takes_the_largest_gap_between_the_others()
    {
        // Ships at both ends of the window: the middle is the emptiest place left.
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        var atStart = now.AddHours(6 * 0.9);
        var atEnd = now.AddHours(6 * 1.1);

        Assert.Equal(now.AddHours(6), ScrapeWorker.SpreadWithin(Interval, now, [atStart, atEnd]));
    }

    [Fact]
    public void A_ship_just_outside_the_window_still_pushes_the_run_the_other_way()
    {
        // Due a minute past the window's end: the far end of the window is the emptiest point.
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        var justPast = now.AddHours(6 * 1.1).AddMinutes(1);

        Assert.Equal(now.AddHours(6 * 0.9), ScrapeWorker.SpreadWithin(Interval, now, [justPast]));
    }

    [Fact]
    public async Task A_finished_run_is_put_back_where_the_other_ships_are_not()
    {
        await AQuietShipAsync();
        var now = _host.Clock.Now.UtcDateTime;

        // A second ship, already scheduled for exactly an interval from now and not due.
        await using (var db = _host.NewContext())
        {
            var emma = await db.Users.SingleAsync();
            var result = await _host.Ships(emma).WatchShip(new("Korra/Asami Sato"), default);
            Assert.IsType<CreatedAtActionResult>(result.Result);
        }

        await using (var db = _host.NewContext())
        {
            var other = await db.ScrapeJobs.OrderBy(j => j.Id).LastAsync();
            other.NextRunAt = now.AddHours(6);
            var ship = await db.Ships.SingleAsync(s => s.Id == other.ShipId);
            ship.BackfillState = ShipBackfillState.Complete;
            ship.LastFullSweepStartedAt = now;
            await db.SaveChangesAsync();
        }

        await _host.NewScrapeWorker().RunDueJobsAsync(default);

        await using var after = _host.NewContext();
        var jobs = await after.ScrapeJobs.OrderBy(j => j.Id).ToListAsync();
        Assert.Single(_scraper.Contexts);

        var gap = (jobs[0].NextRunAt!.Value - jobs[1].NextRunAt!.Value).Duration();
        Assert.Equal(TimeSpan.FromMinutes(36), gap);
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
