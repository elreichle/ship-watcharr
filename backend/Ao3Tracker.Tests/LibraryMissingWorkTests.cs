using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// What <see cref="ShipWork.MissingSinceAt"/> means to a reader: a work a completed full sweep no
/// longer found under a tag leaves the listings — the feed, a saved set's match count, the
/// statistics — while staying reachable by id, so the reader who rated, noted or downloaded it
/// keeps everything of theirs.
///
/// The mark is soft and reversible and a sweep can write it wrongly, which is why the two halves
/// are tested against each other here rather than one endpoint at a time: nearly every assertion
/// in this file is about a work being in one place and not another at the same moment.
/// </summary>
public class LibraryMissingWorkTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";
    private const string Kirk = "Kirk/Spock";

    /// <summary>When the sweep that stopped finding these works concluded.</summary>
    private static readonly DateTime Swept = new(2026, 8, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- the listings ----------------------------------------------------------------------------

    [Fact]
    public async Task Drops_a_work_that_left_the_only_tag_you_follow_it_under()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync(Lexa, emma);
        await SeedWorksAsync(lexa, 1, 2);
        await LeftAsync(lexa, 2);

        var page = Works(await _host.Works(emma).GetWorks(ct: default));

        Assert.Equal([1], page.Items.Select(w => w.Id));
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Keeps_a_work_that_still_appears_under_another_tag_you_follow()
    {
        // The mark is per membership. A crossover that lost one of its two relationship tags is
        // still in the library through the other, and the row says which one let go.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync(Lexa, emma);
        var kirk = await SeedShipAsync(Kirk, emma);
        await SeedWorksAsync(lexa, 1);
        await LinkAsync(kirk, 1);
        await LeftAsync(lexa, 1);

        var work = Assert.Single(Works(await _host.Works(emma).GetWorks(ct: default)).Items);

        Assert.Equal([Kirk], work.Ships);
        Assert.Equal([Lexa], work.LeftShips);
    }

    [Fact]
    public async Task Drops_it_from_the_feed_narrowed_to_the_tag_it_left()
    {
        // Same work, same moment: present under one ship's dropdown and absent under the other's.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync(Lexa, emma);
        var kirk = await SeedShipAsync(Kirk, emma);
        await SeedWorksAsync(lexa, 1);
        await LinkAsync(kirk, 1);
        await LeftAsync(lexa, 1);

        Assert.Empty(Works(await _host.Works(emma).GetWorks(shipId: lexa, ct: default)).Items);
        Assert.Equal([1], Works(await _host.Works(emma).GetWorks(shipId: kirk, ct: default))
            .Items.Select(w => w.Id));
    }

    [Fact]
    public async Task Keeps_a_departed_work_you_have_marked_and_says_which_tag_let_go()
    {
        // The false-positive answer: a sweep's mark can be wrong, so it never takes off the screen
        // a work this reader has said something about — and the chip is the only thing on the row
        // that explains why a work no longer in the tag is still listed.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync(Lexa, emma);
        await SeedWorksAsync(lexa, 1);
        await LeftAsync(lexa, 1);

        Assert.Empty(Works(await _host.Works(emma).GetWorks(ct: default)).Items);

        await _host.NewWorksRequest(emma).SetWorkState(1, new(Status: nameof(ReadingStatus.Read)), default);

        var work = Assert.Single(Works(await _host.Works(emma).GetWorks(ct: default)).Items);
        Assert.Empty(work.Ships);
        Assert.Equal([Lexa], work.LeftShips);
    }

    [Fact]
    public async Task Keeps_a_marked_work_in_the_feed_narrowed_to_the_tag_it_left()
    {
        // The retention is uniform: a mark keeps the work wherever the mark would otherwise have
        // hidden it, the tag's own feed included. Withholding it there while the unnarrowed feed
        // showed it would make the ship dropdown a way of losing your own history.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync(Lexa, emma);
        await SeedWorksAsync(lexa, 1);
        await LeftAsync(lexa, 1);
        await _host.NewWorksRequest(emma).SetWorkState(1, new(Status: nameof(ReadingStatus.Read)), default);

        var work = Assert.Single(Works(await _host.Works(emma).GetWorks(shipId: lexa, ct: default)).Items);

        Assert.Equal([Lexa], work.LeftShips);
    }

    [Fact]
    public async Task Forgets_it_again_when_the_reader_clears_what_they_had_said()
    {
        // A state emptied is stored as no row at all, which is the whole of the retention test —
        // so clearing one has to put the work back out of the library, not leave it stranded there.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync(Lexa, emma);
        await SeedWorksAsync(lexa, 1);
        await LeftAsync(lexa, 1);

        var works = _host.NewWorksRequest(emma);
        await works.SetWorkState(1, new(Rating: 8), default);
        await works.SetWorkState(1, new(), default);

        Assert.Empty(Works(await _host.Works(emma).GetWorks(ct: default)).Items);
    }

    [Fact]
    public async Task Counts_a_saved_set_over_the_works_that_set_lists()
    {
        // A count that disagrees with the list it links to is worse than no count, and this is the
        // narrowing most able to make them disagree: it is applied before the set's own criteria.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync(Lexa, emma);
        await SeedWorksAsync(lexa, w => w.IsComplete = true, 1, 2);
        await LeftAsync(lexa, 2);

        var created = Assert.IsType<SavedFilterDto>(Assert.IsType<CreatedAtActionResult>(
            (await _host.SavedFilters(emma).CreateFilter(new("Finished", IsComplete: true), default)).Result).Value);

        var listed = Works(await _host.Works(emma).GetWorks(savedFilterId: created.Id, ct: default));

        Assert.Equal(1, created.MatchingWorkCount);
        Assert.Equal([1], listed.Items.Select(w => w.Id));
    }

    [Fact]
    public async Task Leaves_a_departed_work_out_of_the_statistics()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync(Lexa, emma);
        await SeedWorksAsync(lexa, w => w.WordCount = 100, 1, 2);
        await LeftAsync(lexa, 2);

        var stats = await StatsAsync(emma);

        Assert.Equal(1, stats.Corpus.WorkCount);
        Assert.Equal(100, stats.Corpus.WordCount);
        Assert.Equal(1, stats.Ships.Single().WorkCount);
    }

    [Fact]
    public async Task Counts_a_crossover_under_the_tag_that_still_carries_it_and_no_other()
    {
        // The work is in the library through the tag it kept, so the statistics have it — but a
        // figure under the tag it left would be one the feed narrowed to that tag contradicts.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync(Lexa, emma);
        var kirk = await SeedShipAsync(Kirk, emma);
        await SeedWorksAsync(lexa, w => w.WordCount = 100, 1);
        await LinkAsync(kirk, 1);
        await LeftAsync(lexa, 1);

        var stats = await StatsAsync(emma);

        // A row per watched ship either way — a ship with nothing left in it says zero rather than
        // vanishing off the page.
        Assert.Equal(1, stats.Corpus.WorkCount);
        Assert.Equal([(Lexa, 0), (Kirk, 1)], stats.Ships.Select(s => (s.TagName, s.WorkCount)));
        Assert.Equal(0, (await StatsAsync(emma, lexa)).Ships.Single().WorkCount);
    }

    [Fact]
    public async Task Counts_a_marked_work_under_the_tag_it_left_because_that_feed_lists_it()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync(Lexa, emma);
        var kirk = await SeedShipAsync(Kirk, emma);
        await SeedWorksAsync(lexa, w => w.WordCount = 100, 1);
        await LinkAsync(kirk, 1);
        await LeftAsync(lexa, 1);
        await _host.NewWorksRequest(emma).SetWorkState(1, new(Status: nameof(ReadingStatus.Read)), default);

        var stats = await StatsAsync(emma);

        Assert.Equal([(Lexa, 1), (Kirk, 1)], stats.Ships.Select(s => (s.TagName, s.WorkCount)));
    }

    // ---- still reachable ---------------------------------------------------------------------------

    [Fact]
    public async Task Opens_the_work_it_dropped_and_names_the_tag_and_the_date()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync(Lexa, emma);
        await SeedWorksAsync(lexa, 1);
        await LeftAsync(lexa, 1);

        var detail = Assert.IsType<WorkDetailDto>(Assert.IsType<OkObjectResult>(
            (await _host.NewWorksRequest(emma).GetWork(1, default)).Result).Value);

        Assert.Equal([new WorkShipDto(lexa, Lexa, Swept)], detail.Ships);
    }

    [Fact]
    public async Task Still_refuses_a_work_under_no_tag_you_follow()
    {
        // The scoping the split must not have loosened: reachable means "a ship you watch carries
        // it, or did", never "any work this instance holds".
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        await SeedShipAsync(Lexa, emma);
        await SeedWorksAsync(await SeedShipAsync(Kirk, sam), 1);

        Assert.IsType<NotFoundResult>((await _host.NewWorksRequest(emma).GetWork(1, default)).Result);
        Assert.IsType<NotFoundResult>((await _host.NewWorksRequest(emma).GetWorkState(1, default)).Result);
    }

    [Fact]
    public async Task Still_takes_a_state_write_and_a_download_request_for_a_work_it_dropped()
    {
        // What the one-clause narrowing of the shared query would have cost: the reader who had
        // rated and downloaded a work would have been 404ed off their own data, on the strength of
        // a mark a sweep can get wrong.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync(Lexa, emma);
        await SeedWorksAsync(lexa, 1);
        await LeftAsync(lexa, 1);

        var state = Assert.IsType<WorkStateDto>(Assert.IsType<OkObjectResult>(
            (await _host.NewWorksRequest(emma).SetWorkState(1, new(Rating: 9), default)).Result).Value);
        Assert.Equal(9, state.Rating);

        var queued = Assert.IsType<DownloadDto>(Assert.IsType<OkObjectResult>(
            (await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default)).Result).Value);
        Assert.Equal(1, queued.WorkId);
    }

    // ---- fixture -------------------------------------------------------------------------------

    private async Task<StatsDto> StatsAsync(ApplicationUser user, int? shipId = null) =>
        Assert.IsType<StatsDto>(Assert.IsType<OkObjectResult>(
            (await _host.Stats(user).GetStats(shipId, default)).Result).Value);

    /// <summary>Marks a work as one a completed sweep of that ship's tag no longer found.</summary>
    private async Task LeftAsync(int shipId, long workId)
    {
        await using var db = _host.NewContext();

        var link = await db.ShipWorks.SingleAsync(sw => sw.ShipId == shipId && sw.WorkId == workId);
        link.MissingSinceAt = Swept;

        await db.SaveChangesAsync();
    }

    /// <summary>Creates a ship through the real endpoint, so its schedule is wired up too.</summary>
    private async Task<int> SeedShipAsync(string tagName, ApplicationUser watcher)
    {
        var result = await _host.Ships(watcher).WatchShip(new(tagName), default);
        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    private Task SeedWorksAsync(int shipId, params long[] ids) => SeedWorksAsync(shipId, _ => { }, ids);

    private async Task SeedWorksAsync(int shipId, Action<Work> customize, params long[] ids)
    {
        await using var db = _host.NewContext();

        foreach (var id in ids)
        {
            var work = new Work { Id = id, Title = $"Work {id}" };
            customize(work);
            db.Works.Add(work);
            db.ShipWorks.Add(new ShipWork { ShipId = shipId, WorkId = id });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Records an existing work as also appearing under a second ship's index.</summary>
    private async Task LinkAsync(int shipId, long workId)
    {
        await using var db = _host.NewContext();
        db.ShipWorks.Add(new ShipWork { ShipId = shipId, WorkId = workId });
        await db.SaveChangesAsync();
    }

    private static PagedResult<WorkListItemDto> Works(ActionResult<PagedResult<WorkListItemDto>> result) =>
        Assert.IsType<PagedResult<WorkListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
}
