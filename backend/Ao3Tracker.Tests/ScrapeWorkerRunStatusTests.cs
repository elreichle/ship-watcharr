using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// What status the worker writes for a run the scraper reported on rather than threw out of.
///
/// Most of the ways a pass fails are *returned*, not thrown: a 404 on the first page requested, a
/// non-OK status mid-walk, a page no blurb on which could be dated. Each stops the run with
/// <see cref="ScrapeStopReason.Error"/> and an ErrorMessage. The run history is the only place a
/// headless worker reports itself, so recording one of those as Succeeded hides exactly the runs
/// an operator went looking for.
/// </summary>
public class ScrapeWorkerRunStatusTests
{
    [Fact]
    public async Task A_run_the_scraper_reported_an_error_for_is_recorded_as_failed()
    {
        using var host = new LibraryTestHost(
            new StubScraper(Ao3ScraperKeys.ShipIndex, ScrapeStopReason.Error, "AO3 returned 403 for page 1."));

        var run = await RunOneAsync(host);

        Assert.Equal(ScrapeRunStatus.Failed, run.Status);
        Assert.Equal(ScrapeStopReason.Error, run.StopReason);
        Assert.Equal("AO3 returned 403 for page 1.", run.ErrorMessage);
    }

    [Fact]
    public async Task A_run_that_stopped_normally_is_still_recorded_as_succeeded()
    {
        // The other side of the rule: stopping at the watermark is the healthy end of an
        // incremental pass, not a failure, and must not start reading as one.
        using var host = new LibraryTestHost(
            new StubScraper(Ao3ScraperKeys.ShipIndex, ScrapeStopReason.Watermark));

        var run = await RunOneAsync(host);

        Assert.Equal(ScrapeRunStatus.Succeeded, run.Status);
        Assert.Null(run.ErrorMessage);
    }

    [Fact]
    public async Task A_run_that_held_a_page_rather_than_asking_for_it_is_recorded_as_failed()
    {
        // A held run read and ingested everything up to the page it stopped at, so it is not nothing
        // — but it did not get through the listing, and the run history is the only place a headless
        // worker reports itself. Filed as a success it would hide the one ship on the instance that
        // needs looking at among the healthy ones, and the streak that timed the hold is read back
        // out of these same rows.
        using var host = new LibraryTestHost(
            new StubScraper(
                Ao3ScraperKeys.ShipIndex,
                ScrapeStopReason.Held,
                "Page 2 has not answered for the last 5 runs; it was not requested this run"));

        var run = await RunOneAsync(host);

        Assert.Equal(ScrapeRunStatus.Failed, run.Status);
        Assert.Equal(ScrapeStopReason.Held, run.StopReason);
        Assert.Equal("Page 2 has not answered for the last 5 runs; it was not requested this run", run.ErrorMessage);
    }

    [Fact]
    public async Task A_run_for_a_tag_AO3_has_denied_is_recorded_as_failed()
    {
        // It made no request and cannot make one until the tag is verified again, so there is
        // nothing about it a success would be describing — and the ship it is for is one an
        // operator has to act on, which is the whole reason this column is not always Succeeded.
        using var host = new LibraryTestHost(
            new StubScraper(
                Ao3ScraperKeys.ShipIndex,
                ScrapeStopReason.Denied,
                "AO3 has denied the tag Clarke Griffin/Lexa; no page of it was requested."));

        var run = await RunOneAsync(host);

        Assert.Equal(ScrapeRunStatus.Failed, run.Status);
        Assert.Equal(ScrapeStopReason.Denied, run.StopReason);
    }

    private static async Task<ScrapeRun> RunOneAsync(LibraryTestHost host)
    {
        var emma = host.SeedUser();
        var result = await host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default);
        Assert.IsType<CreatedAtActionResult>(result.Result);
        await host.SaveAo3LoginAsync();

        await host.NewScrapeWorker().RunDueJobsAsync(default);

        await using var db = host.NewContext();
        return await db.ScrapeRuns.SingleAsync();
    }
}
