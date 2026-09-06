using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Tests;

/// <summary>
/// What one request learning that AO3 wants quiet does for every other request on the instance.
///
/// Production showed AO3's 429s counting down to one deadline per penalty window, and this
/// instance treating each as a private matter between AO3 and the request that drew it: the next
/// ship's page went out into the same window seconds later and drew its own, fifteen ships in a
/// row. The gate now records the ask once, and these pin that nothing — at any priority, for any
/// URL — goes out before it.
/// </summary>
public class Ao3RateGateTests : IDisposable
{
    private const string Url = "https://ao3.test/tags/lexa/works";
    private const string OtherUrl = "https://ao3.test/works/1";

    private readonly string _dataDirectory =
        Directory.CreateTempSubdirectory("ship-watcharr-gate-").FullName;

    private readonly StubArchive _archive = new();
    private readonly StubSessionCache _sessions = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the test is untidy, never a failure.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_429_holds_every_later_request_until_the_deadline_AO3_named()
    {
        // The first request draws a 429 asking for 600ms and has no retries left, so it comes
        // straight back as the failure it is. The second is for something else entirely, and
        // before the hold it would have gone out at once — into the same window.
        var hold = TimeSpan.FromMilliseconds(600);
        _archive.Answers = request => request.RequestUri!.ToString() == Url
            ? Retryable(HttpStatusCode.TooManyRequests, hold)
            : Ok();

        var (client, _) = Client(maxRetries: 0);
        var clock = Stopwatch.StartNew();

        var first = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);

