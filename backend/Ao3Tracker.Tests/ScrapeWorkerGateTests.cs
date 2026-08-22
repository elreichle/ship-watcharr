using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// What the scrape worker does about a due job it is not allowed to run yet.
///
/// Two gates stand in front of every request this app makes: an honest User-Agent, which needs an
/// operator contact, and the deployment's AO3 login. A fresh install has neither, and the worker's
/// job then is to hold — leave the job due, record nothing, ask AO3 for nothing — and to pick it up
/// on the next poll once the missing piece is saved, without a restart.
/// </summary>
public class ScrapeWorkerGateTests : IDisposable
{
    // A stub under the ship-index key, so a job that does run leaves a ScrapeRun behind without
    // anything being parsed or fetched. What is under test is whether it runs at all.
    private readonly LibraryTestHost _host = new(new StubScraper(Ao3ScraperKeys.ShipIndex));

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Holds_a_due_job_when_no_ao3_login_is_stored()
    {
        var emma = _host.SeedUser();
        await FollowAsync(emma, "Clarke Griffin/Lexa");

        await _host.NewScrapeWorker().RunDueJobsAsync(default);

        await using var db = _host.NewContext();
        var job = await db.ScrapeJobs.SingleAsync();

        // Held, not failed: nothing was attempted, so there is nothing to record and nothing to
        // back off from. The job stays due, which is what makes the next poll pick it up.
        Assert.Empty(await db.ScrapeRuns.ToListAsync());
        Assert.Null(job.NextRunAt);
        Assert.Null(job.LastRunAt);
        Assert.Empty(_host.Http.Requested);
    }

    [Fact]
    public async Task Runs_the_held_job_on_the_next_poll_once_a_login_is_saved()
    {
        var emma = _host.SeedUser();
        await FollowAsync(emma, "Clarke Griffin/Lexa");

        // The same worker across both polls. A worker rebuilt in between would prove only that a
        // restart helps, which is the thing this must not require.
        var worker = _host.NewScrapeWorker();
        await worker.RunDueJobsAsync(default);
        Assert.Empty(await CountRunsAsync());

        await _host.SaveAo3LoginAsync();
        await worker.RunDueJobsAsync(default);

        var runs = await CountRunsAsync();
        Assert.Equal(ScrapeRunStatus.Succeeded, Assert.Single(runs).Status);
    }

    [Fact]
    public async Task Holds_a_due_job_when_the_operator_contact_is_missing()
    {
        // The older gate, still enforced independently: a stored login does not buy an instance the
        // right to make an unattributable request.
        var emma = _host.SeedUser();
        await FollowAsync(emma, "Clarke Griffin/Lexa");
        await _host.SaveAo3LoginAsync();
        _host.OperatorContact = null;

        await _host.NewScrapeWorker().RunDueJobsAsync(default);

        Assert.Empty(await CountRunsAsync());
    }

    [Fact]
    public async Task Reports_both_reasons_when_both_are_missing()
    {
        _host.OperatorContact = null;

        var state = await _host.EvaluateScrapingGateAsync();

        Assert.False(state.CanScrape);
        Assert.False(state.IdentityConfigured);
        Assert.False(state.Ao3LoginConfigured);

        // Both at once. Reporting only the first would make fixing the contact look like progress
        // that changed nothing, with the second reason appearing only afterwards.
        Assert.Equal(2, state.Blockers.Count);
        Assert.Contains(ScrapingGate.NoAo3LoginMessage, state.Blockers);
        Assert.Contains(state.Blockers, b => b.Contains("contact", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(state.Problem);
    }

    [Fact]
    public async Task Reports_only_the_missing_login_when_the_identity_is_fine()
    {
        var state = await _host.EvaluateScrapingGateAsync();

        Assert.False(state.CanScrape);
        Assert.True(state.IdentityConfigured);
        Assert.False(state.Ao3LoginConfigured);
        Assert.Equal(ScrapingGate.NoAo3LoginMessage, Assert.Single(state.Blockers));

        // The User-Agent is still known and still honest — being held is not the same as being
        // unidentifiable, and the admin screen shows both facts separately.
        Assert.NotNull(state.UserAgent);
    }

    [Fact]
    public async Task Opens_once_both_gates_pass()
    {
        await _host.SaveAo3LoginAsync();

        var state = await _host.EvaluateScrapingGateAsync();

        Assert.True(state.CanScrape);
        Assert.Empty(state.Blockers);
        Assert.Null(state.Problem);
    }

    [Fact]
    public async Task The_admin_identity_endpoint_reports_the_gate_the_worker_enforces()
    {
        // T3 renders the login form and the held-scraping banner off this endpoint; it has to agree
        // with what is actually holding the jobs.
        var emma = _host.SeedUser("emma", isAdmin: true);

        var before = Identity(await _host.AdminScraping(emma).GetIdentity(default));
        Assert.False(before.ScrapingEnabled);
        Assert.True(before.IdentityConfigured);
        Assert.False(before.Ao3LoginConfigured);
        Assert.Equal(ScrapingGate.NoAo3LoginMessage, before.Problem);

        await _host.SaveAo3LoginAsync();

        var after = Identity(await _host.AdminScraping(emma).GetIdentity(default));
        Assert.True(after.ScrapingEnabled);
        Assert.True(after.Ao3LoginConfigured);
        Assert.Null(after.Problem);
    }

    private async Task FollowAsync(ApplicationUser user, string tagName)
    {
        var result = await _host.Ships(user).WatchShip(new(tagName), default);
        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    private async Task<List<ScrapeRun>> CountRunsAsync()
    {
        await using var db = _host.NewContext();
        return await db.ScrapeRuns.ToListAsync();
    }

    private static ScrapingIdentityDto Identity(ActionResult<ScrapingIdentityDto> result) =>
        Assert.IsType<ScrapingIdentityDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
}
