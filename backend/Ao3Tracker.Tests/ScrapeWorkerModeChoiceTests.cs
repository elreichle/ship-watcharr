using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// Which pass the worker chooses for a due job, off the ship's own backfill state.
///
/// This is the top of the scraper: everything a run does — which end of the listing it reads,
/// whether it may conclude a work has left the tag, what it costs AO3 — follows from the mode
/// chosen here, and three separate comments in <c>Ao3ShipIndexScraper</c> rest on the second half
/// of it. "The ship keeps its incremental pass, so it goes on collecting new works" is what makes
/// a written-off backfill survivable rather than a ship that silently stops updating; a state that
/// backfilled for ever instead would spend a full walk's requests on every tick.
///
/// The sweep is the third arm of the same choice and is covered by
/// <see cref="Ao3ShipIndexFullSweepTests"/>; every ship here has just been swept, so what is left
/// is the backfill-or-incremental decision alone.
/// </summary>
public class ScrapeWorkerModeChoiceTests
{
    [Theory]
    // A ship that has never read its listing, and one part-way through reading it, both owe the
    // same walk into the back catalogue — the second resuming from the cursor the first left.
    [InlineData(ShipBackfillState.NotStarted, ScrapeRunMode.Backfill)]
    [InlineData(ShipBackfillState.InProgress, ScrapeRunMode.Backfill)]
    // A finished back catalogue needs only the newest end of the listing from here on.
    [InlineData(ShipBackfillState.Complete, ScrapeRunMode.Incremental)]
    // And a backfill that was written off gets the same pass rather than retrying for ever: the
    // gap it left is a full sweep's job, and in the meantime the ship still collects new works.
    [InlineData(ShipBackfillState.Failed, ScrapeRunMode.Incremental)]
    public async Task The_pass_a_ship_gets_follows_from_its_backfill_state(
        ShipBackfillState state, ScrapeRunMode expected)
    {
        var (handedToTheScraper, recordedOnTheRun) = await RunOneAsync(state);

        // What the scraper was actually told to do.
        Assert.Equal([expected], handedToTheScraper);

        // And what the run history says it did. The two are one assignment apart in the worker, but
        // the run row is the only place a headless pass reports which end of the listing it read.
        Assert.Equal(expected, recordedOnTheRun);
    }

    /// <summary>
    /// One poll over a single followed ship in <paramref name="state"/>, with a stub scraper
    /// standing in for the walk: the modes it was handed, and the mode recorded on its run.
    /// </summary>
    private static async Task<(IReadOnlyList<ScrapeRunMode> Handed, ScrapeRunMode Recorded)> RunOneAsync(
        ShipBackfillState state)
    {
        var stub = new StubScraper(Ao3ScraperKeys.ShipIndex, ScrapeStopReason.Watermark);
        using var host = new LibraryTestHost(stub);

        var emma = host.SeedUser();
        var result = await host.Ships(emma).WatchShip(new("Clarke Griffin/Lexa"), default);
        Assert.IsType<CreatedAtActionResult>(result.Result);
        await host.SaveAo3LoginAsync();

        await using (var db = host.NewContext())
        {
            var ship = await db.Ships.SingleAsync();
            ship.BackfillState = state;

            // Swept just now, so no sweep is owed. Without this the arm under test is only reached
            // because a freshly followed ship is younger than the sweep interval — true today, and
            // not the thing these cases are about.
            ship.LastFullSweepStartedAt = host.Clock.Now.UtcDateTime;
            await db.SaveChangesAsync();
        }

        await host.NewScrapeWorker().RunDueJobsAsync(default);

        await using var read = host.NewContext();
        return (stub.ModesRun, (await read.ScrapeRuns.SingleAsync()).Mode);
    }
}
