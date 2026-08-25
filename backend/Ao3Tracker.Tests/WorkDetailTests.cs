using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace Ao3Tracker.Tests;

/// <summary>
/// <c>GET /api/works/{id}</c> — everything this instance already holds about one work, which is a
/// strict superset of the row the feed shows: summary, the whole tag list by kind, series, language
/// and the reader's own state.
///
/// It fetches nothing. Every field here was written by a listing scrape, so the risks are the same
/// two the list carries — whose library this is, and whose state is being read — plus one of its
/// own: this is the first endpoint that hands a work's summary to a browser, and that summary is
/// markup a stranger typed. The fields a detail fetch will later fill (published date, the complete
/// tag list) are reported as unfetched rather than as absent, so the page can say which it is.
/// </summary>
public class WorkDetailTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";
    private const string Bellarke = "Bellamy Blake/Clarke Griffin";

    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- what it holds -------------------------------------------------------------------------

    [Fact]
    public async Task Reports_everything_the_listing_scrape_wrote()
    {
        var emma = _host.SeedUser();
        var lexa = await WatchAsync(Lexa, emma);
        await SeedWorkAsync(lexa, 1, work =>
        {
            work.Title = "The long way round";
            work.SummaryHtml = "<p>They meet in the woods.</p>";
            work.Rating = Ao3Rating.TeenAndUpAudiences;
            work.Categories = Ao3Category.FF;
            work.Warnings = Ao3Warning.NoArchiveWarningsApply;
            work.IsComplete = true;
            work.WordCount = 42_000;
            work.ChapterCount = 12;
            work.PlannedChapterCount = 12;
            work.Kudos = 900;
            work.Hits = 30_000;
            work.Bookmarks = 120;
            work.CommentCount = 88;
            work.CollectionCount = 2;
            work.LanguageName = "English";
            work.LanguageCode = "en";
            work.IsRestricted = true;
        });

        var work = Detail(await _host.NewWorksRequest(emma).GetWork(1, default));

        Assert.Equal(1, work.Id);
        Assert.Equal("The long way round", work.Title);
        Assert.Equal("Teen And Up Audiences", work.Rating);
        Assert.Equal(["F/F"], work.Categories);
        Assert.Equal(["No Archive Warnings Apply"], work.Warnings);
        Assert.True(work.IsComplete);
        Assert.Equal(42_000, work.WordCount);
        Assert.Equal(12, work.ChapterCount);
        Assert.Equal(12, work.PlannedChapterCount);
        Assert.Equal(900, work.Kudos);
        Assert.Equal(30_000, work.Hits);
        Assert.Equal(120, work.Bookmarks);
        Assert.Equal(88, work.CommentCount);
        Assert.Equal(2, work.CollectionCount);
        Assert.Equal("English", work.LanguageName);
        Assert.Equal("en", work.LanguageCode);
        Assert.True(work.IsRestricted);
    }

    [Fact]
    public async Task Reports_the_byline_in_the_order_the_blurb_rendered_it()
    {
        var emma = _host.SeedUser();
        var lexa = await WatchAsync(Lexa, emma);
        await SeedWorkAsync(lexa, 1);
        await SeedAuthorsAsync(1, ("second", 1), ("first", 0));

        Assert.Equal(["first", "second"], Detail(await _host.NewWorksRequest(emma).GetWork(1, default)).Authors);
    }

    [Fact]
    public async Task Reports_the_tag_list_with_each_tags_kind()
    {
        // The list row carries fandoms only. Being able to read the relationships, characters and
        // freeforms without leaving for AO3 is most of what a detail page is for.
        var emma = _host.SeedUser();
        var lexa = await WatchAsync(Lexa, emma);
        await SeedWorkAsync(lexa, 1);
        // Seeded in an order no reader would want them back in, so the ordering is the assertion
        // rather than an accident of what the database happens to return rows in.
        await SeedTagsAsync(1,
            (Ao3TagType.Freeform, "Slow Burn"),
            (Ao3TagType.Character, "Lexa"),
            (Ao3TagType.Freeform, "Angst"),
            (Ao3TagType.Relationship, Lexa),
            (Ao3TagType.Fandom, "The 100"));

        var tags = Detail(await _host.NewWorksRequest(emma).GetWork(1, default)).Tags;

        Assert.Equal(
            [
                new WorkTagDto(nameof(Ao3TagType.Fandom), "The 100"),
                new WorkTagDto(nameof(Ao3TagType.Relationship), Lexa),
                new WorkTagDto(nameof(Ao3TagType.Character), "Lexa"),
                new WorkTagDto(nameof(Ao3TagType.Freeform), "Angst"),
                new WorkTagDto(nameof(Ao3TagType.Freeform), "Slow Burn"),
            ],
            tags);
    }

    [Fact]
    public async Task Reports_the_series_a_work_is_part_of()
    {
        var emma = _host.SeedUser();
        var lexa = await WatchAsync(Lexa, emma);
        await SeedWorkAsync(lexa, 1);
        await SeedSeriesAsync(1, (77, "Winter, in three parts", 2));

        Assert.Equal(
            [new WorkSeriesDto(77, "Winter, in three parts", 2)],
            Detail(await _host.NewWorksRequest(emma).GetWork(1, default)).Series);
    }

    [Fact]
    public async Task Reports_the_fields_a_detail_fetch_has_not_filled_yet_as_unfetched()
    {
        // Null here is "nobody has fetched the work's own page", not "the work has no publication
        // date". T10 fills these; until it does the page has to be able to say which it is.
        var emma = _host.SeedUser();
        await SeedWorkAsync(await WatchAsync(Lexa, emma), 1);

        var work = Detail(await _host.NewWorksRequest(emma).GetWork(1, default));

        Assert.Null(work.PublishedAt);
        Assert.Null(work.DetailFetchedAt);
    }

    // ---- whose library, whose state ------------------------------------------------------------

    [Fact]
    public async Task Refuses_a_work_no_ship_the_reader_follows_carries()
    {
        // The same scoping the list applies, and for the same reason: works are global rows, so a
        // detail endpoint that answered on the work id alone would serve someone else's library to
        // anyone who could guess an AO3 work number.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        await SeedWorkAsync(await WatchAsync(Bellarke, sam), 1);
        await WatchAsync(Lexa, emma);

        Assert.IsType<NotFoundResult>((await _host.NewWorksRequest(emma).GetWork(1, default)).Result);
    }

    [Fact]
    public async Task Refuses_a_work_this_instance_has_never_heard_of()
    {
        var emma = _host.SeedUser();
        await WatchAsync(Lexa, emma);

        Assert.IsType<NotFoundResult>((await _host.NewWorksRequest(emma).GetWork(404, default)).Result);
    }

    [Fact]
    public async Task Refuses_a_work_ao3_has_deleted()
    {
        var emma = _host.SeedUser();
        var lexa = await WatchAsync(Lexa, emma);
        await SeedWorkAsync(lexa, 1, work =>
        {
            work.IsDeleted = true;
            work.DeletedAt = DateTime.UtcNow;
        });

        Assert.IsType<NotFoundResult>((await _host.NewWorksRequest(emma).GetWork(1, default)).Result);
    }

    [Fact]
    public async Task Names_only_the_ships_the_reader_follows_it_under()
    {
        // Which other ships this instance tracks for other people is nobody else's business, and
        // the ship id rides along so the page can link back into the feed narrowed to it.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var lexa = await WatchAsync(Lexa, emma);
        var bellarke = await WatchAsync(Bellarke, sam);
        await SeedWorkAsync(lexa, 1);
        await SeedShipWorkAsync(bellarke, 1);

        var ships = Detail(await _host.NewWorksRequest(emma).GetWork(1, default)).Ships;

        Assert.Equal([new WorkShipDto(lexa, Lexa)], ships);
    }

    [Fact]
    public async Task Carries_the_readers_own_state_and_never_anothers()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var lexa = await WatchAsync(Lexa, emma);
        await SeedWorkAsync(lexa, 1);
        await WatchAsync(Lexa, sam);

        await _host.NewWorksRequest(emma).SetWorkState(1, new("Read", 8, "mine"), default);
        await _host.NewWorksRequest(sam).SetWorkState(1, new("Dropped", 1, "sam's"), default);

        Assert.Equal(
            new WorkStateDto("Read", 8, "mine"),
            Detail(await _host.NewWorksRequest(emma).GetWork(1, default)).State);
    }

    [Fact]
    public async Task Never_shows_a_reader_another_readers_state()
    {
        // Distinct from the test above: there, both readers had marked the work, so a query that
        // forgot whose state it was reading could still land on the right row by luck. Here only
        // the other reader has marked it, and the only correct answer is the cleared state.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var lexa = await WatchAsync(Lexa, emma);
        await SeedWorkAsync(lexa, 1);
        await WatchAsync(Lexa, sam);

        await _host.NewWorksRequest(sam).SetWorkState(1, new("Read", 10, "sam's note"), default);

        Assert.Equal(
            WorkStateDto.Cleared,
            Detail(await _host.NewWorksRequest(emma).GetWork(1, default)).State);
    }

    [Fact]
    public async Task Reports_a_cleared_state_for_a_work_the_reader_has_not_touched()
    {
        var emma = _host.SeedUser();
        await SeedWorkAsync(await WatchAsync(Lexa, emma), 1);

        Assert.Equal(
            WorkStateDto.Cleared,
            Detail(await _host.NewWorksRequest(emma).GetWork(1, default)).State);
    }

    // ---- the summary ---------------------------------------------------------------------------

    [Fact]
    public async Task Hands_the_summary_over_sanitized()
    {
        // This is the first endpoint that gives a browser a work's summary at all. What is stored
        // is whatever the author typed into AO3, so the sanitizing has to happen on the way out
        // rather than being left to whichever client remembers to do it.
        var emma = _host.SeedUser();
        var lexa = await WatchAsync(Lexa, emma);
        await SeedWorkAsync(lexa, 1, work =>
            work.SummaryHtml = "<p>They meet.</p><script>alert('x')</script>");

        var summary = Detail(await _host.NewWorksRequest(emma).GetWork(1, default)).SummarySafeHtml;

        Assert.Equal("<p>They meet.</p>", summary);
    }

    [Fact]
    public async Task Reports_no_summary_where_the_scrape_read_none()
    {
        var emma = _host.SeedUser();
        await SeedWorkAsync(await WatchAsync(Lexa, emma), 1);

        Assert.Null(Detail(await _host.NewWorksRequest(emma).GetWork(1, default)).SummarySafeHtml);
    }

    // ---- fixture -------------------------------------------------------------------------------

    /// <summary>Follows a tag through the real endpoint, returning the ship it resolved to.</summary>
    private async Task<int> WatchAsync(string tagName, ApplicationUser watcher)
    {
        var result = await _host.Ships(watcher).WatchShip(new(tagName), default);
        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    private async Task SeedWorkAsync(int shipId, long id, Action<Work>? configure = null)
    {
        await using var db = _host.NewContext();

        var work = new Work { Id = id, Title = $"Work {id}" };
        configure?.Invoke(work);

        db.Works.Add(work);
        db.ShipWorks.Add(new ShipWork { ShipId = shipId, WorkId = id });
        await db.SaveChangesAsync();
    }

    private async Task SeedShipWorkAsync(int shipId, long workId)
    {
        await using var db = _host.NewContext();
        db.ShipWorks.Add(new ShipWork { ShipId = shipId, WorkId = workId });
        await db.SaveChangesAsync();
    }

    private async Task SeedAuthorsAsync(long workId, params (string DisplayName, int Position)[] authors)
    {
        await using var db = _host.NewContext();

        foreach (var (displayName, position) in authors)
        {
            var pseud = new Ao3Pseud
            {
                Username = displayName,
                PseudName = displayName,
                UsernameNormalized = displayName.ToUpperInvariant(),
                PseudNameNormalized = displayName.ToUpperInvariant(),
                DisplayName = displayName,
                DisplayNameNormalized = displayName.ToUpperInvariant(),
            };
            db.Ao3Pseuds.Add(pseud);
            await db.SaveChangesAsync();

            db.WorkAuthors.Add(new WorkAuthor { WorkId = workId, PseudId = pseud.Id, Position = position });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedTagsAsync(long workId, params (Ao3TagType Type, string Name)[] tags)
    {
        await using var db = _host.NewContext();

        foreach (var (type, name) in tags)
        {
            var tag = new Tag { Type = type, Name = name, NameNormalized = name.ToUpperInvariant() };
            db.Tags.Add(tag);
            await db.SaveChangesAsync();

            db.WorkTags.Add(new WorkTag { WorkId = workId, TagId = tag.Id });
        }

        await db.SaveChangesAsync();
    }

    private async Task SeedSeriesAsync(long workId, params (long Id, string Title, int? Part)[] series)
    {
        await using var db = _host.NewContext();

        foreach (var (id, title, part) in series)
        {
            db.Ao3Series.Add(new Ao3Series { Id = id, Title = title });
            db.WorkSeries.Add(new WorkSeries { WorkId = workId, SeriesId = id, Part = part });
        }

        await db.SaveChangesAsync();
    }

    private static WorkDetailDto Detail(ActionResult<WorkDetailDto> result) =>
        Assert.IsType<WorkDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
}
