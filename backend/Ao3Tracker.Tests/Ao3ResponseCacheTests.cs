using System.Net;
using System.Text;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Tests;

/// <summary>
/// What the response cache actually does, driven through the real transport rather than through the
/// fake every other test stands in front of.
///
/// The fake sets <c>FromCache</c> and <c>FetchedAt</c> by hand, which is right for a caller under
/// test — but it means nothing exercises the half those flags come from. A
/// <see cref="IRateLimitedHttpClient.GetFreshAsync"/> that quietly returned the cached copy, or a
/// stamp written when a response was handed out rather than when it came off the wire, would leave
/// every one of those tests passing and the reason they exist unmet.
/// </summary>
public class Ao3ResponseCacheTests : IDisposable
{
    private const string Url = "https://ao3.test/works/1";

    private readonly string _dataDirectory =
        Directory.CreateTempSubdirectory("ship-watcharr-cache-").FullName;

    private readonly StubArchive _archive = new();
    private readonly StubSessionCache _sessions = new();
    private readonly FixedClock _clock = new();

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
    public async Task Serves_a_second_read_from_cache_and_says_when_the_copy_was_taken()
    {
        var client = Client();

        _clock.Now = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
        Answer("first");
        var fresh = await client.GetAsync(Url);

        // An hour on the clock, and the request is answered without the archive being asked. What
        // the caller gets back is the *first* response, stamped with when that one came off the
        // wire — not with now, which is the whole of what makes the stamp readable as an age.
        _clock.Now = _clock.Now.AddHours(1);
        Answer("second");
        var cached = await client.GetAsync(Url);

        Assert.False(fresh.FromCache);
        Assert.True(cached.FromCache);
        Assert.Equal("first", cached.Content);
        Assert.Equal(new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), cached.FetchedAt);
        Assert.Single(_archive.Received);
    }

    [Fact]
    public async Task A_fresh_read_goes_past_the_cached_copy_and_replaces_it()
    {
        // Both halves are load-bearing. Reading past the copy is what a download with evidence its
        // page is stale is asking for; replacing it is what stops the next caller finding the same
        // stale page and spending another rate-gated request on it.
        var client = Client();

        _clock.Now = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
        Answer("first");
        await client.GetAsync(Url);

        _clock.Now = _clock.Now.AddHours(1);
        Answer("second");
        var reread = await client.GetFreshAsync(Url);

        Assert.False(reread.FromCache);
        Assert.Equal("second", reread.Content);
        Assert.Equal(_clock.Now.UtcDateTime, reread.FetchedAt);
        Assert.Equal(2, _archive.Received.Count);

        // And the copy it left behind is the new one, at the new time.
        Answer("third");
        var after = await client.GetAsync(Url);

        Assert.True(after.FromCache);
        Assert.Equal("second", after.Content);
        Assert.Equal(_clock.Now.UtcDateTime, after.FetchedAt);
        Assert.Equal(2, _archive.Received.Count);
    }

    private void Answer(string body) => _archive.Answers = _ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html"),
    };

    private RateLimitedAo3HttpClient Client()
    {
        var options = Options.Create(new Ao3HttpClientOptions
        {
            BaseUrl = "https://ao3.test",
            MinDelayBetweenRequests = TimeSpan.Zero,
            MaxDelayBetweenRequests = TimeSpan.Zero,
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
            _clock,
            NullLogger<RateLimitedAo3HttpClient>.Instance);
    }
}
