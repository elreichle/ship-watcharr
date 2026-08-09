using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace Ao3Tracker.Tests;

/// <summary>
/// The paginated library.
///
/// Two things carry the risk here and get most of the coverage: what a user is allowed to see —
/// works are global rows, so visibility comes entirely from the caller's subscriptions — and
/// whether paging is actually total, since a sort with ties can otherwise show one work twice and
/// another never, with nothing in the response to say so.
/// </summary>
public class WorksControllerTests : IDisposable
{
    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- what you can see ----------------------------------------------------------------------

    [Fact]
    public async Task Returns_works_from_the_ships_you_follow()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, 1, 2);

        var page = Body(await _host.Works(emma).GetWorks(ct: default));

        Assert.Equal([1, 2], page.Items.Select(w => w.Id).Order());
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task Hides_works_from_ships_you_do_not_follow()
    {
        // Works are shared rows; the subscription is the only thing scoping them to a reader.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1);
        await SeedWorksAsync(await SeedShipAsync("Kirk/Spock", sam), 2);

        var page = Body(await _host.Works(emma).GetWorks(ct: default));

        Assert.Equal([1], page.Items.Select(w => w.Id));
    }

    [Fact]
    public async Task Lists_a_work_once_when_it_appears_under_two_of_your_ships()
    {
        // A crossover carries both relationship tags. Scoping with a join instead of an Any()
        // subquery duplicates the row, and the duplicate also throws the total count off.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        var kirk = await SeedShipAsync("Kirk/Spock", emma);
        await SeedWorksAsync(lexa, 1);
        await LinkAsync(kirk, 1);

        var page = Body(await _host.Works(emma).GetWorks(ct: default));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(["Clarke Griffin/Lexa", "Kirk/Spock"], page.Items.Single().Ships.Order());
    }

    [Fact]
    public async Task Names_only_the_ships_the_reader_follows()
    {
        // Which other tags this instance tracks for other people is not a work listing's business.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        var kirk = await SeedShipAsync("Kirk/Spock", sam);
        await SeedWorksAsync(lexa, 1);
        await LinkAsync(kirk, 1);

        var page = Body(await _host.Works(emma).GetWorks(ct: default));

        Assert.Equal(["Clarke Griffin/Lexa"], page.Items.Single().Ships);
    }

    [Fact]
    public async Task Excludes_works_AO3_has_deleted()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, 1);
        await SeedWorksAsync(lexa, w => w.IsDeleted = true, 2);

        Assert.Equal([1], Body(await _host.Works(emma).GetWorks(ct: default)).Items.Select(w => w.Id));
    }

    [Fact]
    public async Task Filters_to_one_ship()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        var kirk = await SeedShipAsync("Kirk/Spock", emma);
        await SeedWorksAsync(lexa, 1);
        await SeedWorksAsync(kirk, 2);

        var page = Body(await _host.Works(emma).GetWorks(shipId: kirk, ct: default));

        Assert.Equal([2], page.Items.Select(w => w.Id));
    }

    [Fact]
    public async Task Refuses_to_filter_by_a_ship_you_do_not_follow()
    {
        // Without this, shipId is a way to read any tag on the instance by guessing small integers.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var kirk = await SeedShipAsync("Kirk/Spock", sam);
        await SeedWorksAsync(kirk, 1);

        var result = await _host.Works(emma).GetWorks(shipId: kirk, ct: default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Returns_an_empty_page_for_a_user_following_nothing()
    {
        var emma = _host.SeedUser();

        var page = Body(await _host.Works(emma).GetWorks(ct: default));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.TotalPages);
    }

    // ---- paging --------------------------------------------------------------------------------

    [Fact]
    public async Task Pages_through_every_work_exactly_once()
    {
        // The assertion that matters: walking every page returns each id once. All five works share
        // a kudos count, so without the id tie-break the database is free to order the ties
        // differently per query and this silently drops and repeats rows.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.Kudos = 100, 1, 2, 3, 4, 5);

        List<long> seen = [];
        for (var page = 1; page <= 3; page++)
        {
            var body = Body(await _host.Works(emma).GetWorks(page, pageSize: 2, sort: "kudos", ct: default));
            seen.AddRange(body.Items.Select(w => w.Id));
        }

        Assert.Equal([1, 2, 3, 4, 5], seen.Order());
    }

    [Fact]
    public async Task Reports_the_total_across_all_pages_not_the_page_size()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, 1, 2, 3, 4, 5);

        var page = Body(await _host.Works(emma).GetWorks(page: 1, pageSize: 2, ct: default));

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(3, page.TotalPages);
    }

    [Fact]
    public async Task Returns_an_empty_page_past_the_end_rather_than_wrapping()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1);

        var page = Body(await _host.Works(emma).GetWorks(page: 99, ct: default));

        Assert.Empty(page.Items);
        Assert.Equal(1, page.TotalCount);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public async Task Clamps_a_nonsense_page_number(int requested, int expected)
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1);

        Assert.Equal(expected, Body(await _host.Works(emma).GetWorks(page: requested, ct: default)).Page);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(5000, 100)]
    public async Task Clamps_the_page_size(int requested, int expected)
    {
        // The upper bound is the load-bearing one: each row fans out into authors, fandoms and ship
        // names, so an unbounded page size is an unbounded query rather than just a big response.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1);

        Assert.Equal(expected, Body(await _host.Works(emma).GetWorks(pageSize: requested, ct: default)).PageSize);
    }

    // ---- sorting -------------------------------------------------------------------------------

    [Fact]
    public async Task Sorts_by_most_recently_updated_by_default()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1);
        await SeedWorksAsync(lexa, w => w.UpdatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), 2);

        Assert.Equal([2, 1], Body(await _host.Works(emma).GetWorks(ct: default)).Items.Select(w => w.Id));
    }

    [Theory]
    [InlineData("kudos")]
    [InlineData("hits")]
    [InlineData("bookmarks")]
    [InlineData("comments")]
    [InlineData("words")]
    [InlineData("updated")]
    public async Task Accepts_every_sort_it_advertises(string sort)
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1);

        Assert.Single(Body(await _host.Works(emma).GetWorks(sort: sort, ct: default)).Items);
    }

    [Fact]
    public async Task Reverses_on_request()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.Kudos = 10, 1);
        await SeedWorksAsync(lexa, w => w.Kudos = 90, 2);

        var page = Body(await _host.Works(emma).GetWorks(sort: "kudos", ascending: true, ct: default));

        Assert.Equal([1, 2], page.Items.Select(w => w.Id));
    }

    [Fact]
    public async Task Rejects_a_sort_it_does_not_offer()
    {
        // A typo'd sort that quietly returns a different order is worse than an error, and "title"
        // in particular is absent on purpose: it would order differently on SQLite and PostgreSQL.
        var emma = _host.SeedUser();

        var result = await _host.Works(emma).GetWorks(sort: "title", ct: default);

        var problem = Assert.IsType<ValidationProblemDetails>(
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.True(problem.Errors.ContainsKey("sort"));
    }

    // ---- what a row says -----------------------------------------------------------------------

    [Fact]
    public async Task Expands_the_flag_columns_into_AO3s_own_labels()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(
            await SeedShipAsync("Clarke Griffin/Lexa", emma),
            w =>
            {
                w.Rating = Ao3Rating.TeenAndUpAudiences;
                w.Categories = Ao3Category.FF | Ao3Category.Gen;
                w.Warnings = Ao3Warning.MajorCharacterDeath;
            },
            1);

        var work = Body(await _host.Works(emma).GetWorks(ct: default)).Items.Single();

        Assert.Equal("Teen And Up Audiences", work.Rating);
        Assert.Equal(["F/F", "Gen"], work.Categories);
        Assert.Equal(["Major Character Death"], work.Warnings);
    }

    [Fact]
    public async Task Reports_no_categories_rather_than_inventing_one()
    {
        // Empty is the honest answer for a work scraped before the parser understood categories.
        // "No category" is AO3's explicit choice and means something different.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1);

        var work = Body(await _host.Works(emma).GetWorks(ct: default)).Items.Single();

        Assert.Empty(work.Categories);
        Assert.Empty(work.Warnings);
    }

    [Fact]
    public async Task Lists_authors_in_byline_order()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1);

        await using (var db = _host.NewContext())
        {
            db.Ao3Pseuds.AddRange(
                new Ao3Pseud { Id = 1, Username = "second", PseudName = "second", DisplayName = "second" },
                new Ao3Pseud { Id = 2, Username = "first", PseudName = "first", DisplayName = "first" });
            db.WorkAuthors.AddRange(
                new WorkAuthor { WorkId = 1, PseudId = 1, Position = 1 },
                new WorkAuthor { WorkId = 1, PseudId = 2, Position = 0 });
            await db.SaveChangesAsync();
        }

        var work = Body(await _host.Works(emma).GetWorks(ct: default)).Items.Single();

        Assert.Equal(["first", "second"], work.Authors);
    }

    [Fact]
    public async Task Lists_fandoms_and_leaves_the_other_tag_types_out()
    {
        // The list column is fandoms only; characters and freeforms would make every row unreadable.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1);

        await using (var db = _host.NewContext())
        {
            db.Tags.AddRange(
                new Tag { Id = 1, Type = Ao3TagType.Fandom, Name = "The 100", NameNormalized = "THE 100" },
                new Tag { Id = 2, Type = Ao3TagType.Freeform, Name = "Fluff", NameNormalized = "FLUFF" });
            db.WorkTags.AddRange(
                new WorkTag { WorkId = 1, TagId = 1 },
                new WorkTag { WorkId = 1, TagId = 2 });
            await db.SaveChangesAsync();
        }

        Assert.Equal(["The 100"], Body(await _host.Works(emma).GetWorks(ct: default)).Items.Single().Fandoms);
    }

    [Fact]
    public async Task Keeps_an_open_ended_WIPs_planned_total_null()
    {
        // AO3 shows "?" there. Collapsing it to the current chapter count would render every WIP as
        // if it were finished.
        var emma = _host.SeedUser();
        await SeedWorksAsync(
            await SeedShipAsync("Clarke Griffin/Lexa", emma),
            w =>
            {
                w.ChapterCount = 3;
                w.PlannedChapterCount = null;
            },
            1);

        var work = Body(await _host.Works(emma).GetWorks(ct: default)).Items.Single();

        Assert.Equal(3, work.ChapterCount);
        Assert.Null(work.PlannedChapterCount);
    }

    // ---- fixture -------------------------------------------------------------------------------

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

    private static PagedResult<WorkListItemDto> Body(ActionResult<PagedResult<WorkListItemDto>> result) =>
        Assert.IsType<PagedResult<WorkListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
}
