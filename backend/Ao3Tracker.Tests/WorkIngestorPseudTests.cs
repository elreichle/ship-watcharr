using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// Who a work is by, and what a work is tagged with, across re-reads of the same listing.
///
/// Both are resolved the same way: a page's blurbs are turned into keys, existing rows are looked
/// up by those keys, and whatever is missing is inserted. The failure this pins is what happens
/// when the key the lookup uses and the key the row was stored under are not the same string — a
/// missed match means an insert, and an insert of something that already exists fails the whole
/// page's save on a unique index.
/// </summary>
public class WorkIngestorPseudTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";

    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Re_seeing_an_author_under_a_different_case_reuses_their_row()
    {
        // AO3 renders a byline as the creator typed it, and creators re-type it. Two rows for one
        // account would split their works in the library and, because the unique key is on the
        // normalized pair, fail this ingest outright.
        var shipId = await FollowAsync();

        await _host.IngestAsync(shipId, Page(Blurb(1, username: "someuser", pseud: "somepseud")));
        await _host.IngestAsync(shipId, Page(Blurb(2, username: "SomeUser", pseud: "SomePseud")));

        await using var db = _host.NewContext();
        var pseud = Assert.Single(await db.Ao3Pseuds.ToListAsync());

        // The first spelling is kept: it is the one already on display everywhere.
        Assert.Equal("someuser", pseud.Username);
        Assert.Equal("SOMEUSER", pseud.UsernameNormalized);
        Assert.Equal(2, await db.WorkAuthors.CountAsync(wa => wa.PseudId == pseud.Id));
    }

    [Fact]
    public async Task An_author_seen_twice_in_one_page_is_one_row()
    {
        var shipId = await FollowAsync();

        await _host.IngestAsync(shipId, Page(
            Blurb(1, username: "someuser", pseud: "somepseud"),
            Blurb(2, username: "SOMEUSER", pseud: "somepseud")));

        await using var db = _host.NewContext();
        Assert.Single(await db.Ao3Pseuds.ToListAsync());
    }

    [Fact]
    public async Task A_tag_too_long_for_its_column_still_lands_on_the_work()
    {
        // AO3 freeforms are famously sentences. The row stores the tag truncated, so a lookup built
        // from the untruncated name misses it — and a miss here is silent: the tag is simply left
        // off the work, and taken off again on every later pass.
        var monster = new string('x', 250);
        var shipId = await FollowAsync();

        await _host.IngestAsync(shipId, Page(Blurb(1, freeforms: [monster])));

        await using var db = _host.NewContext();
        var tag = await db.Tags.SingleAsync(t => t.Name.StartsWith("xxx"));

        Assert.Equal(200, tag.Name.Length);
        Assert.True(await db.WorkTags.AnyAsync(wt => wt.WorkId == 1 && wt.TagId == tag.Id));
    }

    [Fact]
    public async Task A_long_tag_survives_a_second_pass_over_the_same_work()
    {
        // The reconcile step is what makes a missed lookup destructive rather than merely lossy:
        // the second pass sees "this work should have no such tag" and deletes the link.
        var monster = new string('y', 250);
        var shipId = await FollowAsync();

        await _host.IngestAsync(shipId, Page(Blurb(1, freeforms: [monster])));
        await _host.IngestAsync(shipId, Page(Blurb(1, freeforms: [monster])));

        await using var db = _host.NewContext();
        Assert.Equal(1, await db.WorkTags.CountAsync(wt => wt.WorkId == 1 && wt.Tag.Name.StartsWith("yyy")));
    }

    [Fact]
    public async Task An_author_name_too_long_for_its_column_still_lands_on_the_work()
    {
        var longUser = new string('z', 130);
        var shipId = await FollowAsync();

        await _host.IngestAsync(shipId, Page(Blurb(1, username: longUser, pseud: longUser)));
        await _host.IngestAsync(shipId, Page(Blurb(1, username: longUser, pseud: longUser)));

        await using var db = _host.NewContext();
        var pseud = Assert.Single(await db.Ao3Pseuds.ToListAsync());

        Assert.Equal(100, pseud.Username.Length);
        Assert.Equal(1, await db.WorkAuthors.CountAsync(wa => wa.PseudId == pseud.Id));
    }

    private async Task<int> FollowAsync()
    {
        var result = await _host.Ships(_host.SeedUser()).WatchShip(new(Lexa), default);
        return Assert.IsType<WatchedShipDto>(
            Assert.IsType<Microsoft.AspNetCore.Mvc.CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    private static string Page(params string[] blurbs) => $"""
        <html><body><ol class="work index group">
        {string.Join('\n', blurbs)}
        </ol></body></html>
        """;

    private static string Blurb(
        long id,
        string username = "someuser",
        string pseud = "somepseud",
        string[]? freeforms = null)
    {
        var tags = string.Join('\n', (freeforms ?? ["Fluff"])
            .Select(f => $"""<li class="freeforms"><a class="tag" href="/tags/{f}/works">{f}</a></li>"""));

        return $"""
            <li id="work_{id}" class="work blurb group">
              <div class="header module">
                <h4 class="heading">
                  <a href="/works/{id}">Work {id}</a>
                  by <a rel="author" href="/users/{username}/pseuds/{pseud}">{pseud} ({username})</a>
                </h4>
                <!-- updated_at=1672531200 -->
                <p class="datetime">1 Jan 2023</p>
              </div>
              <ul class="tags commas">
                <li class="relationships"><a class="tag" href="/tags/lexa/works">{Lexa}</a></li>
                {tags}
              </ul>
              <dl class="stats">
                <dt class="words">Words:</dt><dd class="words">1,000</dd>
                <dt class="chapters">Chapters:</dt><dd class="chapters">1/1</dd>
                <dt class="kudos">Kudos:</dt><dd class="kudos">10</dd>
              </dl>
            </li>
            """;
    }
}
