using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace Ao3Tracker.Tests;

/// <summary>
/// Where a reader is in a work. Per-user data written by a page the reader is scrolling, so the
/// questions are whose it is, whether a second save replaces the first, and whether one reader's
/// place can ever be another's.
/// </summary>
public class ReadingPositionsControllerTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";

    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_reader_with_no_place_yet_has_none()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var result = await _host.NewReadingPositionRequest(emma).GetReadingPosition(1, default);

        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public async Task Saves_a_place_and_reads_it_back()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var saved = Position(await _host.NewReadingPositionRequest(emma).SetReadingPosition(1, new(2, 41, 0.35), default));

        Assert.Equal(2, saved.ChapterIndex);
        Assert.Equal(41, saved.BlockIndex);
        Assert.Equal(0.35, saved.Progress);

        var read = Position(await _host.NewReadingPositionRequest(emma).GetReadingPosition(1, default));

        Assert.Equal(saved, read);
    }

    [Fact]
    public async Task A_second_save_replaces_the_first()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        Position(await _host.NewReadingPositionRequest(emma).SetReadingPosition(1, new(0, 3, 0.01), default));
        Position(await _host.NewReadingPositionRequest(emma).SetReadingPosition(1, new(5, 0, 0.9), default));

        var read = Position(await _host.NewReadingPositionRequest(emma).GetReadingPosition(1, default));

        Assert.Equal(5, read.ChapterIndex);
        Assert.Equal(0, read.BlockIndex);

        await using var db = _host.NewContext();
        Assert.Single(db.ReadingPositions);
    }

    [Fact]
    public async Task Two_readers_places_in_one_work_stay_apart()
    {
        var emma = _host.SeedUser();
        var mercy = _host.SeedUser("mercy");
        var ship = await WatchAsync(Lexa, emma);
        await WatchAsync(Lexa, mercy);
        await SeedWorksAsync(ship, 1);

        Position(await _host.NewReadingPositionRequest(emma).SetReadingPosition(1, new(1, 1, 0.1), default));
        Position(await _host.NewReadingPositionRequest(mercy).SetReadingPosition(1, new(7, 7, 0.7), default));

        Assert.Equal(1, Position(await _host.NewReadingPositionRequest(emma).GetReadingPosition(1, default)).ChapterIndex);
        Assert.Equal(7, Position(await _host.NewReadingPositionRequest(mercy).GetReadingPosition(1, default)).ChapterIndex);
    }

    [Fact]
    public async Task Refuses_a_work_no_ship_the_reader_watches_carries()
    {
        var emma = _host.SeedUser();
        var mercy = _host.SeedUser("mercy");
        await SeedWorksAsync(await WatchAsync(Lexa, mercy), 1);

        Assert.IsType<NotFoundResult>((await _host.NewReadingPositionRequest(emma).GetReadingPosition(1, default)).Result);
        Assert.IsType<NotFoundResult>(
            (await _host.NewReadingPositionRequest(emma).SetReadingPosition(1, new(0, 0, 0), default)).Result);
    }

    [Theory]
    [InlineData(-1, 0, 0.0, "ChapterIndex")]
    [InlineData(0, -1, 0.0, "BlockIndex")]
    [InlineData(0, 0, 1.5, "Progress")]
    [InlineData(0, 0, -0.1, "Progress")]
    [InlineData(0, 0, double.NaN, "Progress")]
    public async Task Refuses_a_place_that_is_not_one(int chapter, int block, double progress, string field)
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var result = await _host.NewReadingPositionRequest(emma).SetReadingPosition(1, new(chapter, block, progress), default);

        var problem = Assert.IsType<ValidationProblemDetails>(
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.Contains(field, problem.Errors.Keys);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<int> WatchAsync(string tagName, ApplicationUser watcher)
    {
        var result = await _host.Ships(watcher).WatchShip(new(tagName), default);
        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    private async Task SeedWorksAsync(int shipId, params long[] ids)
    {
        await using var db = _host.NewContext();

        foreach (var id in ids)
        {
            db.Works.Add(new Work { Id = id, Title = $"Work {id}", UpdatedAt = DateTime.UtcNow });
            db.ShipWorks.Add(new ShipWork { ShipId = shipId, WorkId = id });
        }

        await db.SaveChangesAsync();
    }

    private static ReadingPositionDto Position(ActionResult<ReadingPositionDto> result) =>
        Assert.IsType<ReadingPositionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
}
