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

    [Fact]
    public async Task Stamps_the_moment_a_revision_moved_and_leaves_it_alone_when_it_did_not()
    {
        // The column exists to be read as "everything fetched before this moment describes the
        // previous version" — which is how a download tells a cached work page apart from a current
        // one. Written on every pass instead of on every move it would be LastScrapedAt under
        // another name, and that reading would be false of an unchanged work the moment anything
        // re-scraped it.
        var shipId = await FollowAsync();

        _host.Clock.Now = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
        await _host.IngestAsync(shipId, Page(1, [Blurb(1, updatedAt: Jan(9))]).Html);

        Assert.Equal(_host.Clock.Now.UtcDateTime, (await WorkAsync()).UpdatedAtObservedAt);

        // Re-read at a later hour, same revision. Kudos and hits move on a pass like this; the
        // version does not, and neither may this.
        var firstSeen = _host.Clock.Now.UtcDateTime;
        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        await _host.IngestAsync(shipId, Page(1, [Blurb(1, updatedAt: Jan(9))]).Html);

        var unchanged = await WorkAsync();
        Assert.Equal(firstSeen, unchanged.UpdatedAtObservedAt);
        Assert.Equal(_host.Clock.Now.UtcDateTime, unchanged.LastScrapedAt);

        // The author posts a chapter. Now it moves, to the moment this instance saw it move rather
        // than to AO3's own stamp — they are different clocks and only ours can be compared against
        // when this instance fetched something.
        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        await _host.IngestAsync(shipId, Page(1, [Blurb(1, updatedAt: Jan(11))]).Html);

        Assert.Equal(_host.Clock.Now.UtcDateTime, (await WorkAsync()).UpdatedAtObservedAt);
    }

    [Fact]
    public async Task Does_not_stamp_a_revision_off_a_blurb_whose_date_could_not_be_read()
    {
        // The unreadable-date arm leaves UpdatedAt alone, so nothing moved and there is nothing to
        // record. Stamping here would say a revision was observed at a moment when the pass could
        // not read one, and a download would then re-read a page that was perfectly current.
        var shipId = await FollowAsync();

        _host.Clock.Now = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
        await _host.IngestAsync(shipId, Page(1, [Blurb(1, updatedAt: Jan(9))]).Html);

        var stamped = (await WorkAsync()).UpdatedAtObservedAt;

        _host.Clock.Now = _host.Clock.Now.AddHours(1);
        await _host.IngestAsync(shipId, Page(1, [Blurb(1, undated: true)]).Html);

        Assert.Equal(stamped, (await WorkAsync()).UpdatedAtObservedAt);
    }

    // ---- the day AO3 shows ---------------------------------------------------------------------

    [Fact]
    public async Task Stores_the_day_a_blurb_shows_and_moves_it_with_the_next_revision()
    {
        // RevisedOn is what a window AO3 drew gets compared against, so it follows the work — and it
        // is the visible day, never UpdatedAt's, which here sits eight days ahead of it.
        var shipId = await FollowAsync();

        await _host.IngestAsync(shipId, Page(1, [Blurb(1, updatedAt: Jan(11), revisedOn: new(2023, 1, 3))]).Html);
        Assert.Equal(new DateTime(2023, 1, 3, 0, 0, 0, DateTimeKind.Utc), (await WorkAsync()).RevisedOn);

        await _host.IngestAsync(shipId, Page(1, [Blurb(1, updatedAt: Jan(20), revisedOn: new(2023, 1, 12))]).Html);
        Assert.Equal(new DateTime(2023, 1, 12, 0, 0, 0, DateTimeKind.Utc), (await WorkAsync()).RevisedOn);
    }

    [Fact]
    public async Task An_unreadable_day_neither_invents_a_revision_day_nor_clears_a_stored_one()
    {
        var shipId = await FollowAsync();

        // First seen undated: null — not year 1, and not a day borrowed from another clock.
        await _host.IngestAsync(shipId, Page(1, [Blurb(1, undated: true)]).Html);
        Assert.Null((await WorkAsync()).RevisedOn);

        await _host.IngestAsync(shipId, Page(1, [Blurb(1, revisedOn: new(2023, 1, 9))]).Html);
        await _host.IngestAsync(shipId, Page(1, [Blurb(1, undated: true)]).Html);

        Assert.Equal(new DateTime(2023, 1, 9, 0, 0, 0, DateTimeKind.Utc), (await WorkAsync()).RevisedOn);
    }

    private async Task<Api.Models.Work> WorkAsync()
    {
        await using var db = _host.NewContext();
        return await db.Works.SingleAsync();
    }

    private async Task<int> FollowAsync()
    {
        var result = await _host.Ships(_host.SeedUser())
            .WatchShip(new AddWatchedShipRequest(Lexa), CancellationToken.None);

        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }
}
