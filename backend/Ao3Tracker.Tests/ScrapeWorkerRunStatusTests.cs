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
