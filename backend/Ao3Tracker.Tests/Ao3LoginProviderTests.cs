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
/// The two things <see cref="Ao3SessionProvider"/> owns that no round trip can show: that one login
/// happens however many callers want a session at once, and what instant the cooldown after a
/// refused one is measured from.
///
/// Both are about a class with two independent callers — the scrape worker and the download worker,
/// each on its own one-minute timer from host boot — so the establisher here is a stub whose timing
/// the test controls, rather than the real one over the fake archive.
/// </summary>
public class Ao3SessionProviderTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly StubSessionEstablisher _establisher = new();
    private readonly CountingSessionCache _sessions = new();
    private readonly LibraryTestHost _host;

    public Ao3SessionProviderTests()
    {
        _host = new LibraryTestHost(services =>
        {
            services.AddScoped<IAo3SessionEstablisher>(_ => _establisher);

            // The real cache over the real store, with a counter around it: what the second caller
            // has to be past before the first is allowed to finish is its own read of the cache.
            services.AddSingleton<IAo3SessionCache>(sp =>
            {
                _sessions.Inner = ActivatorUtilities.CreateInstance<Ao3SessionCache>(sp);
                return _sessions;
            });
        });

        _host.Clock.Now = Start;
    }

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Logs_in_once_when_two_callers_want_a_session_at_the_same_moment()
    {
        // Both workers poll from host boot, so with a queued download and no cached session both
        // observe "no session" in the same instant. Two logins is four rate-gated requests where
        // one was needed, it advances the backoff twice per cycle so its schedule skips rungs, and
        // Rails rotates the session on sign-in — so the first login's cookie is dead the moment the
        // second lands, and the request already in flight under it comes back logged out.
        await _host.SaveAo3LoginAsync();

        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        _establisher.OnLogIn = async () =>
        {
            inside.TrySetResult();
            await release.Task;

            await _host.WithCredentialStoreAsync(async store =>
            {
                await store.SetSessionAsync(new Ao3Session("_otwarchive_session=abc", Start.UtcDateTime, null));
                return true;
            });

            return new Ao3LoginResult(true);
        };

        var first = Task.Run(() => _host.EnsureAo3SessionAsync());
        await inside.Task;

        var readsBefore = _sessions.Reads;
        var second = Task.Run(() => _host.EnsureAo3SessionAsync());

        // The second caller has looked at the cache and found nothing, which is the state that used
        // to send it into a login of its own — and then a moment in which it could start one, while
        // the first is still parked inside its own and the cache it will repair is still empty.
        // Only then is the first one allowed to finish: released any earlier, a second login could
        // find the session already stored and the test would pass on the timing rather than on the
        // rule.
        await WaitUntilAsync(() => _sessions.Reads > readsBefore);
        await Task.Delay(250);
        release.SetResult();

        Assert.True((await first).Success);
        Assert.True((await second).Success);

        Assert.Equal(1, _establisher.Calls);
    }

    [Fact]
    public async Task Measures_the_cooldown_from_when_the_attempt_finished()
    {
        // The attempt is two rate-gated requests, each behind a 5-8s gate wait and a 30s transport
        // timeout. Timed from before it, the first cooldown expires early by however long it took —
        // longest in exactly the case the backoff exists for, an archive that is not answering.
        await _host.SaveAo3LoginAsync();

        _establisher.OnLogIn = () =>
        {
            _host.Clock.Now = _host.Clock.Now.AddSeconds(45);
            return Task.FromResult(new Ao3LoginResult(false, Error: "AO3 did not answer."));
        };

        Assert.False((await _host.EnsureAo3SessionAsync()).Success);

        // The first rung of the schedule, from where the attempt ended.
        Assert.Equal(Start.UtcDateTime.AddSeconds(45).AddMinutes(5), _host.LoginBackoff.RetryAfter);
    }

    [Fact]
    public async Task Measures_the_cooldown_check_against_the_clock_as_it_stands()
    {
        // The other half of reading the clock twice: the check and the record are different
        // questions about different instants, and a cooldown must still be honoured by a caller
        // that arrives while it is running.
        await _host.SaveAo3LoginAsync();
        _establisher.OnLogIn = () => Task.FromResult(new Ao3LoginResult(false, Error: "AO3 did not answer."));

        await _host.EnsureAo3SessionAsync();
        _host.Clock.Now = _host.Clock.Now.AddMinutes(1);

        var held = await _host.EnsureAo3SessionAsync();

        Assert.False(held.Success);
        Assert.Equal(1, _establisher.Calls);

        _host.Clock.Now = _host.Clock.Now.AddMinutes(5);
        await _host.EnsureAo3SessionAsync();

        Assert.Equal(2, _establisher.Calls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("The second caller never reached the session provider.");
    }
}

/// <summary>A login whose timing and answer the test decides, and which counts how often it ran.</summary>
internal sealed class StubSessionEstablisher : IAo3SessionEstablisher
{
    private int _calls;

    public Func<Task<Ao3LoginResult>> OnLogIn { get; set; } = () => Task.FromResult(new Ao3LoginResult(true));

    public int Calls => Volatile.Read(ref _calls);

    public Task<Ao3LoginResult> LogInAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        return OnLogIn();
    }
}

/// <summary>The real cache, counting reads, so a test can tell when another caller has looked.</summary>
internal sealed class CountingSessionCache : IAo3SessionCache
{
    private int _reads;

    public IAo3SessionCache Inner { get; set; } = null!;

    public int Reads => Volatile.Read(ref _reads);

    public Task<Ao3Session?> GetUsableAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _reads);
        return Inner.GetUsableAsync(ct);
    }

    public Task DiscardAsync(CancellationToken ct = default) => Inner.DiscardAsync(ct);
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
