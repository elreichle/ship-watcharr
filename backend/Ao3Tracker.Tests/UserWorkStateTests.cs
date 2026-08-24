using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// One reader's own data about a work — reading status, half-star rating, note — and the copy of
/// it each row of the library list carries.
///
/// Three things carry the risk. Whose data it is: the endpoints take no user id, so a reader can
/// only ever name a work, never an owner. What "no opinion" means: an unrated work and a work
/// rated the lowest possible score are different facts, and a null coerced to a number quietly
/// loses one of them. And whether a re-scrape can cost a reader their own writing, which is the
/// whole reason this lives in a table of its own.
/// </summary>
public class UserWorkStateTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";

    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- reading and writing -------------------------------------------------------------------

    [Fact]
    public async Task Reports_a_cleared_state_for_a_work_nobody_has_touched()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var state = State(await _host.NewWorksRequest(emma).GetWorkState(1, default));

        Assert.Equal(nameof(ReadingStatus.None), state.Status);
        Assert.Null(state.Rating);
        Assert.Null(state.Note);
    }

    [Fact]
    public async Task Round_trips_a_status_a_rating_and_a_note()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var saved = State(await _host.NewWorksRequest(emma).SetWorkState(
            1, new("Read", 7, "Made me put the phone down."), default));

        Assert.Equal("Read", saved.Status);
        Assert.Equal(7, saved.Rating);
        Assert.Equal("Made me put the phone down.", saved.Note);

        var read = State(await _host.NewWorksRequest(emma).GetWorkState(1, default));
        Assert.Equal(saved, read);
    }

    [Fact]
    public async Task Keeps_an_unrated_work_distinct_from_the_lowest_rating()
    {
        // Half a star is a real opinion and "I have not rated this" is not one. Coercing the null
        // to the bottom of the scale would report the two identically for ever after.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1, 2);

        await _host.NewWorksRequest(emma).SetWorkState(1, new("Read", null, null), default);
        await _host.NewWorksRequest(emma).SetWorkState(2, new("Read", 1, null), default);

        Assert.Null(State(await _host.NewWorksRequest(emma).GetWorkState(1, default)).Rating);
        Assert.Equal(1, State(await _host.NewWorksRequest(emma).GetWorkState(2, default)).Rating);
    }

    [Fact]
    public async Task Clears_a_rating_without_clearing_the_status_beside_it()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        await _host.NewWorksRequest(emma).SetWorkState(1, new("Read", 9, null), default);
        var cleared = State(await _host.NewWorksRequest(emma).SetWorkState(1, new("Read", null, null), default));

        Assert.Equal("Read", cleared.Status);
        Assert.Null(cleared.Rating);
    }

    [Fact]
    public async Task Updates_the_row_it_already_has_rather_than_adding_a_second()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        await _host.NewWorksRequest(emma).SetWorkState(1, new("ToRead", null, null), default);
        await _host.NewWorksRequest(emma).SetWorkState(1, new("Reading", 4, null), default);

        await using var db = _host.NewContext();
        var row = Assert.Single(await db.UserWorkStates.ToListAsync());
        Assert.Equal(ReadingStatus.Reading, row.Status);
        Assert.Equal(4, row.Rating);
    }

    [Fact]
    public async Task Stores_a_state_with_nothing_left_in_it_as_no_row_at_all()
    {
        // The canonical cleared state is the absence of a row — see the remarks on SetWorkState.
        // A reader cannot tell the difference, but every query that has to treat "unread" as
        // including works nobody has opened is spared a second case if only one of the two exists.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        await _host.NewWorksRequest(emma).SetWorkState(1, new("Read", 6, "note"), default);
        var cleared = State(await _host.NewWorksRequest(emma).SetWorkState(1, new(null, null, null), default));

        Assert.Equal(nameof(ReadingStatus.None), cleared.Status);

        await using var db = _host.NewContext();
        Assert.Empty(await db.UserWorkStates.ToListAsync());
    }

    [Fact]
    public async Task Stores_a_blank_note_as_no_note()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var saved = State(await _host.NewWorksRequest(emma).SetWorkState(
            1, new("Read", null, "   "), default));

        Assert.Null(saved.Note);
    }

    [Fact]
    public async Task Trims_a_note()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var saved = State(await _host.NewWorksRequest(emma).SetWorkState(
            1, new("Read", null, "  spoilers  "), default));

        Assert.Equal("spoilers", saved.Note);
    }

    // ---- what it refuses -----------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    public async Task Refuses_a_rating_outside_the_half_star_range(int rating)
    {
        // The column has a check constraint, so an unvalidated write is a 500 rather than a 400 —
        // and on SQLite, where the constraint is enforced, it takes the whole SaveChanges with it.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var result = await _host.NewWorksRequest(emma).SetWorkState(1, new("Read", rating, null), default);

        Assert.True(Rejected(result, "Rating"));
    }

    [Fact]
    public async Task Refuses_a_reading_status_it_does_not_offer()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var result = await _host.NewWorksRequest(emma).SetWorkState(1, new("Abandoned", null, null), default);

        Assert.True(Rejected(result, "Status"));
    }

    [Fact]
    public async Task Refuses_a_status_number_no_reading_status_has()
    {
        // Enum.TryParse takes a number as readily as a name and hands back whatever byte was asked
        // for, defined or not. A word cannot reach that path, so a test naming only "Abandoned"
        // pins the parse and leaves the range check unpinned — which is what let a 99 through.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var result = await _host.NewWorksRequest(emma).SetWorkState(1, new("99", null, null), default);

        Assert.True(Rejected(result, "Status"));
    }

    [Fact]
    public async Task Refuses_a_note_longer_than_the_column_holds()
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        var result = await _host.NewWorksRequest(emma).SetWorkState(
            1, new("Read", null, new string('x', 4001)), default);

        Assert.True(Rejected(result, "Note"));
    }

    // ---- whose state it is ---------------------------------------------------------------------

    [Fact]
    public async Task Refuses_to_write_state_for_a_work_outside_your_library()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        await SeedWorksAsync(await WatchAsync("Kirk/Spock", sam), 1);

        var result = await _host.NewWorksRequest(emma).SetWorkState(1, new("Read", 8, null), default);

        Assert.IsType<NotFoundResult>(result.Result);

        await using var db = _host.NewContext();
        Assert.Empty(await db.UserWorkStates.ToListAsync());
    }

    [Fact]
    public async Task Refuses_to_read_state_for_a_work_outside_your_library()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        await SeedWorksAsync(await WatchAsync("Kirk/Spock", sam), 1);

        var result = await _host.NewWorksRequest(emma).GetWorkState(1, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Refuses_a_work_this_instance_has_never_heard_of()
    {
        var emma = _host.SeedUser();
        await WatchAsync(Lexa, emma);

        Assert.IsType<NotFoundResult>((await _host.NewWorksRequest(emma).GetWorkState(404, default)).Result);
    }

    [Fact]
    public async Task Keeps_two_readers_states_apart_on_the_same_work()
    {
        // Nothing in either request names an owner: the work id is all a caller may say, and the
        // claim decides the rest. This is the test that would fail first if that stopped being true.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var lexa = await WatchAsync(Lexa, emma);
        await SeedWorksAsync(lexa, 1);
        await WatchAsync(Lexa, sam);

        await _host.NewWorksRequest(emma).SetWorkState(1, new("Read", 10, "loved it"), default);
        await _host.NewWorksRequest(sam).SetWorkState(1, new("Dropped", 2, "not for me"), default);

        var hers = State(await _host.NewWorksRequest(emma).GetWorkState(1, default));
        var his = State(await _host.NewWorksRequest(sam).GetWorkState(1, default));

        Assert.Equal(new WorkStateDto("Read", 10, "loved it"), hers);
        Assert.Equal(new WorkStateDto("Dropped", 2, "not for me"), his);
    }

    // ---- on the feed ---------------------------------------------------------------------------

    [Fact]
    public async Task Carries_the_readers_own_state_on_the_row()
    {
        // The point of putting it here is that a page of rows costs one request, not one per row.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1, 2);

        await _host.NewWorksRequest(emma).SetWorkState(1, new("Reading", 5, "halfway"), default);

        var page = Works(await _host.NewWorksRequest(emma).GetWorks(ct: default));

        var first = page.Items.Single(w => w.Id == 1);
        Assert.Equal(new WorkStateDto("Reading", 5, "halfway"), first.State);

        // And an untouched work reports the same cleared state the state endpoint would.
        var second = page.Items.Single(w => w.Id == 2);
        Assert.Equal(new WorkStateDto(nameof(ReadingStatus.None), null, null), second.State);
    }

    [Fact]
    public async Task Carries_a_half_empty_state_on_the_row_without_reading_it_as_absent()
    {
        // A rating with the status taken back off is a row that exists and says None. The feed
        // reads its state through a correlated subquery rather than the endpoint's own query, so
        // "a row whose leading column is the enum's zero" is a case only this path can get wrong.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await WatchAsync(Lexa, emma), 1);

        await _host.NewWorksRequest(emma).SetWorkState(1, new(null, 3, null), default);

        var row = Works(await _host.NewWorksRequest(emma).GetWorks(ct: default)).Items.Single();

        Assert.Equal(new WorkStateDto(nameof(ReadingStatus.None), 3, null), row.State);
    }

    [Fact]
    public async Task Never_shows_a_reader_another_readers_state_on_the_row()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var lexa = await WatchAsync(Lexa, emma);
        await SeedWorksAsync(lexa, 1);
        await WatchAsync(Lexa, sam);

        await _host.NewWorksRequest(sam).SetWorkState(1, new("Read", 10, "sam's note"), default);

        var row = Works(await _host.NewWorksRequest(emma).GetWorks(ct: default)).Items.Single();

        Assert.Equal(new WorkStateDto(nameof(ReadingStatus.None), null, null), row.State);
    }

    // ---- against the scraper -------------------------------------------------------------------

    [Fact]
    public async Task Survives_a_re_scrape_that_rewrites_the_works_metadata()
    {
        // The whole reason this is a table of its own. An ingest overwrites everything it parsed;
        // if it could reach a reader's own row, updated kudos would cost them their rating.
        var emma = _host.SeedUser();
        var lexa = await WatchAsync(Lexa, emma);
        await _host.IngestAsync(lexa, Page(Blurb(1, kudos: 10)));

        await _host.NewWorksRequest(emma).SetWorkState(1, new("Read", 8, "mine"), default);

        await _host.IngestAsync(lexa, Page(Blurb(1, kudos: 4321)));

        await using var db = _host.NewContext();
        Assert.Equal(4321, await db.Works.Where(w => w.Id == 1).Select(w => w.Kudos).SingleAsync());

        Assert.Equal(
            new WorkStateDto("Read", 8, "mine"),
            State(await _host.NewWorksRequest(emma).GetWorkState(1, default)));
    }

    // ---- fixture -------------------------------------------------------------------------------

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
            db.Works.Add(new Work { Id = id, Title = $"Work {id}" });
            db.ShipWorks.Add(new ShipWork { ShipId = shipId, WorkId = id });
        }

        await db.SaveChangesAsync();
    }

    private static string Page(params string[] blurbs) => $"""
        <html><body><ol class="work index group">
        {string.Join('\n', blurbs)}
        </ol></body></html>
        """;

    private static string Blurb(long id, int kudos) => $"""
        <li id="work_{id}" class="work blurb group">
          <div class="header module">
            <h4 class="heading">
              <a href="/works/{id}">Work {id}</a>
              by <a rel="author" href="/users/someuser/pseuds/somepseud">somepseud (someuser)</a>
            </h4>
            <!-- updated_at=1672531200 -->
            <p class="datetime">1 Jan 2023</p>
          </div>
          <ul class="tags commas">
            <li class="relationships"><a class="tag" href="/tags/lexa/works">{Lexa}</a></li>
          </ul>
          <dl class="stats">
            <dt class="words">Words:</dt><dd class="words">1,000</dd>
            <dt class="chapters">Chapters:</dt><dd class="chapters">1/1</dd>
            <dt class="kudos">Kudos:</dt><dd class="kudos">{kudos}</dd>
          </dl>
        </li>
        """;

    private static WorkStateDto State(ActionResult<WorkStateDto> result) =>
        Assert.IsType<WorkStateDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static PagedResult<WorkListItemDto> Works(ActionResult<PagedResult<WorkListItemDto>> result) =>
        Assert.IsType<PagedResult<WorkListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static bool Rejected<T>(ActionResult<T> result, string field)
    {
        var problem = Assert.IsType<ValidationProblemDetails>(
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        return problem.Errors.ContainsKey(field);
    }
}
