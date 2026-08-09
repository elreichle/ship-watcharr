using System.Net;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// Checking a followed tag against AO3.
///
/// The interesting half is not "does it 404" but what happens when AO3 answers a *different* tag
/// than the one asked for. That is a synonym, and left alone it produces exactly the duplicate
/// fetching the shared-ship design exists to prevent: two spellings of one pairing, two ships, two
/// schedules, two walks over identical works.
/// </summary>
public class ShipVerifierTests : IDisposable
{
    private const string Lexa = "Clarke Griffin/Lexa";
    private const string LexaSegment = "Clarke%20Griffin*s*Lexa";
    private const string Bellarke = "Bellamy Blake/Clarke Griffin";

    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- the tag exists ------------------------------------------------------------------------

    [Fact]
    public async Task Asks_AO3_for_the_tags_own_works_index()
    {
        var shipId = await FollowAsync(_host.SeedUser(), Lexa);

        await _host.VerifyAsync(shipId);

        Assert.Equal([LibraryTestHost.TagUrl(LexaSegment)], _host.Http.Requested);
    }

    [Fact]
    public async Task Marks_a_real_tag_verified()
    {
        var shipId = await FollowAsync(_host.SeedUser(), Lexa);

        var result = await _host.VerifyAsync(shipId);

        Assert.Equal(ShipVerificationOutcome.Verified, result.Outcome);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipVerificationState.Verified, ship.VerificationState);
        Assert.NotNull(ship.VerificationCheckedAt);
        Assert.Null(ship.VerificationError);
    }

    [Fact]
    public async Task Harvests_the_numeric_tag_id_from_the_feed_link()
    {
        // The id is what makes a tag addressable in a way that survives AO3 renaming it.
        _host.Http.Responds = url => Ok(url, """
            <link rel="alternate" type="application/rss+xml" href="/tags/48371/feed.atom" />
            """);

        var shipId = await FollowAsync(_host.SeedUser(), Lexa);
        await _host.VerifyAsync(shipId);

        Assert.Equal(48371, (await ReloadAsync(shipId)).Ao3TagId);
    }

    [Fact]
    public async Task Verifies_even_when_no_tag_id_can_be_found()
    {
        // The id is a bonus, never a condition. A markup change that hides it must not turn every
        // verification into a failure.
        var shipId = await FollowAsync(_host.SeedUser(), Lexa);

        await _host.VerifyAsync(shipId);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipVerificationState.Verified, ship.VerificationState);
        Assert.Null(ship.Ao3TagId);
    }

    [Fact]
    public async Task Does_not_re_check_a_ship_that_is_already_settled()
    {
        var shipId = await FollowAsync(_host.SeedUser(), Lexa);
        await _host.VerifyAsync(shipId);
        _host.Http.Requested.Clear();

        await _host.VerifyAsync(shipId);

        Assert.Empty(_host.Http.Requested);
    }

    // ---- the tag does not exist ----------------------------------------------------------------

    [Fact]
    public async Task Records_a_404_as_the_tag_not_existing()
    {
        // The typo case, and the whole reason this feature exists.
        _host.Http.Responds = _ => new ScrapeHttpResponse("", HttpStatusCode.NotFound, false);

        var shipId = await FollowAsync(_host.SeedUser(), "Clarke Griffen/Lexa");
        var result = await _host.VerifyAsync(shipId);

        Assert.Equal(ShipVerificationOutcome.NotFound, result.Outcome);
        Assert.Equal(ShipVerificationState.NotFoundOnAo3, (await ReloadAsync(shipId)).VerificationState);
    }

    [Fact]
    public async Task Stops_scheduling_a_tag_that_does_not_exist()
    {
        // Otherwise the schedule keeps a guaranteed-404 request on the books forever.
        _host.Http.Responds = _ => new ScrapeHttpResponse("", HttpStatusCode.NotFound, false);

        var shipId = await FollowAsync(_host.SeedUser(), "Clarke Griffen/Lexa");
        await _host.VerifyAsync(shipId);

        await using var db = _host.NewContext();
        Assert.False((await db.ScrapeJobs.SingleAsync(j => j.ShipId == shipId)).IsEnabled);
    }

    [Fact]
    public async Task Following_a_known_missing_tag_again_does_not_re_enable_its_schedule()
    {
        _host.Http.Responds = _ => new ScrapeHttpResponse("", HttpStatusCode.NotFound, false);

        var emma = _host.SeedUser("emma");
        var shipId = await FollowAsync(emma, "Clarke Griffen/Lexa");
        await _host.VerifyAsync(shipId);

        await FollowAsync(_host.SeedUser("sam"), "Clarke Griffen/Lexa");

        await using var db = _host.NewContext();
        Assert.False((await db.ScrapeJobs.SingleAsync(j => j.ShipId == shipId)).IsEnabled);
    }

    // ---- inconclusive checks -------------------------------------------------------------------

    [Fact]
    public async Task Leaves_a_ship_pending_when_AO3_is_unreachable()
    {
        // An archive being down says nothing about a tag, so this must never become "not found".
        _host.Http.Fails = new HttpRequestException("Connection refused");

        var shipId = await FollowAsync(_host.SeedUser(), Lexa);
        var result = await _host.VerifyAsync(shipId);

        Assert.Equal(ShipVerificationOutcome.Inconclusive, result.Outcome);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipVerificationState.Pending, ship.VerificationState);
        Assert.Equal("Connection refused", ship.VerificationError);
        Assert.Equal(1, ship.VerificationAttempts);
        Assert.NotNull(ship.NextVerificationAttemptAt);
    }

    [Fact]
    public async Task Backs_off_further_on_each_successive_failure()
    {
        _host.Http.Fails = new HttpRequestException("Connection refused");
        var shipId = await FollowAsync(_host.SeedUser(), Lexa);

        await _host.VerifyAsync(shipId);
        var first = (await ReloadAsync(shipId)).NextVerificationAttemptAt;

        await _host.VerifyAsync(shipId);
        var ship = await ReloadAsync(shipId);

        Assert.Equal(2, ship.VerificationAttempts);
        Assert.True(ship.NextVerificationAttemptAt > first);
    }

    [Fact]
    public void Caps_the_retry_backoff()
    {
        // Deliberately capped rather than abandoned: there is no attempt count at which an
        // unreachable archive becomes evidence that a tag is missing.
        Assert.Equal(TimeSpan.FromMinutes(2), Ao3ShipVerifier.RetryDelay(1));
        Assert.Equal(TimeSpan.FromHours(6), Ao3ShipVerifier.RetryDelay(50));
    }

    [Fact]
    public async Task Treats_a_server_error_as_inconclusive()
    {
        _host.Http.Responds = _ => new ScrapeHttpResponse("", HttpStatusCode.ServiceUnavailable, false);

        var shipId = await FollowAsync(_host.SeedUser(), Lexa);

        Assert.Equal(ShipVerificationOutcome.Inconclusive, (await _host.VerifyAsync(shipId)).Outcome);
        Assert.Equal("AO3 returned 503.", (await ReloadAsync(shipId)).VerificationError);
    }

    [Fact]
    public async Task Refuses_to_conclude_anything_from_a_redirect_off_the_tag_index()
    {
        // A redirect to a login or error page describes something other than the tag. Reading a
        // canonical name out of it would rename the user's ship to whatever that page was.
        _host.Http.Responds = _ => Ok($"{LibraryTestHost.BaseUrl}/users/login");

        var shipId = await FollowAsync(_host.SeedUser(), Lexa);
        var result = await _host.VerifyAsync(shipId);

        Assert.Equal(ShipVerificationOutcome.Inconclusive, result.Outcome);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(ShipVerificationState.Pending, ship.VerificationState);
        Assert.Equal(Lexa, ship.CanonicalTagName);
    }

    [Fact]
    public async Task Never_reaches_AO3_without_an_operator_contact()
    {
        // Fail-closed: an instance that cannot say who to contact does not get to make requests,
        // and this path must not quietly become an exception to that.
        _host.OperatorContact = null;
        var shipId = await FollowAsync(_host.SeedUser(), Lexa);

        await _host.RunVerificationTickAsync();

        Assert.Empty(_host.Http.Requested);

        // And no attempt is burned, so the backoff isn't pushed out by a failure that was never
        // about the tag.
        Assert.Equal(0, (await ReloadAsync(shipId)).VerificationAttempts);
    }

    // ---- what the worker picks up --------------------------------------------------------------

    [Fact]
    public async Task The_worker_checks_a_newly_followed_tag()
    {
        await FollowAsync(_host.SeedUser(), Lexa);

        await _host.RunVerificationTickAsync();

        Assert.Equal([LibraryTestHost.TagUrl(LexaSegment)], _host.Http.Requested);
    }

    [Fact]
    public async Task The_worker_leaves_a_backed_off_ship_alone_until_it_is_due()
    {
        // Without the due check, a ship that failed once would be retried every single minute —
        // the backoff would be recorded and then ignored.
        _host.Http.Fails = new HttpRequestException("Connection refused");
        var shipId = await FollowAsync(_host.SeedUser(), Lexa);
        await _host.VerifyAsync(shipId);

        _host.Http.Fails = null;
        _host.Http.Requested.Clear();

        await _host.RunVerificationTickAsync();

        Assert.Empty(_host.Http.Requested);
        Assert.Equal(ShipVerificationState.Pending, (await ReloadAsync(shipId)).VerificationState);
    }

    [Fact]
    public async Task The_worker_retries_once_the_backoff_has_elapsed()
    {
        _host.Http.Fails = new HttpRequestException("Connection refused");
        var shipId = await FollowAsync(_host.SeedUser(), Lexa);
        await _host.VerifyAsync(shipId);

        await using (var db = _host.NewContext())
        {
            var ship = await db.Ships.SingleAsync(s => s.Id == shipId);
            ship.NextVerificationAttemptAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        _host.Http.Fails = null;
        await _host.RunVerificationTickAsync();

        Assert.Equal(ShipVerificationState.Verified, (await ReloadAsync(shipId)).VerificationState);
    }

    [Fact]
    public async Task The_worker_ignores_ships_that_are_already_settled()
    {
        var shipId = await FollowAsync(_host.SeedUser(), Lexa);
        await _host.VerifyAsync(shipId);
        _host.Http.Requested.Clear();

        await _host.RunVerificationTickAsync();

        Assert.Empty(_host.Http.Requested);
    }

    [Fact]
    public async Task The_worker_caps_how_many_ships_one_tick_checks()
    {
        // Each check is a real request through the shared rate gate, so a batch of new follows must
        // not be able to monopolise it ahead of scraping.
        var emma = _host.SeedUser();
        for (var i = 0; i < 13; i++) await FollowAsync(emma, $"Character {i}/Someone");

        await _host.RunVerificationTickAsync();

        Assert.Equal(10, _host.Http.Requested.Count);
    }

    // ---- synonyms ------------------------------------------------------------------------------

    [Fact]
    public async Task Renames_a_synonym_to_the_canonical_tag()
    {
        _host.Http.Responds = _ => Ok(LibraryTestHost.TagUrl(Ao3TagUrl.ToUrlSegment(Bellarke)));

        var shipId = await FollowAsync(_host.SeedUser(), "Bellarke");
        var result = await _host.VerifyAsync(shipId);

        Assert.Equal(ShipVerificationOutcome.RenamedToCanonical, result.Outcome);

        var ship = await ReloadAsync(shipId);
        Assert.Equal(Bellarke, ship.CanonicalTagName);
        Assert.Equal(Bellarke.ToUpperInvariant(), ship.CanonicalTagNameNormalized);
        Assert.Equal(Ao3TagUrl.ToUrlSegment(Bellarke), ship.TagUrlSegment);
        Assert.Equal(ShipVerificationState.Verified, ship.VerificationState);
    }

    [Fact]
    public async Task Remembers_what_the_user_actually_typed_when_renaming()
    {
        // Without this the tag someone entered silently turns into a different string, which reads
        // as the app having ignored them.
        _host.Http.Responds = _ => Ok(LibraryTestHost.TagUrl(Ao3TagUrl.ToUrlSegment(Bellarke)));

        var shipId = await FollowAsync(_host.SeedUser(), "Bellarke");
        await _host.VerifyAsync(shipId);

        await using var db = _host.NewContext();
        Assert.Equal("Bellarke", (await db.WatchedShips.SingleAsync()).RequestedTagName);
    }

    [Fact]
    public async Task Leaves_the_requested_name_alone_when_nothing_was_renamed()
    {
        var shipId = await FollowAsync(_host.SeedUser(), Lexa);
        await _host.VerifyAsync(shipId);

        await using var db = _host.NewContext();
        Assert.Null((await db.WatchedShips.SingleAsync()).RequestedTagName);
    }

    [Fact]
    public async Task Merges_a_synonym_into_an_existing_canonical_ship()
    {
        // The duplicate-fetch case: two spellings already followed, which must end as one ship.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");

        var canonicalId = await FollowAsync(emma, Bellarke);
        var synonymId = await FollowAsync(sam, "Bellarke");

        _host.Http.Responds = _ => Ok(LibraryTestHost.TagUrl(Ao3TagUrl.ToUrlSegment(Bellarke)));
        var result = await _host.VerifyAsync(synonymId);

        Assert.Equal(ShipVerificationOutcome.MergedIntoCanonical, result.Outcome);
        Assert.Equal(canonicalId, result.ShipId);

        await using var db = _host.NewContext();
        Assert.Equal(1, await db.Ships.CountAsync());
        Assert.Equal(1, await db.ScrapeJobs.CountAsync());

        // Both watchers survive, now pointing at the one ship.
        Assert.Equal(2, await db.WatchedShips.CountAsync(w => w.ShipId == canonicalId));
        Assert.Equal("Bellarke", (await db.WatchedShips.SingleAsync(w => w.UserId == sam.Id)).RequestedTagName);
    }

    [Fact]
    public async Task Does_not_leave_a_user_watching_the_same_ship_twice()
    {
        // Someone who followed both spellings would otherwise end up with two subscriptions to one
        // ship, which the unique index on (UserId, ShipId) rejects outright.
        var emma = _host.SeedUser();
        var canonicalId = await FollowAsync(emma, Bellarke);
        var synonymId = await FollowAsync(emma, "Bellarke");

        _host.Http.Responds = _ => Ok(LibraryTestHost.TagUrl(Ao3TagUrl.ToUrlSegment(Bellarke)));
        await _host.VerifyAsync(synonymId);

        await using var db = _host.NewContext();
        Assert.Equal(1, await db.WatchedShips.CountAsync());
        Assert.Equal(canonicalId, (await db.WatchedShips.SingleAsync()).ShipId);
    }

    [Fact]
    public async Task Moves_the_synonyms_works_onto_the_canonical_ship()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var canonicalId = await FollowAsync(emma, Bellarke);
        var synonymId = await FollowAsync(sam, "Bellarke");

        await using (var db = _host.NewContext())
        {
            db.Works.AddRange(
                new Work { Id = 1, Title = "Shared" },
                new Work { Id = 2, Title = "Only on the synonym" });
            db.ShipWorks.AddRange(
                new ShipWork { ShipId = canonicalId, WorkId = 1 },
                new ShipWork { ShipId = synonymId, WorkId = 1 },
                new ShipWork { ShipId = synonymId, WorkId = 2 });
            await db.SaveChangesAsync();
        }

        _host.Http.Responds = _ => Ok(LibraryTestHost.TagUrl(Ao3TagUrl.ToUrlSegment(Bellarke)));
        await _host.VerifyAsync(synonymId);

        await using var after = _host.NewContext();

        // Work 2 moves across; work 1 was on both, so it must not become a duplicate row.
        Assert.Equal([1L, 2L], await after.ShipWorks.Select(sw => sw.WorkId).Order().ToListAsync());
        Assert.True(await after.ShipWorks.AllAsync(sw => sw.ShipId == canonicalId));

        // The works themselves are global and shared — a merge must not delete any.
        Assert.Equal(2, await after.Works.CountAsync());
    }

    [Fact]
    public async Task Verifies_the_canonical_ship_as_part_of_the_merge()
    {
        // AO3 just answered for the canonical tag, so re-asking would spend a request re-learning
        // something this response already proved.
        var emma = _host.SeedUser("emma");
        var canonicalId = await FollowAsync(emma, Bellarke);
        var synonymId = await FollowAsync(_host.SeedUser("sam"), "Bellarke");

        _host.Http.Responds = _ => Ok(LibraryTestHost.TagUrl(Ao3TagUrl.ToUrlSegment(Bellarke)));
        await _host.VerifyAsync(synonymId);

        Assert.Equal(ShipVerificationState.Verified, (await ReloadAsync(canonicalId)).VerificationState);
    }

    // ---- fixture -------------------------------------------------------------------------------

    /// <summary>200 with no body, redirected to <paramref name="finalUrl"/>.</summary>
    private static ScrapeHttpResponse Ok(string finalUrl, string content = "") =>
        new(content, HttpStatusCode.OK, FromCache: false, FinalUrl: finalUrl);

    private async Task<int> FollowAsync(ApplicationUser user, string tagName)
    {
        var result = await _host.Ships(user).WatchShip(new(tagName), default);
        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    private async Task<Ship> ReloadAsync(int shipId)
    {
        await using var db = _host.NewContext();
        return await db.Ships.AsNoTracking().SingleAsync(s => s.Id == shipId);
    }
}
