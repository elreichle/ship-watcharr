using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Single choke point for all outbound scraping traffic. Requests are serialized through
/// one semaphore so that no matter how many scrapers/users run concurrently, requests to
/// AO3 never go out faster than <see cref="Ao3HttpClientOptions.MinDelayBetweenRequests"/>
/// apart. This is a hard constraint, not a tunable-away nicety.
/// </summary>
public class RateLimitedAo3HttpClient : IRateLimitedHttpClient
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly Ao3HttpClientOptions _options;
    private readonly Ao3UserAgentProvider _userAgents;
    private readonly ILogger<RateLimitedAo3HttpClient> _logger;

    public RateLimitedAo3HttpClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<Ao3HttpClientOptions> options,
        Ao3UserAgentProvider userAgents,
        ILogger<RateLimitedAo3HttpClient> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
        _userAgents = userAgents;
        _logger = logger;
    }

    public async Task<ScrapeHttpResponse> GetAsync(string url, CancellationToken ct = default)
    {
        if (_cache.TryGetValue<ScrapeHttpResponse>(CacheKey(url), out var cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for {Url}", url);
            return cached with { FromCache = true };
        }

        await Gate.WaitAsync(ct);
        try
        {
            await WaitForRateLimitSlotAsync(ct);

            try
            {
                var response = await SendWithRetryAsync(url, ct);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    _cache.Set(CacheKey(url), response, _options.CacheDuration);
                }

                return response;
            }
            finally
            {
                // Must be in a finally, not on the success path. A network failure or the 30s
                // HttpClient timeout throws straight out of SendWithRetryAsync, and if the
                // timestamp were only advanced on success the next caller would compute a
                // negative "time since last request" and fire immediately. That would remove
                // the rate limit precisely when AO3 is failing and least able to absorb load.
                _lastRequestAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Waits out the spacing owed since the previous request, using a fresh random target drawn
    /// per request from [Min, Max].
    ///
    /// Randomizing raises the mean delay above the floor (5s fixed becomes ~6.5s across 5–8s), so
    /// this strictly reduces request rate. It also breaks up the lockstep that fixed intervals
    /// produce, which is what turns several independent clients into a synchronized load spike.
    /// </summary>
    private async Task WaitForRateLimitSlotAsync(CancellationToken ct)
    {
        var target = NextDelayTarget();
        var elapsedSinceLast = DateTimeOffset.UtcNow - _lastRequestAt;
        var remaining = target - elapsedSinceLast;
        if (remaining > TimeSpan.Zero)
        {
            _logger.LogDebug("Rate limiting: waiting {Delay} (target spacing {Target})", remaining, target);
            await Task.Delay(remaining, ct);
        }
    }

    internal TimeSpan NextDelayTarget()
    {
        var min = _options.MinDelayBetweenRequests;

        // Clamp rather than throw: a Max below Min is a misconfiguration that must degrade to
        // "slower", never to "no spacing at all".
        var max = _options.MaxDelayBetweenRequests < min ? min : _options.MaxDelayBetweenRequests;
        if (max == min) return min;

        return min + (max - min) * Random.Shared.NextDouble();
    }

    private async Task<ScrapeHttpResponse> SendWithRetryAsync(string url, CancellationToken ct)
    {
        var backoff = _options.InitialBackoff;

        // Resolved once per logical fetch, not once per client: the settings UI can change the
        // operator contact at any time, and a pooled HttpClient's default headers would keep
        // sending the old one until its handler was recycled. Throws if no contact is usable,
        // which is the intended fail-closed behaviour — no contact, no request.
        var userAgent = await _userAgents.GetUserAgentAsync(ct);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(userAgent);

            using var response = await _httpClient.SendAsync(request, ct);
            var isRetryable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                               (int)response.StatusCode >= 500;

            if (!isRetryable || attempt >= _options.MaxRetries)
            {
                var content = await response.Content.ReadAsStringAsync(ct);

                // RequestMessage is the *last* request the handler made, so after an automatic
                // redirect its Uri is the destination rather than what we asked for. That is
                // exactly the difference a synonym check needs.
                return new ScrapeHttpResponse(
                    content,
                    response.StatusCode,
                    FromCache: false,
                    FinalUrl: response.RequestMessage?.RequestUri?.ToString(),

                    // Nothing attaches a session cookie yet, so every page this client fetches is
                    // the logged-out view of it and saying otherwise would be a lie a full sweep
                    // acts on. T5 is where the request gains a session and this gains a source.
                    Authenticated: false);
            }

            // Retry-After is AO3 telling us exactly what it wants; honor it verbatim and do not
            // jitter it — the whole value of an explicit instruction is that it isn't guesswork.
            // Our own backoff is a guess, so that one gets jittered.
            var delay = response.Headers.RetryAfter?.Delta ?? Jitter(backoff);

            _logger.LogWarning(
                "Scrape request to {Url} got {StatusCode}, retrying in {Delay} (attempt {Attempt}/{MaxRetries})",
                url, response.StatusCode, delay, attempt + 1, _options.MaxRetries);

            await Task.Delay(delay, ct);
            backoff *= 2;
        }
    }

    /// <summary>
    /// Spreads a computed backoff by ±<see cref="Ao3HttpClientOptions.BackoffJitterFactor"/>, so
    /// that every client which failed at the same instant does not retry at the same instant too.
    /// </summary>
    internal TimeSpan Jitter(TimeSpan value)
    {
        var factor = Math.Clamp(_options.BackoffJitterFactor, 0, 1);
        if (factor == 0) return value;

        var multiplier = 1 + ((Random.Shared.NextDouble() * 2 - 1) * factor);
        return value * multiplier;
    }

    private static string CacheKey(string url) => $"ao3http:{url}";
}
