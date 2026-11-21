using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Settings;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Tests;

/// <summary>
/// The admin's switch for stopping every request to AO3 while the site stays up. Made after AO3's
/// November 2026 outage, when the archive was limiting traffic and even a backed-off instance was
/// more than it wanted. A paused instance holds its work exactly as an unconfigured one does:
/// nothing attempted, nothing recorded, picked up again on the next poll after the resume.
/// </summary>
public class Ao3RequestPauseTests : IDisposable
{
    private readonly LibraryTestHost _host = new(new StubScraper(Ao3ScraperKeys.ShipIndex));

    private readonly string _dataDirectory =
        Directory.CreateTempSubdirectory("ship-watcharr-pause-").FullName;

    public void Dispose()
    {
        _host.Dispose();

        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the test is untidy, never a failure.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Holds_a_due_job_while_paused_and_runs_it_on_the_next_poll_after_the_resume()
    {
        var emma = _host.SeedUser("emma", isAdmin: true);
        await _host.SaveAo3LoginAsync();
        await FollowAsync(emma, "Clarke Griffin/Lexa");

        Identity(await _host.AdminScraping(emma).UpdatePause(new UpdateAo3PauseRequest(true), default));

        var worker = _host.NewScrapeWorker();
        await worker.RunDueJobsAsync(default);

        // Held, not failed, so a long pause costs the job nothing to come back from.
        await using (var db = _host.NewContext())
        {
            Assert.Empty(await db.ScrapeRuns.ToListAsync());
            Assert.Null((await db.ScrapeJobs.SingleAsync()).NextRunAt);
        }
        Assert.Empty(_host.Http.Requested);

        Identity(await _host.AdminScraping(emma).UpdatePause(new UpdateAo3PauseRequest(false), default));
        await worker.RunDueJobsAsync(default);

        await using (var db = _host.NewContext())
        {
            Assert.Equal(ScrapeRunStatus.Succeeded, Assert.Single(await db.ScrapeRuns.ToListAsync()).Status);
        }
    }

    [Fact]
    public async Task The_gate_reports_the_pause_as_its_own_blocker()
    {
        await _host.SaveAo3LoginAsync();
        _host.Gate.Pause(DateTimeOffset.UtcNow);

        var state = await _host.EvaluateScrapingGateAsync();

        Assert.False(state.CanScrape);
        Assert.Equal(ScrapingGate.PausedMessage, Assert.Single(state.Blockers));
        Assert.NotNull(state.PausedSince);

        // Paused is not unconfigured: the instance can still say who it is.
        Assert.True(state.IdentityConfigured);
        Assert.True(state.Ao3LoginConfigured);
    }

    [Fact]
    public async Task Every_user_is_told_on_the_ships_page()
    {
        var reader = _host.SeedUser("reader");
        _host.Gate.Pause(DateTimeOffset.UtcNow);

        var result = await _host.Ships(reader).GetWatchedShips(default);
        var ships = Assert.IsType<WatchedShipsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.NotNull(ships.Ao3PausedSince);
    }

    [Fact]
    public async Task Only_an_admin_may_pause()
    {
        var reader = _host.SeedUser("reader");

        var result = await _host.AdminScraping(reader).UpdatePause(new UpdateAo3PauseRequest(true), default);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Null(_host.Gate.PausedSince);
    }

    [Fact]
    public async Task A_pause_survives_a_restart_with_its_date_and_a_resume_clears_it()
    {
        var settings = Settings();
        var since = new DateTimeOffset(2026, 11, 21, 18, 30, 0, TimeSpan.Zero);

        await Pause(NewGate(), settings, since).PauseAsync();

        // A new gate and a new switch over the same settings.json: what a redeploy starts with.
        var restarted = NewGate();
        await Pause(restarted, settings, since.AddDays(3)).RestoreAsync();
        Assert.Equal(since, restarted.PausedSince);

        await Pause(restarted, settings, since.AddDays(3)).ResumeAsync();
        Assert.Null(restarted.PausedSince);

        var again = NewGate();
        await Pause(again, settings, since.AddDays(4)).RestoreAsync();
        Assert.Null(again.PausedSince);
    }

    [Fact]
    public async Task Pausing_again_keeps_the_first_date()
    {
        var settings = Settings();
        var gate = NewGate();
        var first = new DateTimeOffset(2026, 11, 21, 18, 30, 0, TimeSpan.Zero);

        await Pause(gate, settings, first).PauseAsync();
        await Pause(gate, settings, first.AddHours(5)).PauseAsync();

        Assert.Equal(first, gate.PausedSince);
        Assert.Equal(first, await settings.ReadAo3PausedSinceAsync());
    }

    private PersistedSettingsStore Settings() => new(new StoragePaths(
        _dataDirectory,
        Path.Combine(_dataDirectory, "test.db"),
        Path.Combine(_dataDirectory, "settings.json"),
        Path.Combine(_dataDirectory, "keys")));

    private static Ao3RateGate NewGate() => new(
        Options.Create(new Ao3HttpClientOptions()), TimeProvider.System, NullLogger<Ao3RateGate>.Instance);

    private static Ao3RequestPause Pause(Ao3RateGate gate, IPersistedSettingsStore settings, DateTimeOffset now) =>
        new(gate, settings, new FixedClock(now));

    private async Task FollowAsync(ApplicationUser user, string tagName)
    {
        var result = await _host.Ships(user).WatchShip(new(tagName), default);
        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    private static ScrapingIdentityDto Identity(ActionResult<ScrapingIdentityDto> result) =>
        Assert.IsType<ScrapingIdentityDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
