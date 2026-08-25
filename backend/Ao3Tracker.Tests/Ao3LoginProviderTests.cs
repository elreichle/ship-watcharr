using System.Net;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Credentials;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ao3Tracker.Tests;

/// <summary>
/// Deciding whether a login is needed at all.
///
/// This is the piece that keeps the archive from being asked twice an hour for a session it already
/// gave us. A healthy instance calls it every poll and sends nothing.
/// </summary>
public class Ao3LoginProviderTests : IDisposable
{
    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Logs_in_when_nothing_is_cached()
    {
        await _host.SaveAo3LoginAsync();

        Assert.True((await _host.EnsureAo3SessionAsync()).Success);

        Assert.Single(_host.Http.Posted);
    }

    [Fact]
    public async Task Asks_AO3_for_nothing_when_a_session_is_already_cached()
    {
        await _host.SaveAo3LoginAsync();
        await _host.EnsureAo3SessionAsync();
        _host.Http.Posted.Clear();
        _host.Http.LoginPagesRequested.Clear();

        Assert.True((await _host.EnsureAo3SessionAsync()).Success);

        Assert.Empty(_host.Http.LoginPagesRequested);
        Assert.Empty(_host.Http.Posted);
    }

    [Fact]
    public async Task Logs_in_again_once_the_cached_session_has_run_out()
    {
        _host.Clock.Now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            "", HttpStatusCode.Found, FromCache: false, FinalUrl: url,
            SetCookieHeaders: ["_otwarchive_session=logged-in; path=/; Max-Age=3600"],
            Location: "https://ao3.test/users/shipwatcharr");

        await _host.SaveAo3LoginAsync();
        await _host.EnsureAo3SessionAsync();
        _host.Http.Posted.Clear();

        _host.Clock.Now = _host.Clock.Now.AddHours(2);
        Assert.True((await _host.EnsureAo3SessionAsync()).Success);

        Assert.Single(_host.Http.Posted);
    }

    [Fact]
    public async Task Keeps_a_session_that_has_not_run_out_yet()
    {
        // The other side of the expiry check. A provider that re-logged-in whenever an expiry was
        // merely *present* would re-authenticate on every poll for a fortnight-long cookie.
        _host.Clock.Now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            "", HttpStatusCode.Found, FromCache: false, FinalUrl: url,
            SetCookieHeaders: ["_otwarchive_session=logged-in; path=/; Max-Age=3600"],
            Location: "https://ao3.test/users/shipwatcharr");

        await _host.SaveAo3LoginAsync();
        await _host.EnsureAo3SessionAsync();
        _host.Http.Posted.Clear();

        _host.Clock.Now = _host.Clock.Now.AddMinutes(30);
        await _host.EnsureAo3SessionAsync();

        Assert.Empty(_host.Http.Posted);
    }

    [Fact]
    public async Task Reports_the_refusal_when_AO3_will_not_sign_this_instance_in()
    {
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            Fixtures.Load(Fixtures.LoginPage), HttpStatusCode.OK, FromCache: false, FinalUrl: url);

        await _host.SaveAo3LoginAsync();

        var result = await _host.EnsureAo3SessionAsync();

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}

/// <summary>
/// The scope <see cref="Ao3SessionCache"/> reads and writes in.
///
/// It is reached from inside the shared HTTP client, which runs in whatever scope the current scrape
/// job holds — so a write performed through that scope would call <c>SaveChangesAsync</c> in the
/// middle of a walk and commit whatever the ingestor happened to have tracked. That is the failure
/// this constructs.
/// </summary>
public class Ao3LoginSessionScopeTests : IDisposable
{
    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Discarding_a_session_does_not_save_the_callers_pending_work()
    {
        await _host.SaveAo3LoginAsync();
        await _host.EnsureAo3SessionAsync();

        await _host.WithScopeAsync(async services =>
        {
            // A half-finished page, as the ingestor would leave it partway through a walk: tracked
            // by this scope's context and deliberately not saved yet.
            var db = services.GetRequiredService<AppDbContext>();
            db.Ships.Add(new Ship
            {
                CanonicalTagName = "Clarke Griffin/Lexa",
                CanonicalTagNameNormalized = "clarke griffin/lexa",
                TagUrlSegment = "Clarke%20Griffin*s*Lexa",
            });

            await services.GetRequiredService<IAo3SessionCache>().DiscardAsync();
        });

        await using var fresh = _host.NewContext();
        Assert.Empty(await fresh.Ships.ToListAsync());

        // And the discard itself still happened — the point is where it was written, not whether.
        Assert.Null(await _host.WithCredentialStoreAsync(store => store.GetSessionAsync()));
    }

    [Fact]
    public async Task Reading_the_session_does_not_save_the_callers_pending_work_either()
    {
        // GetUsableAsync only reads, but it reads through a DbContext; resolving the caller's would
        // put this on the same footing as the write the moment anything about it changed.
        await _host.SaveAo3LoginAsync();
        await _host.EnsureAo3SessionAsync();

        await _host.WithScopeAsync(async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            db.Ships.Add(new Ship
            {
                CanonicalTagName = "Bellamy Blake/Clarke Griffin",
                CanonicalTagNameNormalized = "bellamy blake/clarke griffin",
                TagUrlSegment = "Bellamy%20Blake*s*Clarke%20Griffin",
            });

            Assert.NotNull(await services.GetRequiredService<IAo3SessionCache>().GetUsableAsync());
        });

        await using var fresh = _host.NewContext();
        Assert.Empty(await fresh.Ships.ToListAsync());
    }
}

/// <summary>
/// What counts as a session worth attaching to a request, which is the one rule
/// <see cref="Ao3SessionCache"/> adds on top of the row.
/// </summary>
public class Ao3LoginSessionCacheTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    public void Is_usable_while_it_has_a_cookie_and_time_left(int? hoursLeft)
    {
        var session = new Ao3Session("s=abc", Now, hoursLeft is null ? null : Now.AddHours(hoursLeft.Value));

        Assert.True(session.IsUsableAt(Now));
    }

    [Fact]
    public void Is_not_usable_once_its_expiry_has_passed()
    {
        Assert.False(new Ao3Session("s=abc", Now, Now.AddHours(-1)).IsUsableAt(Now));
    }

    [Fact]
    public void Is_not_usable_at_the_very_moment_it_expires()
    {
        Assert.False(new Ao3Session("s=abc", Now, Now).IsUsableAt(Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Is_not_usable_without_a_cookie_to_send(string cookie)
    {
        // A row that says there is a session but holds nothing to attach would produce a request
        // that is anonymous while every log line called it authenticated.
        Assert.False(new Ao3Session(cookie, Now, null).IsUsableAt(Now));
    }
}
