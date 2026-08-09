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
    private readonly ILogger<RateLimitedAo3HttpClient> _logger;

    public RateLimitedAo3HttpClient(
        HttpClient httpClient,
        IMemoryCache cache,
        IOptions<Ao3HttpClientOptions> options,
        ILogger<RateLimitedAo3HttpClient> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
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
            var response = await SendWithRetryAsync(url, ct);

            _lastRequestAt = DateTimeOffset.UtcNow;

            if (response.StatusCode == HttpStatusCode.OK)
            {
                _cache.Set(CacheKey(url), response, _options.CacheDuration);
            }

            return response;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task WaitForRateLimitSlotAsync(CancellationToken ct)
    {
        var elapsedSinceLast = DateTimeOffset.UtcNow - _lastRequestAt;
        var remaining = _options.MinDelayBetweenRequests - elapsedSinceLast;
        if (remaining > TimeSpan.Zero)
        {
            _logger.LogDebug("Rate limiting: waiting {Delay}", remaining);
            await Task.Delay(remaining, ct);
        }
    }

    private async Task<ScrapeHttpResponse> SendWithRetryAsync(string url, CancellationToken ct)
    {
        var backoff = _options.InitialBackoff;

        for (var attempt = 0; ; attempt++)
        {
            using var response = await _httpClient.GetAsync(url, ct);
            var isRetryable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                               (int)response.StatusCode >= 500;

            if (!isRetryable || attempt >= _options.MaxRetries)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                return new ScrapeHttpResponse(content, response.StatusCode, FromCache: false);
            }

            var delay = response.Headers.RetryAfter?.Delta ?? backoff;
            _logger.LogWarning(
                "Scrape request to {Url} got {StatusCode}, retrying in {Delay} (attempt {Attempt}/{MaxRetries})",
                url, response.StatusCode, delay, attempt + 1, _options.MaxRetries);

            await Task.Delay(delay, ct);
            backoff *= 2;
        }
    }

    private static string CacheKey(string url) => $"ao3http:{url}";
}
