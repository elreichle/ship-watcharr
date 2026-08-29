using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Ao3Tracker.Tests.Ao3ListingFixtures;

namespace Ao3Tracker.Tests;

/// <summary>
/// Being told that a ship you follow has gained a work.
///
/// Two halves, and the first carries all the risk. <b>What counts as news</b> is a judgement made
/// once, where works are ingested, and it is the only thing standing between a reader and four
/// thousand notifications the first time a backfill walks a large tag — so most of what follows is
/// about the passes that must stay silent rather than the one that speaks. <b>Whose notification
/// it is</b> is the second: the endpoints take ids, so a query that forgot its owner would not
/// merely leak a count, it would let one reader mark another's list read.
/// </summary>
public class NotificationTests : IDisposable
{
    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- what counts as news -------------------------------------------------------------------

    [Fact]
    public async Task An_incremental_pass_tells_a_watcher_about_a_work_the_ship_has_just_gained()
    {
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);

        await ScrapeAsync(Page(1, [Blurb(2, Jan(5)), Blurb(1)]));

        var row = Assert.Single(await NotificationsOfAsync(emma));
        Assert.Equal(2, row.WorkId);
        Assert.Equal(shipId, row.ShipId);
        Assert.Null(row.ReadAt);
    }

    [Fact]
    public async Task Says_nothing_about_a_work_the_ship_already_had()
    {
        // The pass re-reads page 1 every time it runs. Only the works it *adds* are news; a work
        // seen again is the ordinary case and must be silent, or every run would notify everyone
        // about the whole of page 1.
        //
        // Asserted through an announcing ingest rather than through a second scrape: the watermark
        // stops a second scrape before it ever reaches the ingestor, so a test written that way
        // would be agreeing with the stopping rule while this one went unchecked. It did, until a
        // mutation that announced every work on the page survived it.
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);

        await _host.IngestAsync(shipId, ListingOf(Blurb(1), Blurb(3, Jan(6))), announceToWatchers: true);

        Assert.Equal(3, Assert.Single(await NotificationsOfAsync(emma)).WorkId);
    }

    [Fact]
    public async Task Says_nothing_at_all_on_the_first_pass_over_a_newly_followed_ship()
    {
        // The back catalogue. A first incremental pass has no watermark to be newer than, so every
        // work in the tag reads as fresh — and following a tag must not be a way to receive one
        // notification per work in it.
        var emma = _host.SeedUser();

        await AShipHoldingAsync(emma, 1, 2, 3);

        Assert.Empty(await NotificationsOfAsync(emma));
    }

    [Fact]
    public async Task A_backfill_walking_into_the_back_catalogue_says_nothing()
    {
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);

        _host.Http.Responds = Pages(Page(1, [Blurb(2, Jan(5)), Blurb(3, Jan(6))]));
        await _host.ScrapeAsync(shipId, ScrapeRunMode.Backfill);

        Assert.Equal(3, await LinkCountAsync(shipId));
        Assert.Empty(await NotificationsOfAsync(emma));
    }

    [Fact]
    public async Task A_full_sweep_re_walking_the_listing_says_nothing()
    {
        // The sweep re-reads the whole tag, so anything it finds that the library is missing is a
        // gap being repaired rather than an arrival.
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);

        _host.Http.Responds = LoggedInPages(Page(1, [Blurb(1), Blurb(2, Jan(5))], total: 2));
        await _host.ScrapeAsync(shipId, ScrapeRunMode.FullSweep);

        Assert.Equal(2, await LinkCountAsync(shipId));
        Assert.Empty(await NotificationsOfAsync(emma));
    }

    [Fact]
    public async Task Tells_every_watcher_of_the_ship_separately()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        await AShipHoldingAsync(emma, 1);
        await WatchAsync(sam);

        await ScrapeAsync(Page(1, [Blurb(2, Jan(5)), Blurb(1)]));

        Assert.Single(await NotificationsOfAsync(emma));
        Assert.Single(await NotificationsOfAsync(sam));
    }

    [Fact]
    public async Task Tells_a_reader_who_followed_a_ship_somebody_else_was_already_following()
    {
        // The watermark belongs to the ship, not to the subscription — so a reader joining a tag
        // this instance already scrapes is told about its next arrival straight away, which is
        // right: the pass really is reporting an arrival rather than replaying a history.
        var emma = _host.SeedUser("emma");
        await AShipHoldingAsync(emma, 1);

        var sam = _host.SeedUser("sam");
        await WatchAsync(sam);

        await ScrapeAsync(Page(1, [Blurb(2, Jan(5)), Blurb(1)]));

        Assert.Equal(2, Assert.Single(await NotificationsOfAsync(sam)).WorkId);
    }

    [Fact]
    public async Task Honours_a_watcher_who_has_turned_notifications_off()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var shipId = await AShipHoldingAsync(emma, 1);
        await WatchAsync(sam);
        await SilenceAsync(sam, shipId);

        await ScrapeAsync(Page(1, [Blurb(2, Jan(5)), Blurb(1)]));

        Assert.Single(await NotificationsOfAsync(emma));
        Assert.Empty(await NotificationsOfAsync(sam));
    }

    [Fact]
    public async Task Leaves_the_works_in_the_library_of_a_watcher_who_has_turned_them_off()
    {
        // Off is "do not tell me", not "do not scrape for me". The distinction is the whole of what
        // the switch means, and getting it wrong would silently cost that reader the works.
        var emma = _host.SeedUser("emma");
        var shipId = await AShipHoldingAsync(emma, 1);
        await SilenceAsync(emma, shipId);

        await ScrapeAsync(Page(1, [Blurb(2, Jan(5)), Blurb(1)]));

        Assert.Equal(2, await LinkCountAsync(shipId));
        Assert.Empty(await NotificationsOfAsync(emma));
    }

    [Fact]
    public async Task An_ingest_that_is_not_announcing_writes_nothing_however_new_the_work_is()
    {
        // The ingestor's own half of the rule, without a scrape around it: it never announces
        // unless the caller says the pass was one whose finds are arrivals.
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);

        await _host.IngestAsync(shipId, ListingOf(Blurb(2, Jan(5))));

        Assert.Equal(2, await LinkCountAsync(shipId));
        Assert.Empty(await NotificationsOfAsync(emma));
    }

    // ---- the bound on the table ----------------------------------------------------------------

    [Fact]
    public async Task Keeps_only_the_newest_rows_a_reader_is_allowed()
    {
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);
        var oldest = await SeedNotificationsAsync(emma, shipId, workId: 1, count: Notification.MaxPerUser);

        await _host.IngestAsync(shipId, ListingOf(Blurb(2, Jan(5))), announceToWatchers: true);

        await using var db = _host.NewContext();
        Assert.Equal(Notification.MaxPerUser, await db.Notifications.CountAsync(n => n.UserId == emma.Id));
        Assert.False(await db.Notifications.AnyAsync(n => n.Id == oldest));
        Assert.True(await db.Notifications.AnyAsync(n => n.WorkId == 2));
    }

    [Fact]
    public async Task Caps_one_readers_list_without_touching_anybody_elses()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var shipId = await AShipHoldingAsync(emma, 1);
        await WatchAsync(sam);
        await SilenceAsync(sam, shipId);
        await SeedNotificationsAsync(sam, shipId, workId: 1, count: Notification.MaxPerUser + 5);
        await SeedNotificationsAsync(emma, shipId, workId: 1, count: Notification.MaxPerUser);

        await _host.IngestAsync(shipId, ListingOf(Blurb(2, Jan(5))), announceToWatchers: true);

        await using var db = _host.NewContext();
        Assert.Equal(Notification.MaxPerUser, await db.Notifications.CountAsync(n => n.UserId == emma.Id));

        // Sam was not notified, so nothing swept his list — the cap is applied to the readers a
        // page actually wrote to, not to every account on the instance on every page.
        Assert.Equal(Notification.MaxPerUser + 5, await db.Notifications.CountAsync(n => n.UserId == sam.Id));
    }

    // ---- reading the list ----------------------------------------------------------------------

    [Fact]
    public async Task Lists_the_newest_first_and_names_the_ship_and_the_work()
    {
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);

        await ScrapeAsync(Page(1, [Blurb(2, Jan(5)), Blurb(1)]));
        await ScrapeAsync(Page(1, [Blurb(3, Jan(6)), Blurb(2, Jan(5)), Blurb(1)]));

        var listed = await NotificationsOfAsync(emma);

        Assert.Equal([3L, 2L], [.. listed.Select(n => n.WorkId)]);
        Assert.Equal(Lexa, listed[0].ShipName);
        Assert.Equal("Work 3", listed[0].WorkTitle);
        Assert.Equal(shipId, listed[0].ShipId);
    }

    [Fact]
    public async Task Pages_the_list_and_reports_the_total()
    {
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);
        await SeedNotificationsAsync(emma, shipId, workId: 1, count: 3);

        var page = Listed(await _host.Notifications(emma).GetNotifications(page: 2, pageSize: 2, ct: default));

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Shows_one_reader_nothing_of_anothers()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var shipId = await AShipHoldingAsync(emma, 1);
        await SeedNotificationsAsync(emma, shipId, workId: 1, count: 2);

        Assert.Empty(await NotificationsOfAsync(sam));
        Assert.Equal(0, (await UnreadAsync(sam)).Unread);
    }

    // ---- unread, and marking read ---------------------------------------------------------------

    [Fact]
    public async Task Counts_what_has_not_been_read()
    {
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);
        await SeedNotificationsAsync(emma, shipId, workId: 1, count: 3);

        Assert.Equal(3, (await UnreadAsync(emma)).Unread);
    }

    [Fact]
    public async Task Marks_the_named_notifications_read_and_leaves_the_rest()
    {
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);
        await SeedNotificationsAsync(emma, shipId, workId: 1, count: 3);
        var ids = (await NotificationsOfAsync(emma)).Select(n => n.Id).ToList();

        var left = Unread(await _host.Notifications(emma).MarkRead(new([ids[0]]), default));

        Assert.Equal(2, left.Unread);

        var listed = await NotificationsOfAsync(emma);
        Assert.NotNull(listed.Single(n => n.Id == ids[0]).ReadAt);
        Assert.Null(listed.Single(n => n.Id == ids[1]).ReadAt);
    }

    [Fact]
    public async Task Narrows_the_list_to_what_is_unread_when_asked()
    {
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);
        await SeedNotificationsAsync(emma, shipId, workId: 1, count: 3);
        var ids = (await NotificationsOfAsync(emma)).Select(n => n.Id).ToList();

        await _host.Notifications(emma).MarkRead(new([ids[0]]), default);

        var page = Listed(await _host.Notifications(emma).GetNotifications(unreadOnly: true, ct: default));

        Assert.Equal(2, page.TotalCount);
        Assert.DoesNotContain(ids[0], page.Items.Select(n => n.Id));
    }

    [Fact]
    public async Task Marks_everything_read_at_once()
    {
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);
        await SeedNotificationsAsync(emma, shipId, workId: 1, count: 3);

        Assert.Equal(0, Unread(await _host.Notifications(emma).MarkAllRead(default)).Unread);
        Assert.All(await NotificationsOfAsync(emma), n => Assert.NotNull(n.ReadAt));
    }

    [Fact]
    public async Task Cannot_mark_another_readers_notification_read()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var shipId = await AShipHoldingAsync(emma, 1);
        await SeedNotificationsAsync(emma, shipId, workId: 1, count: 1);
        var hers = (await NotificationsOfAsync(emma)).Single().Id;

        await _host.Notifications(sam).MarkRead(new([hers]), default);
        await _host.Notifications(sam).MarkAllRead(default);

        Assert.Equal(1, (await UnreadAsync(emma)).Unread);
    }

    [Fact]
    public async Task Does_not_move_the_timestamp_on_a_notification_already_read()
    {
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);
        await SeedNotificationsAsync(emma, shipId, workId: 1, count: 1);
        var id = (await NotificationsOfAsync(emma)).Single().Id;

        await _host.Notifications(emma).MarkRead(new([id]), default);
        var first = (await NotificationsOfAsync(emma)).Single().ReadAt;

        await _host.Notifications(emma).MarkAllRead(default);

        Assert.Equal(first, (await NotificationsOfAsync(emma)).Single().ReadAt);
    }

    [Fact]
    public async Task Rejects_a_mark_read_that_names_nothing()
    {
        var emma = _host.SeedUser();

        Assert.True(Rejected(await _host.Notifications(emma).MarkRead(new([]), default), "Ids"));
        Assert.True(Rejected(await _host.Notifications(emma).MarkRead(new(null), default), "Ids"));
    }

    [Fact]
    public async Task Rejects_a_mark_read_naming_more_notifications_than_a_reader_can_hold()
    {
        var emma = _host.SeedUser();
        var tooMany = Enumerable.Range(1, Notification.MaxPerUser + 1).ToList();

        Assert.True(Rejected(await _host.Notifications(emma).MarkRead(new(tooMany), default), "Ids"));
    }

    [Fact]
    public async Task Answers_a_page_number_too_large_to_offset_with_an_empty_page()
    {
        // (page - 1) * pageSize overflows int, and a negative offset is a provider error rather
        // than the empty page that is past the end of any list.
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);
        await SeedNotificationsAsync(emma, shipId, workId: 1, count: 1);

        var page = Listed(await _host.Notifications(emma)
            .GetNotifications(page: int.MaxValue, pageSize: 100, ct: default));

        Assert.Empty(page.Items);
        Assert.Equal(1, page.TotalCount);
    }

    // ---- when the subscription ends --------------------------------------------------------------

    [Fact]
    public async Task Unfollowing_a_ship_takes_its_notifications_with_it()
    {
        var emma = _host.SeedUser();
        var shipId = await AShipHoldingAsync(emma, 1);
        await ScrapeAsync(Page(1, [Blurb(2, Jan(5)), Blurb(1)]));

        await _host.Ships(emma).UnwatchShip(shipId, default);

        Assert.Equal(0, (await UnreadAsync(emma)).Unread);
        Assert.Empty(await NotificationsOfAsync(emma));
    }

    [Fact]
    public async Task Unfollowing_leaves_another_watchers_notifications_alone()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var shipId = await AShipHoldingAsync(emma, 1);
        await WatchAsync(sam);
        await ScrapeAsync(Page(1, [Blurb(2, Jan(5)), Blurb(1)]));

        await _host.Ships(emma).UnwatchShip(shipId, default);

        Assert.Single(await NotificationsOfAsync(sam));
    }

    // ---- fixture ---------------------------------------------------------------------------------

    /// <summary>
    /// A followed ship whose first incremental pass has already been and gone, so it has the
    /// watermark every "and then it gained a work" test needs — and so the back catalogue those
    /// works represent has already been ingested in silence.
    /// </summary>
    private async Task<int> AShipHoldingAsync(ApplicationUser watcher, params long[] workIds)
    {
        var shipId = await WatchAsync(watcher);

        _host.Http.Responds = Pages(Page(1, [.. workIds.Select(id => Blurb(id))]));
        await _host.ScrapeAsync(shipId);

        Assert.Equal(workIds.Length, await LinkCountAsync(shipId));
        return shipId;
    }

    private async Task<int> WatchAsync(ApplicationUser watcher)
    {
        var result = await _host.Ships(watcher).WatchShip(new(Lexa), CancellationToken.None);
        return Assert.IsType<WatchedShipDto>(
            Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    /// <summary>One incremental pass over the pages given, the way the worker runs them.</summary>
    private async Task ScrapeAsync(params FakePage[] pages)
    {
        _host.Http.Responds = Pages(pages);

        await using var db = _host.NewContext();
        var shipId = await db.Ships.Select(s => s.Id).SingleAsync();

        await _host.ScrapeAsync(shipId);
    }

    /// <summary>Turns one watcher's notifications off, which nothing but this switch does.</summary>
    private async Task SilenceAsync(ApplicationUser watcher, int shipId)
    {
        await using var db = _host.NewContext();

        var watch = await db.WatchedShips.SingleAsync(w => w.UserId == watcher.Id && w.ShipId == shipId);
        watch.NotificationsEnabled = false;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Rows written straight to the table, for the reads and the cap — which need a list of a given
    /// length rather than the pass that would have produced one.
    /// </summary>
    /// <returns>The id of the oldest row seeded.</returns>
    private async Task<int> SeedNotificationsAsync(ApplicationUser user, int shipId, long workId, int count)
    {
        await using var db = _host.NewContext();

        var rows = Enumerable.Range(0, count)
            .Select(_ => new Notification
            {
                UserId = user.Id,
                ShipId = shipId,
                WorkId = workId,
                CreatedAt = _host.Clock.Now.UtcDateTime,
            })
            .ToList();

        db.Notifications.AddRange(rows);
        await db.SaveChangesAsync();

        return rows[0].Id;
    }

    private async Task<IReadOnlyList<NotificationDto>> NotificationsOfAsync(ApplicationUser user) =>
        Listed(await _host.Notifications(user).GetNotifications(pageSize: 100, ct: default)).Items;

    private async Task<UnreadNotificationsDto> UnreadAsync(ApplicationUser user) =>
        Unread(await _host.Notifications(user).GetUnreadCount(default));

    private async Task<int> LinkCountAsync(int shipId)
    {
        await using var db = _host.NewContext();
        return await db.ShipWorks.CountAsync(sw => sw.ShipId == shipId);
    }

    private static string ListingOf(params string[] blurbs) => Page(1, blurbs).Html;

    private static PagedResult<NotificationDto> Listed(ActionResult<PagedResult<NotificationDto>> result) =>
        Assert.IsType<PagedResult<NotificationDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static UnreadNotificationsDto Unread(ActionResult<UnreadNotificationsDto> result) =>
        Assert.IsType<UnreadNotificationsDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static bool Rejected<T>(ActionResult<T> result, string field)
    {
        var problem = Assert.IsType<ValidationProblemDetails>(
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        return problem.Errors.ContainsKey(field);
    }
}
