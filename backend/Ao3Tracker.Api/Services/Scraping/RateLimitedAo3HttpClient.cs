using System.Net;
using Ao3Tracker.Api.Services.Credentials;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// The redirect-following half of the transport is wrong for exactly one request — the login POST,
/// which answers success with a 302 whose <c>Set-Cookie</c> is the session itself. Follow that
/// redirect and the cookie is spent on a page nobody asked for. So the login gets a second
/// <see cref="HttpClient"/> configured not to follow redirects, rather than the whole scraper
/// losing the automatic redirect that is how a synonym tag is recognised.
///
/// Same gate, same User-Agent, same options: this is a differently-configured transport, never a
/// second way out of the rate limit.
/// </summary>
public sealed class Ao3LoginHttpClient
{
    public Ao3LoginHttpClient(HttpClient client) => Client = client;

    public HttpClient Client { get; }
}

/// <summary>
/// Single choke point for all outbound scraping traffic. Requests are serialized through
/// one semaphore so that no matter how many scrapers/users run concurrently, requests to
/// AO3 never go out faster than <see cref="Ao3HttpClientOptions.MinDelayBetweenRequests"/>
/// apart. This is a hard constraint, not a tunable-away nicety.
///
/// It is also where the instance's AO3 session is attached, and the only place that decides a
/// session has stopped working. Both belong here for the same reason the rate gate does: a scraper
/// that had to remember to attach a cookie would eventually forget.
/// </summary>
public class RateLimitedAo3HttpClient : IRateLimitedHttpClient
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    private readonly HttpClient _httpClient;
    private readonly Ao3LoginHttpClient _loginClient;
    private readonly IMemoryCache _cache;
    private readonly Ao3HttpClientOptions _options;
    private readonly Ao3UserAgentProvider _userAgents;
    private readonly IAo3SessionCache _sessions;
    private readonly ILogger<RateLimitedAo3HttpClient> _logger;

    public RateLimitedAo3HttpClient(
        HttpClient httpClient,
        Ao3LoginHttpClient loginClient,
        IMemoryCache cache,
        IOptions<Ao3HttpClientOptions> options,
        Ao3UserAgentProvider userAgents,
        IAo3SessionCache sessions,
        ILogger<RateLimitedAo3HttpClient> logger)
    {
        _httpClient = httpClient;
        _loginClient = loginClient;
        _cache = cache;
        _options = options.Value;
        _userAgents = userAgents;
        _sessions = sessions;
        _logger = logger;
    }

    public async Task<ScrapeHttpResponse> GetAsync(string url, CancellationToken ct = default)
    {
        var session = await _sessions.GetUsableAsync(ct);
        var cookie = session?.SessionCookie;

        // Anonymous and logged-in views of one URL are different pages — AO3 hides adult and
        // restricted works from nobody-in-particular — so they cannot share a cache entry. Keyed by
        // whether a session was sent rather than by which one: a re-login does not change what the
        // archive is willing to show this account.
        var cacheKey = CacheKey(url, authenticated: cookie is not null);

        if (_cache.TryGetValue<ScrapeHttpResponse>(cacheKey, out var cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for {Url}", url);
            return cached with { FromCache = true };
        }

        var response = await SendAsync(
            _httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cookie,
            ct);

        // Dropped rather than carried: no scraper reads them, and a shared process-wide cache is no
        // place to keep cookie material for fifteen minutes for no purpose. It also makes the
        // interface's "empty for ordinary scraping requests" true rather than nearly true.
        response = response with { SetCookieHeaders = null };

        var reading = cookie is null ? null : await ReadSessionStateAsync(response, ct);
        if (reading is not null)
        {
            response = response with { Authenticated = reading.State == Ao3SessionState.LoggedIn };
        }

        // A page AO3 served logged out while we held a session is the *dead* session's copy of it,
        // and it must not be cached under the session key. The session is discarded, the next poll
        // logs in again, and the very next request for this URL would then find that anonymous copy
        // still sitting under the key the fresh session computes — reading the logged-out half of
        // the archive for the rest of the cache window, having just re-authenticated to avoid
        // exactly that.
        if (response.StatusCode == HttpStatusCode.OK && reading?.State != Ao3SessionState.LoggedOut)
        {
            _cache.Set(cacheKey, response, _options.CacheDuration);
        }

        return response;
    }

    public Task<ScrapeHttpResponse> GetLoggedOutAsync(string url, CancellationToken ct = default) =>
        SendAsync(_httpClient, () => new HttpRequestMessage(HttpMethod.Get, url), cookieHeader: null, ct);

    public Task<ScrapeHttpResponse> PostFormAsync(
        string url,
        IReadOnlyDictionary<string, string> fields,
        string? cookieHeader,
        CancellationToken ct = default) =>
        SendAsync(
            _loginClient.Client,
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(fields),
            },
            cookieHeader,
            ct);

    /// <summary>
    /// Reads back off the page whether AO3 actually served it to our session, and drops the cached
    /// cookie when it plainly did not.
    ///
    /// This is the whole expiry mechanism. AO3 does not answer a dead session with a 401 — it
    /// answers with a 200 and the anonymous view — so nothing at the transport level notices, and a
    /// scraper would go on quietly reading the logged-out half of the archive for as long as the
    /// row survived. Discarding costs one login; not discarding costs every restricted work.
    ///
    /// <see cref="Ao3SessionState.Unknown"/> changes nothing on purpose. A 404 or a file body is not
    /// evidence, and throwing away a working session over one would re-login on every miss.
    /// </summary>
    private async Task<Ao3SessionReading> ReadSessionStateAsync(
        ScrapeHttpResponse response, CancellationToken ct)
    {
        var reading = Ao3LoginPage.ReadSessionState(response.Content);

        if (reading.State == Ao3SessionState.LoggedOut)
        {
            _logger.LogWarning(
                "AO3 served {Url} logged out despite a stored session; discarding it so the next run logs in again.",
                response.FinalUrl);

            await _sessions.DiscardAsync(ct);
        }

        return reading;
    }

    private async Task<ScrapeHttpResponse> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> newRequest,
        string? cookieHeader,
        CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await WaitForRateLimitSlotAsync(ct);

            try
            {
                return await SendWithRetryAsync(client, newRequest, cookieHeader, ct);
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

    private async Task<ScrapeHttpResponse> SendWithRetryAsync(
        HttpClient client,
        Func<HttpRequestMessage> newRequest,
        string? cookieHeader,
        CancellationToken ct)
    {
        var backoff = _options.InitialBackoff;

        // Resolved once per logical fetch, not once per client: the settings UI can change the
        // operator contact at any time, and a pooled HttpClient's default headers would keep
        // sending the old one until its handler was recycled. Throws if no contact is usable,
        // which is the intended fail-closed behaviour — no contact, no request.
        var userAgent = await _userAgents.GetUserAgentAsync(ct);

        for (var attempt = 0; ; attempt++)
        {
            using var request = newRequest();
            request.Headers.UserAgent.ParseAdd(userAgent);

            // Set by hand rather than through a CookieContainer: the session lives in the database
            // and is shared by every process reading this deployment's data, so a per-handler
            // cookie jar would be a second, divergent copy of it.
            if (cookieHeader is not null) request.Headers.Add("Cookie", cookieHeader);

            using var response = await client.SendAsync(request, ct);
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

                    // Set by the caller that knows whether a session was attached, and only from
                    // the page's own evidence. Nothing here can tell.
                    Authenticated: false,
                    SetCookieHeaders: SetCookies(response),
                    Location: response.Headers.Location?.ToString());
            }

            // Retry-After is AO3 telling us exactly what it wants; honor it verbatim and do not
            // jitter it — the whole value of an explicit instruction is that it isn't guesswork.
            // Our own backoff is a guess, so that one gets jittered.
            var delay = response.Headers.RetryAfter?.Delta ?? Jitter(backoff);

            _logger.LogWarning(
                "Scrape request to {Url} got {StatusCode}, retrying in {Delay} (attempt {Attempt}/{MaxRetries})",
                request.RequestUri, response.StatusCode, delay, attempt + 1, _options.MaxRetries);

            await Task.Delay(delay, ct);
            backoff *= 2;
        }
    }

    private static IReadOnlyList<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values] : [];

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

    private static string CacheKey(string url, bool authenticated) =>
        $"ao3http:{(authenticated ? "session" : "anon")}:{url}";
}