        var other = await client.GetAsync(OtherUrl);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        Assert.True(clock.Elapsed >= hold, $"The second request went out after {clock.Elapsed}, inside the {hold} AO3 asked for.");
    }

    [Fact]
    public async Task An_ask_past_the_retry_ceiling_still_holds_the_instance()
    {
        // The ceiling bounds how long one request waits for itself. It must not bound how long the
        // instance stays quiet: a request that gives up and a ship that fires into the window it
        // gave up on are different things, and only the first is what the ceiling is for.
        var hold = TimeSpan.FromMilliseconds(600);
        _archive.Answers = request => request.RequestUri!.ToString() == Url
            ? Retryable(HttpStatusCode.TooManyRequests, hold)
            : Ok();

        var (client, gate) = Client(maxRetryAfter: TimeSpan.FromMilliseconds(50));
        var clock = Stopwatch.StartNew();

        var first = await client.GetAsync(Url);
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Single(_archive.Received);
        Assert.NotNull(gate.HeldUntil);

        await client.GetAsync(OtherUrl);
        Assert.True(clock.Elapsed >= hold, $"The second request went out after {clock.Elapsed}, inside the {hold} AO3 asked for.");
    }

    [Fact]
    public async Task The_hold_is_capped_so_one_bad_header_cannot_park_the_instance_for_good()
    {
        // An hour asked for, a 300ms cap configured: the instance parks for the cap, not the hour.
        _archive.Answers = request => request.RequestUri!.ToString() == Url
            ? Retryable(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(1))
            : Ok();

        var (client, _) = Client(maxRetries: 0, maxThrottleHold: TimeSpan.FromMilliseconds(300));

        await client.GetAsync(Url);
        var other = await client.GetAsync(OtherUrl).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task A_5xx_asks_nothing_of_the_instance()
    {
        // The archive struggling with a page is that page's problem, answered by its own retry
        // backoff. RateLimitedRetryTests pins that the wait does not hold the gate; this pins that
        // it records no hold either.
        _archive.Answers = _ => Retryable(HttpStatusCode.ServiceUnavailable, TimeSpan.FromHours(1));

        var (client, gate) = Client(maxRetries: 0);

        await client.GetAsync(Url);

        Assert.Null(gate.HeldUntil);
    }

    [Fact]
    public async Task Waiters_are_served_by_priority_and_then_by_arrival()
    {
        var gate = NewGate();

        // The channel is taken, and three requests queue behind it in this order.
        await gate.EnterAsync(Ao3RequestPriority.Scheduled, default);

        var order = new List<string>();
        var background = Enter(gate, Ao3RequestPriority.Background, "detail page", order);
        var scheduled = Enter(gate, Ao3RequestPriority.Scheduled, "listing page", order);
        var interactive = Enter(gate, Ao3RequestPriority.Interactive, "download", order);
        var laterScheduled = Enter(gate, Ao3RequestPriority.Scheduled, "another listing page", order);

        // Each Exit hands the channel to exactly one waiter.
        gate.Exit();
        await interactive;
        gate.Exit();
        await scheduled;
        gate.Exit();
        await laterScheduled;
        gate.Exit();
        await background;
        gate.Exit();

        Assert.Equal(["download", "listing page", "another listing page", "detail page"], order);
    }

    [Fact]
    public async Task A_waiter_that_gives_up_is_skipped_rather_than_handed_the_channel()
    {
        var gate = NewGate();
        await gate.EnterAsync(Ao3RequestPriority.Scheduled, default);

        using var cancelled = new CancellationTokenSource();
        var gaveUp = gate.EnterAsync(Ao3RequestPriority.Interactive, cancelled.Token);
        var patient = gate.EnterAsync(Ao3RequestPriority.Background, default);

        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gaveUp);

        gate.Exit();
        await patient.WaitAsync(TimeSpan.FromSeconds(2));
        gate.Exit();
    }

    [Fact]
    public async Task Every_redirect_hop_is_spaced_like_any_other_request()
    {
        // A redirect is a second request AO3 has to field. Before this the hop went out the
        // instant the 302 arrived — a synonym tag, or a download, was two requests within a
        // second, which is exactly the burst a per-window limiter counts.
        var spacing = TimeSpan.FromMilliseconds(400);
        var sentAt = new List<DateTimeOffset>();

        _archive.Answers = request =>
        {
            sentAt.Add(DateTimeOffset.UtcNow);
            if (request.RequestUri!.ToString() != Url) return Ok();

            var redirect = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
            redirect.Headers.Location = new Uri(OtherUrl);
            return redirect;
        };

        var (client, _) = Client(spacing: spacing);

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, sentAt.Count);
        Assert.True(sentAt[1] - sentAt[0] >= spacing, $"The hop went out {sentAt[1] - sentAt[0]} after the first request.");
    }

    private static async Task Enter(Ao3RateGate gate, Ao3RequestPriority priority, string who, List<string> order)
    {
        await gate.EnterAsync(priority, default);
        lock (order) order.Add(who);
    }

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent("<html></html>") };

    private static HttpResponseMessage Retryable(HttpStatusCode status, TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("") };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);

        return response;
    }

    private static Ao3RateGate NewGate() => new(
        Options.Create(new Ao3HttpClientOptions()), TimeProvider.System, NullLogger<Ao3RateGate>.Instance);

    private (RateLimitedAo3HttpClient Client, Ao3RateGate Gate) Client(
        int maxRetries = 3,
        TimeSpan? maxRetryAfter = null,
        TimeSpan? maxThrottleHold = null,
        TimeSpan? spacing = null)
    {
        var options = Options.Create(new Ao3HttpClientOptions
        {
            BaseUrl = "https://ao3.test",
            MinDelayBetweenRequests = spacing ?? TimeSpan.Zero,
            MaxDelayBetweenRequests = spacing ?? TimeSpan.Zero,
            MaxRetries = maxRetries,
            MaxRetryAfter = maxRetryAfter ?? TimeSpan.FromMinutes(15),
            MaxThrottleHold = maxThrottleHold ?? TimeSpan.FromHours(1),
        });

        var storagePaths = new StoragePaths(
            _dataDirectory,
            Path.Combine(_dataDirectory, "test.db"),
            Path.Combine(_dataDirectory, "settings.json"),
            Path.Combine(_dataDirectory, "keys"));

        var userAgents = new Ao3UserAgentProvider(
            options,
            InstanceIdentity.LoadOrCreate(storagePaths),
            new StubContacts(() => "emma@example.com"));

        var gate = new Ao3RateGate(options, TimeProvider.System, NullLogger<Ao3RateGate>.Instance);

        var client = new RateLimitedAo3HttpClient(
            gate,
            new HttpClient(_archive),
            new Ao3LoginHttpClient(new HttpClient(new StubArchive())),
            new MemoryCache(new MemoryCacheOptions()),
            options,
            userAgents,
            _sessions,
            TimeProvider.System,
            NullLogger<RateLimitedAo3HttpClient>.Instance);

        return (client, gate);
    }
}
