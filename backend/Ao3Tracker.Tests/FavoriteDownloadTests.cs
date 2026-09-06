using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Downloads;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// The setting that makes a favorite ask for its own EPUB, and the favorite write that acts on it.
///
/// Two things carry the risk. That it fires only on the transition: every control on a row sends
/// the whole state, so a reader re-rating a favorite would otherwise re-ask for a file they have
/// or have already asked for. And that the request it makes is the same request the button makes
/// — one row per (reader, work, format), completing off a copy already on disk — rather than a
/// second path to the queue with its own idea of "already asked for".
/// </summary>
public class FavoriteDownloadTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";

    /// <summary>
    /// The version every seeded work reports, so a file seeded at it is a copy of the current
    /// version rather than of one the work has moved past.
    /// </summary>
    private static readonly DateTime FirstVersion = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- the preference ------------------------------------------------------------------------

    [Fact]
    public async Task Is_off_for_a_new_account()
    {
        var emma = _host.SeedUser();

        var preferences = Preferences(await _host.AccountPreferences(emma).Get());

        Assert.False(preferences.AutoDownloadFavorites);
    }

    [Fact]
    public async Task Round_trips_the_preference()
    {
        var emma = _host.SeedUser();

        var saved = Preferences(await _host.AccountPreferences(emma).Update(new(AutoDownloadFavorites: true)));
        Assert.True(saved.AutoDownloadFavorites);

        Assert.True(Preferences(await _host.AccountPreferences(emma).Get()).AutoDownloadFavorites);

        var cleared = Preferences(await _host.AccountPreferences(emma).Update(new(AutoDownloadFavorites: false)));
        Assert.False(cleared.AutoDownloadFavorites);
        Assert.False(Preferences(await _host.AccountPreferences(emma).Get()).AutoDownloadFavorites);
    }

    [Fact]
    public async Task Keeps_the_preference_to_the_account_that_set_it()
    {
        var emma = _host.SeedUser();
        var mercy = _host.SeedUser("mercy");

        await _host.AccountPreferences(emma).Update(new(AutoDownloadFavorites: true));

        Assert.False(Preferences(await _host.AccountPreferences(mercy).Get()).AutoDownloadFavorites);
    }

    // ---- what a favorite does ------------------------------------------------------------------

    [Fact]
    public async Task Favoriting_a_work_queues_its_epub_once_the_reader_has_asked_for_that()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await _host.AccountPreferences(emma).Update(new(AutoDownloadFavorites: true));

        var saved = State(await Favorite(emma, 1));
        Assert.True(saved.IsFavorite);

        var queued = Assert.Single(await DownloadRowsAsync());
        Assert.Equal(emma.Id, queued.UserId);
        Assert.Equal(1, queued.WorkId);
        Assert.Equal(Ao3DownloadFormat.Epub, queued.Format);
        Assert.Equal(DownloadStatus.Pending, queued.Status);

        // Queued, not fetched: the mark answers without waiting on the rate gate, and the worker is
        // told there is something for it rather than left to its poll interval.
        Assert.Empty(_host.Http.Requested);
        Assert.Empty(_host.Http.FilesRequested);
        Assert.True(await _host.DownloadWake.WaitAsync(TimeSpan.Zero));

        // It is the reader's own request, listed where the button's would be.
        var listed = Assert.Single(Downloads(await _host.NewDownloadsRequest(emma).GetDownloads(default)));
        Assert.Equal(queued.Id, listed.Id);
        Assert.Equal("Work 1", listed.WorkTitle);
    }

    [Fact]
    public async Task Favoriting_queues_nothing_unless_the_reader_asked_for_that()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var saved = State(await Favorite(emma, 1));

        Assert.True(saved.IsFavorite);
        Assert.Empty(await DownloadRowsAsync());
        Assert.False(await _host.DownloadWake.WaitAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task Favoriting_queues_nothing_once_the_reader_has_turned_it_back_off()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await _host.AccountPreferences(emma).Update(new(AutoDownloadFavorites: true));
        await _host.AccountPreferences(emma).Update(new(AutoDownloadFavorites: false));

        await Favorite(emma, 1);

        Assert.Empty(await DownloadRowsAsync());
    }

    [Fact]
    public async Task Re_saving_a_favorite_does_not_ask_again()
    {
        // Every control sends the whole state, so a rating on a favorite arrives as a save that
        // still says IsFavorite. The request is the mark going on, not the mark being there.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await _host.AccountPreferences(emma).Update(new(AutoDownloadFavorites: true));

        await Favorite(emma, 1);
        var first = Assert.Single(await DownloadRowsAsync());

        // Dropped from the queue, so a second ask would be visible as a new row rather than
        // hidden behind the idempotence the request itself has.
        await _host.NewDownloadsRequest(emma).DeleteDownload(first.Id, default);
        Assert.Empty(await DownloadRowsAsync());

        var rated = State(await _host.NewWorksRequest(emma).SetWorkState(
            1, new("Read", 8, null, IsFavorite: true), default));

        Assert.True(rated.IsFavorite);
        Assert.Equal(8, rated.Rating);
        Assert.Empty(await DownloadRowsAsync());
    }

    [Fact]
    public async Task Marking_again_after_unmarking_asks_again()
    {
        // The reverse of the above: off and on is two transitions, and the second is a mark
        // going on. What that costs is settled by the request's own rules — a row still on the
        // queue is answered with itself, not doubled.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await _host.AccountPreferences(emma).Update(new(AutoDownloadFavorites: true));

        await Favorite(emma, 1);
        var first = Assert.Single(await DownloadRowsAsync());
        await _host.NewDownloadsRequest(emma).DeleteDownload(first.Id, default);

        await _host.NewWorksRequest(emma).SetWorkState(1, new(null, null, null, IsFavorite: false), default);
        await Favorite(emma, 1);

        var second = Assert.Single(await DownloadRowsAsync());
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Unfavoriting_leaves_the_download_where_it_is()
    {
        // The mark asked for the file; taking the mark off is not asking for the file to go. The
        // Downloads page is where a request is dropped, and it says so.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await _host.AccountPreferences(emma).Update(new(AutoDownloadFavorites: true));

        await Favorite(emma, 1);
        await _host.NewWorksRequest(emma).SetWorkState(1, new(null, null, null, IsFavorite: false), default);

        Assert.Single(await DownloadRowsAsync());
    }

    [Fact]
    public async Task Favoriting_a_work_already_asked_for_does_not_queue_a_second_fetch()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        await _host.AccountPreferences(emma).Update(new(AutoDownloadFavorites: true));

        var clicked = Download(await _host.NewDownloadsRequest(emma).RequestDownload(1, new("Epub"), default));

        await Favorite(emma, 1);

        var only = Assert.Single(await DownloadRowsAsync());
        Assert.Equal(clicked.Id, only.Id);
        Assert.Equal(DownloadStatus.Pending, only.Status);
    }

    [Fact]
    public async Task Favoriting_a_work_another_reader_already_fetched_completes_without_a_fetch()
    {
        // The same rule the button follows, reached from the mark: bytes for this exact version are
        // on disk, so the request is complete on the spot and AO3 is not asked for anything.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);
        var fileId = await SeedFileAsync(1, Ao3DownloadFormat.Epub, FirstVersion, sizeBytes: 3);

        await _host.AccountPreferences(emma).Update(new(AutoDownloadFavorites: true));
        await Favorite(emma, 1);

        var mine = Assert.Single(await DownloadRowsAsync());
        Assert.Equal(DownloadStatus.Complete, mine.Status);
        Assert.Equal(fileId, mine.WorkDownloadFileId);
        Assert.Empty(_host.Http.FilesRequested);
        Assert.False(await _host.DownloadWake.WaitAsync(TimeSpan.Zero));

        var listed = Assert.Single(Downloads(await _host.NewDownloadsRequest(emma).GetDownloads(default)));
        Assert.Equal(3, listed.SizeBytes);
    }

    [Fact]
    public async Task Favoriting_with_nothing_else_said_still_asks()
    {
        // A mark on its own is a row of its own (see UserWorkStateTests), and it is also a mark
        // going on. The insert path and the update path have to agree.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1, 2);
        await _host.AccountPreferences(emma).Update(new(AutoDownloadFavorites: true));

        // Work 1 has a row already; work 2 does not.
        await _host.NewWorksRequest(emma).SetWorkState(1, new("ToRead", null, null), default);

        await _host.NewWorksRequest(emma).SetWorkState(1, new("ToRead", null, null, IsFavorite: true), default);
        await _host.NewWorksRequest(emma).SetWorkState(2, new(null, null, null, IsFavorite: true), default);

        var rows = await DownloadRowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, d => d.WorkId == 1);
        Assert.Contains(rows, d => d.WorkId == 2);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private Task<ActionResult<WorkStateDto>> Favorite(ApplicationUser reader, long workId) =>
        _host.NewWorksRequest(reader).SetWorkState(workId, new(null, null, null, IsFavorite: true), default);

    /// <summary>Follows a tag through the real endpoint, returning the ship it resolved to.</summary>
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
            db.Works.Add(new Work { Id = id, Title = $"Work {id}", UpdatedAt = FirstVersion });
            db.ShipWorks.Add(new ShipWork { ShipId = shipId, WorkId = id });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Bytes on disk, as the download worker will leave them: the row, and a file of that size at
    /// the path the row names — the same shape DownloadsControllerTests seeds, because a request
    /// is only answered off a stored file whose bytes are still there.
    /// </summary>
    private async Task<int> SeedFileAsync(long workId, Ao3DownloadFormat format, DateTime version, long sizeBytes)
    {
        var relativePath = DownloadPaths.Relative(workId, format, version);
        var absolutePath = DownloadPaths.Absolute(_host.DataDirectory, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllBytesAsync(absolutePath, new byte[sizeBytes]);

        await using var db = _host.NewContext();

        var file = new WorkDownloadFile
        {
            WorkId = workId,
            Format = format,
            WorkUpdatedAt = version,
            RelativePath = relativePath,
            SizeBytes = sizeBytes,
            FetchedAt = version,
        };

        db.WorkDownloadFiles.Add(file);
        await db.SaveChangesAsync();

        return file.Id;
    }

    private async Task<List<Download>> DownloadRowsAsync()
    {
        await using var db = _host.NewContext();
        return await db.Downloads.ToListAsync();
    }

    private static WorkStateDto State(ActionResult<WorkStateDto> result) =>
        Assert.IsType<WorkStateDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static AccountPreferencesDto Preferences(ActionResult<AccountPreferencesDto> result) =>
        Assert.IsType<AccountPreferencesDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static DownloadDto Download(ActionResult<DownloadDto> result) =>
        Assert.IsType<DownloadDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static IReadOnlyList<DownloadDto> Downloads(ActionResult<IReadOnlyList<DownloadDto>> result) =>
        Assert.IsType<List<DownloadDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
}
