using Ao3Tracker.Api.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Ao3Tracker.Tests.Ao3ListingFixtures;

namespace Ao3Tracker.Tests;

/// <summary>
/// What re-reading a work does to the one column the whole scheduler is built on.
///
/// <see cref="Ao3Tracker.Api.Models.Work.UpdatedAt"/> is AO3's revision timestamp, and a ship's
/// incremental watermark is derived from the newest one in the tag. A blurb whose date the parser
/// cannot read reports <c>DateTime.MinValue</c>, so writing it unconditionally would drag the
/// watermark back to year 1 and re-walk the entire tag on the next pass — the cost the whole
/// incremental pass exists to avoid. <c>WorkIngestor.Apply</c> guards against that with a single
/// <c>if</c>, and until now only its first-seen arm was pinned: nothing failed if a refactor
/// dropped the guard on a work already in the library.
/// </summary>
public class WorkIngestorTimestampTests : IDisposable
{
    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task An_unreadable_date_does_not_erase_the_one_an_earlier_pass_read()
    {
        var shipId = await FollowAsync();

        await _host.IngestAsync(shipId, Page(1, [Blurb(1, updatedAt: Jan(9))]).Html);
        await _host.IngestAsync(shipId, Page(1, [Blurb(1, undated: true)]).Html);

        await using var db = _host.NewContext();
        var work = await db.Works.SingleAsync();

        // The whole of the rule: what this pass could not read is not evidence about the work, so
        // the date Jan 9 was read on stands.
        Assert.Equal(Jan(9), work.UpdatedAt);

        // The flag beside it is *not* preserved — the same arm sets it, because it cannot tell a
        // work first seen undated from one seen again undated, and on the first the row would
        // otherwise assert 0001-01-01 as exact. The cost is stated here rather than left to be
        // discovered: a work whose date was read exactly and then re-seen undated stops claiming
        // second precision for it until the next pass that can read a date says otherwise, which
        // the test below is the other half of.
        Assert.True(work.UpdatedAtIsApproximate);
    }

    [Fact]
    public async Task A_work_first_seen_undated_takes_the_date_a_later_pass_can_read()
    {
        // The guard is one-directional, and must stay so: it withholds a MinValue, it does not
        // freeze the column. A work stuck at year 1 would sit below every watermark for ever.
        var shipId = await FollowAsync();

        await _host.IngestAsync(shipId, Page(1, [Blurb(1, undated: true)]).Html);
        await _host.IngestAsync(shipId, Page(1, [Blurb(1, updatedAt: Jan(9))]).Html);

        await using var db = _host.NewContext();
        var work = await db.Works.SingleAsync();

        Assert.Equal(Jan(9), work.UpdatedAt);
        Assert.False(work.UpdatedAtIsApproximate);
    }

    private async Task<int> FollowAsync()
    {
        var result = await _host.Ships(_host.SeedUser())
            .WatchShip(new AddWatchedShipRequest(Lexa), CancellationToken.None);

        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }
}
