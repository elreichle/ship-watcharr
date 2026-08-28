using System.Net;
using System.Net.Http.Headers;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Tests;

/// <summary>
/// What one request's retry costs every other request on the instance.
///
/// The rate gate is a process-wide semaphore, and everything outbound queues behind it — the ship
/// walk and the download drain alike. So where the wait between attempts happens is not a detail:
/// inside the gate, one <c>Retry-After</c> is every caller's wait, and the circuit breaker cannot
/// intervene because nothing is making requests for it to count.
///
/// Honouring <c>Retry-After</c> is not in question. What these pin is that one request's wait is
/// not allowed to become the instance's.
/// </summary>
public class RateLimitedRetryTests : IDisposable
{
    // Nothing escaped in either: Uri.ToString() unescapes, so a URL with a %20 in it would not
    // compare equal to what the stub recorded.
    private const string Url = "https://ao3.test/tags/lexa/works";
    private const string OtherUrl = "https://ao3.test/works/1";

    private readonly string _dataDirectory =
        Directory.CreateTempSubdirectory("ship-watcharr-retry-").FullName;

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
    public async Task Waits_out_a_retry_without_holding_the_gate_shut()
    {
        // A 503 with four seconds asked for, and a second request for something else while that
        // wait runs. Waited out inside the gate, the second request cannot start until the first
        // has finished retrying — which is how one struggling page stops an instance scraping.
        _archive.Answers = request => request.RequestUri!.ToString() == Url
            ? Retryable(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(4))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") };

        var client = Client(maxRetries: 1);

        var held = Task.Run(() => client.GetAsync(Url));

        // The retry wait has begun once the first attempt has been answered.
        await WaitUntilAsync(() => _archive.Received.Count >= 1);

        var other = await client.GetAsync(OtherUrl).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        // And the retry still happened: the point is where it waited, not whether.
        await held;
        Assert.Equal(2, _archive.Received.Count(r => r.Url == Url));
    }

    [Fact]
    public async Task Stops_asking_rather_than_coming_back_before_AO3_asked()
    {
        // An hour is longer than this instance will hold a request open for. The answer is not to
        // come back sooner — that would be asking again before AO3 said to — but to stop retrying
        // and let the response be the answer, which its caller records and the breaker acts on.
        _archive.Answers = _ => Retryable(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(1));

        var response = await Client().GetAsync(Url).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(_archive.Received);
    }

    [Fact]
    public async Task Reads_the_ceiling_against_a_wait_asked_for_as_a_date()
    {
        // Retry-After has two forms and AO3 may use either. Reading only the delta leaves the date
        // form outside the ceiling *and* unhonoured: an hour asked for as a date would fall through
        // to this instance's own ten-second backoff, which is the opposite of honouring it.
        _archive.Answers = _ => RetryableAt(HttpStatusCode.TooManyRequests, DateTimeOffset.UtcNow.AddHours(1));

        var response = await Client().GetAsync(Url).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(_archive.Received);
    }

    [Fact]
    public async Task Honours_a_wait_it_can_make()
    {
        // The other side of the ceiling: an ask this instance can meet is met, verbatim.
        // Received is appended to before the answer is chosen, so this is the first request.
        _archive.Answers = _ => _archive.Received.Count == 1
            ? Retryable(HttpStatusCode.TooManyRequests, TimeSpan.FromMilliseconds(200))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") };

        var response = await Client(maxRetries: 1).GetAsync(Url).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _archive.Received.Count);
    }

    private static HttpResponseMessage Retryable(HttpStatusCode status, TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("") };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);

        return response;
    }

    private static HttpResponseMessage RetryableAt(HttpStatusCode status, DateTimeOffset when)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("") };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(when);

        return response;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("The first request never reached the archive.");
    }

    private RateLimitedAo3HttpClient Client(int maxRetries = 3)
    {
        var options = Options.Create(new Ao3HttpClientOptions
        {
            BaseUrl = "https://ao3.test",
            MinDelayBetweenRequests = TimeSpan.Zero,
            MaxDelayBetweenRequests = TimeSpan.Zero,
            MaxRetries = maxRetries,
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

        return new RateLimitedAo3HttpClient(
            new HttpClient(_archive),
            new Ao3LoginHttpClient(new HttpClient(new StubArchive())),
            new MemoryCache(new MemoryCacheOptions()),
            options,
            userAgents,
            _sessions,
            NullLogger<RateLimitedAo3HttpClient>.Instance);
    }
}
