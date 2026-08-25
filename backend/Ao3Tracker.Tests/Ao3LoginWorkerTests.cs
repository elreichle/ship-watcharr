using System.Net;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// What the worker does about the session its due jobs are supposed to scrape as.
///
/// The configuration gates answer "is a login stored"; this answers "does it work". They fail
/// differently and have to be held differently: a stored password AO3 refuses is not a
/// misconfiguration the settings screen can see, and burning the schedule against it would turn one
/// wrong password into a job history full of failures.
/// </summary>
public class Ao3LoginWorkerTests : IDisposable
{
    private readonly LibraryTestHost _host = new(new StubScraper(Ao3ScraperKeys.ShipIndex));

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Logs_in_before_running_the_jobs_that_are_due()
    {
        await FollowAsync();
        await _host.SaveAo3LoginAsync();

        await _host.NewScrapeWorker().RunDueJobsAsync(default);

        Assert.Single(_host.Http.Posted);
        Assert.Equal(ScrapeRunStatus.Succeeded, Assert.Single(await RunsAsync()).Status);
    }

    [Fact]
    public async Task Asks_AO3_for_nothing_when_no_job_is_due()
    {
        // An idle instance re-authenticating on a timer would be two requests an hour that read
        // nothing at all, which is exactly the load this scraper exists not to put on the archive.
        await _host.SaveAo3LoginAsync();

        await _host.NewScrapeWorker().RunDueJobsAsync(default);

        Assert.Empty(_host.Http.LoginPagesRequested);
        Assert.Empty(_host.Http.Posted);
    }

    [Fact]
    public async Task Holds_a_due_job_when_AO3_refuses_the_login()
    {
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            Fixtures.Load(Fixtures.LoginPage), HttpStatusCode.OK, FromCache: false, FinalUrl: url);

        await FollowAsync();
        await _host.SaveAo3LoginAsync();

        await _host.NewScrapeWorker().RunDueJobsAsync(default);

        await using var db = _host.NewContext();
        var job = await db.ScrapeJobs.SingleAsync();

        // Held, exactly as a missing login is: nothing attempted, so nothing to record and nothing
        // to back off from. The job stays due, which is what makes the next poll pick it up.
        Assert.Empty(await db.ScrapeRuns.ToListAsync());
        Assert.Null(job.NextRunAt);
        Assert.Null(job.LastRunAt);
        Assert.Empty(_host.Http.Requested);
    }

    [Fact]
    public async Task Leaves_AO3_alone_for_a_while_after_it_refuses_the_login()
    {
        // Without a cooldown this is two requests a minute for ever: the jobs stay due because a
        // held job is deliberately not advanced, so the next poll asks again, and a stored password
        // does not become correct by being retried. See Ao3LoginBackoff.
        _host.Http.RespondsToPost = RefusesTheLogin;

        await FollowAsync();
        await _host.SaveAo3LoginAsync();

        var worker = _host.NewScrapeWorker();
        await worker.RunDueJobsAsync(default);
        await worker.RunDueJobsAsync(default);
        await worker.RunDueJobsAsync(default);

        Assert.Single(_host.Http.Posted);
        Assert.Empty(await RunsAsync());
    }

    [Fact]
    public async Task Tries_again_once_the_cooldown_has_passed()
    {
        // The other half: a cooldown that never lifts is an instance that has stopped scraping.
        _host.Clock.Now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        _host.Http.RespondsToPost = RefusesTheLogin;

        await FollowAsync();
        await _host.SaveAo3LoginAsync();

        var worker = _host.NewScrapeWorker();
        await worker.RunDueJobsAsync(default);

        _host.Clock.Now = _host.Clock.Now.AddMinutes(10);
        _host.Http.RespondsToPost = AcceptsTheLogin;
        await worker.RunDueJobsAsync(default);

        Assert.Equal(ScrapeRunStatus.Succeeded, Assert.Single(await RunsAsync()).Status);
    }

    [Fact]
    public async Task Runs_the_held_job_on_the_next_poll_once_the_login_is_corrected()
    {
        // An operator who has just fixed the password must not have to wait out a backoff that was
        // measuring the old one — so saving the credential clears it and the next poll tries at
        // once. The same worker across both polls: one rebuilt in between would prove only that a
        // restart helps, which is the thing this must not require.
        _host.Http.RespondsToPost = RefusesTheLogin;

        await FollowAsync();
        await _host.SaveAo3LoginAsync();

        var worker = _host.NewScrapeWorker();
        await worker.RunDueJobsAsync(default);
        Assert.Empty(await RunsAsync());

        _host.Http.RespondsToPost = AcceptsTheLogin;
        await SaveTheLoginThroughTheAdminEndpointAsync();

        await worker.RunDueJobsAsync(default);

        Assert.Equal(ScrapeRunStatus.Succeeded, Assert.Single(await RunsAsync()).Status);
    }

    private static ScrapeHttpResponse RefusesTheLogin(string url) => new(
        Fixtures.Load(Fixtures.LoginPage), HttpStatusCode.OK, FromCache: false, FinalUrl: url);

    private static ScrapeHttpResponse AcceptsTheLogin(string url) => new(
        "", HttpStatusCode.Found, FromCache: false, FinalUrl: url,
        SetCookieHeaders: ["_otwarchive_session=logged-in; path=/"],
        Location: "https://ao3.test/users/shipwatcharr");

    /// <summary>
    /// Through the endpoint rather than the store, because clearing the cooldown is the endpoint's
    /// job — a test that wrote the credential directly would be asserting a reset nobody performs.
    /// </summary>
    private async Task SaveTheLoginThroughTheAdminEndpointAsync()
    {
        var admin = _host.SeedUser("admin", isAdmin: true);
        var result = await _host.AdminAo3Credential(admin)
            .SetCredential(new SetInstanceAo3CredentialRequest("shipwatcharr", "hunter2"), default);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Logs_in_once_and_reuses_the_session_on_later_polls()
    {
        await FollowAsync();
        await _host.SaveAo3LoginAsync();

        var worker = _host.NewScrapeWorker();
        await worker.RunDueJobsAsync(default);

        // Make the job due again, the way the passage of time does.
        await MakeDueAsync();
        await worker.RunDueJobsAsync(default);

        Assert.Equal(2, (await RunsAsync()).Count);
        Assert.Single(_host.Http.Posted);
    }

    private async Task FollowAsync()
    {
        var result = await _host.Ships(_host.SeedUser()).WatchShip(new("Clarke Griffin/Lexa"), default);
        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    private async Task MakeDueAsync()
    {
        await using var db = _host.NewContext();
        (await db.ScrapeJobs.SingleAsync()).NextRunAt = null;
        await db.SaveChangesAsync();
    }

    private async Task<List<ScrapeRun>> RunsAsync()
    {
        await using var db = _host.NewContext();
        return await db.ScrapeRuns.ToListAsync();
    }
}
