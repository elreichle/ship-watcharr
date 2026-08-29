using Ao3Tracker.Api.Services.Scraping;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Tests;

/// <summary>
/// Randomized delays. The property that actually matters is not "the numbers vary" but "the
/// numbers never drop below the floor" — a jitter bug that produced a delay of zero would remove
/// the rate limit entirely while still looking like it worked.
/// </summary>
public class JitterTests
{
    private const int Samples = 2_000;

    private static RateLimitedAo3HttpClient Client(Ao3HttpClientOptions options) =>
        new(new HttpClient(),
            new Ao3LoginHttpClient(new HttpClient()),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(options),
            userAgents: null!,   // not reached: these tests exercise delay maths only
            sessions: null!,     // likewise
            TimeProvider.System,
            NullLogger<RateLimitedAo3HttpClient>.Instance);

    [Fact]
    public void Request_spacing_stays_within_the_configured_range()
    {
        var options = new Ao3HttpClientOptions
        {
            MinDelayBetweenRequests = TimeSpan.FromSeconds(5),
            MaxDelayBetweenRequests = TimeSpan.FromSeconds(8),
        };
        var client = Client(options);

        for (var i = 0; i < Samples; i++)
        {
            var delay = client.NextDelayTarget();
            Assert.InRange(delay, options.MinDelayBetweenRequests, options.MaxDelayBetweenRequests);
        }
    }

    [Fact]
    public void Request_spacing_averages_above_the_floor()
    {
        // The politeness claim being defended: randomizing 5-8s makes the scraper SLOWER on
        // average than a fixed 5s, so jitter is not a way of squeezing out extra requests.
        var options = new Ao3HttpClientOptions
        {
            MinDelayBetweenRequests = TimeSpan.FromSeconds(5),
            MaxDelayBetweenRequests = TimeSpan.FromSeconds(8),
        };
        var client = Client(options);

        var mean = Enumerable.Range(0, Samples)
            .Average(_ => client.NextDelayTarget().TotalSeconds);

        Assert.True(mean > 6.0, $"mean spacing {mean:F2}s should exceed the 5s floor");
        Assert.True(mean < 7.0, $"mean spacing {mean:F2}s should sit inside the 5-8s range");
    }

    [Fact]
    public void Request_spacing_actually_varies()
    {
        var client = Client(new Ao3HttpClientOptions
        {
            MinDelayBetweenRequests = TimeSpan.FromSeconds(5),
            MaxDelayBetweenRequests = TimeSpan.FromSeconds(8),
        });

        var distinct = Enumerable.Range(0, 100).Select(_ => client.NextDelayTarget()).Distinct().Count();

        Assert.True(distinct > 50, $"expected varied delays, got {distinct} distinct values in 100");
    }

    [Fact]
    public void Max_below_min_clamps_to_min_rather_than_to_zero()
    {
        // A misconfiguration must only ever be able to make the scraper slower. If this degraded
        // to "no spacing", a typo in appsettings.json would silently unleash it on AO3.
        var client = Client(new Ao3HttpClientOptions
        {
            MinDelayBetweenRequests = TimeSpan.FromSeconds(5),
            MaxDelayBetweenRequests = TimeSpan.FromSeconds(1),
        });

        for (var i = 0; i < 100; i++)
            Assert.Equal(TimeSpan.FromSeconds(5), client.NextDelayTarget());
    }

    [Fact]
    public void Equal_min_and_max_gives_a_fixed_delay()
    {
        var client = Client(new Ao3HttpClientOptions
        {
            MinDelayBetweenRequests = TimeSpan.FromSeconds(6),
            MaxDelayBetweenRequests = TimeSpan.FromSeconds(6),
        });

        Assert.Equal(TimeSpan.FromSeconds(6), client.NextDelayTarget());
    }

    [Fact]
    public void Backoff_jitter_stays_within_the_configured_factor()
    {
        var client = Client(new Ao3HttpClientOptions { BackoffJitterFactor = 0.2 });
        var baseDelay = TimeSpan.FromSeconds(10);

        for (var i = 0; i < Samples; i++)
        {
            var jittered = client.Jitter(baseDelay);
            Assert.InRange(jittered, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(12));
        }
    }

    [Fact]
    public void Backoff_jitter_of_zero_is_a_no_op()
    {
        var client = Client(new Ao3HttpClientOptions { BackoffJitterFactor = 0 });
        Assert.Equal(TimeSpan.FromSeconds(10), client.Jitter(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Backoff_jitter_factor_is_clamped_to_one()
    {
        // Guards against a configured factor > 1 producing a negative delay, which Task.Delay
        // would reject at exactly the moment AO3 is already failing.
        var client = Client(new Ao3HttpClientOptions { BackoffJitterFactor = 5 });

        for (var i = 0; i < Samples; i++)
            Assert.True(client.Jitter(TimeSpan.FromSeconds(10)) >= TimeSpan.Zero);
    }

    [Fact]
    public void Schedule_jitter_spreads_next_run_around_the_interval()
    {
        // Without this, jobs sharing an interval converge onto the same tick and queue every
        // scrape behind the shared gate simultaneously.
        var interval = TimeSpan.FromHours(6);
        var before = DateTime.UtcNow;

        var runs = Enumerable.Range(0, 500).Select(_ => ScrapeWorker.NextRunAfter(interval)).ToList();

        var offsets = runs.Select(r => (r - before).TotalHours).ToList();

        Assert.All(offsets, o => Assert.InRange(o, 6 * 0.9, 6 * 1.1 + 0.01));
        Assert.True(offsets.Distinct().Count() > 400, "next-run times should not cluster");
        Assert.True(offsets.Max() - offsets.Min() > 0.5, "expected a meaningful spread across the window");
    }
}
