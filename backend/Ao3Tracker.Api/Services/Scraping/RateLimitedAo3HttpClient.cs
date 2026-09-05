using System.Net;
using System.Net.Http.Headers;
using Ao3Tracker.Api.Services.Credentials;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// The transport for exactly one request — the login POST, which answers success with a 302 whose
/// <c>Set-Cookie</c> is the session itself. Follow that redirect and the cookie is spent on a page
/// nobody asked for, so the login's send never follows one, not even within the archive.
///
/// Same gate, same User-Agent, same options: this is a separate <see cref="HttpClient"/>, never a
/// second way out of the rate limit. No handler here follows redirects any more — the scraper
/// walks its own by hand, per hop, so a hand-set <c>Cookie</c> header can never be copied onto a
/// request that leaves the archive — so what keeps the login POST where it was sent is that its
/// send asks for no redirect to be followed at all.
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
    private readonly TimeProvider _time;
    private readonly ILogger<RateLimitedAo3HttpClient> _logger;

    public RateLimitedAo3HttpClient(
        HttpClient httpClient,
        Ao3LoginHttpClient loginClient,
        IMemoryCache cache,
        IOptions<Ao3HttpClientOptions> options,
        Ao3UserAgentProvider userAgents,
        IAo3SessionCache sessions,
        TimeProvider time,
        ILogger<RateLimitedAo3HttpClient> logger)
    {
        _httpClient = httpClient;
        _loginClient = loginClient;
        _cache = cache;
        _options = options.Value;
        _userAgents = userAgents;
        _sessions = sessions;
        _time = time;
        _logger = logger;
    }

    public Task<ScrapeHttpResponse> GetAsync(string url, CancellationToken ct = default) =>
        GetAsync(url, readFromCache: true, ct);

    public Task<ScrapeHttpResponse> GetFreshAsync(string url, CancellationToken ct = default) =>
        GetAsync(url, readFromCache: false, ct);

    /// <param name="readFromCache">
    /// Whether a cached copy may answer this. False reads past it and still writes what comes back,
    /// so one caller with evidence that the copy is stale replaces it for everyone rather than
    /// leaving the next caller to find the same stale page and fetch again.
    /// </param>
    private async Task<ScrapeHttpResponse> GetAsync(string url, bool readFromCache, CancellationToken ct)
    {
        var session = await _sessions.GetUsableAsync(ct);
        var cookie = session?.SessionCookie;

        // Anonymous and logged-in views of one URL are different pages — AO3 hides adult and
        // restricted works from nobody-in-particular — so they cannot share a cache entry. Keyed by
        // whether a session was sent rather than by which one: a re-login does not change what the
        // archive is willing to show this account.
        var cacheKey = CacheKey(url, authenticated: cookie is not null);

        if (readFromCache && _cache.TryGetValue<ScrapeHttpResponse>(cacheKey, out var cached) && cached is not null)
        {
            _logger.LogDebug("Cache hit for {Url}", url);
            return cached with { FromCache = true };
        }

        var response = await SendAsync(
            _httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cookie,
            ReadPageAsync,
            followRedirects: true,
            ct);

        // Dropped rather than carried: no scraper reads them, and a shared process-wide cache is no
        // place to keep cookie material for fifteen minutes for no purpose. It also makes the
        // interface's "empty for ordinary scraping requests" true rather than nearly true.
        //
        // Stamped in the same breath, and before anything is cached: the copy that goes into the
        // cache must carry when it came off the wire, not when it was later handed out.
        response = response with
        {
            SetCookieHeaders = null,
            FetchedAt = _time.GetUtcNow().UtcDateTime,
        };

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
        SendAsync(
            _httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, url),
            cookieHeader: null,
            ReadPageAsync,
            followRedirects: true,
            ct);

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
            ReadPageAsync,
            followRedirects: false,
            ct);

    public async Task<ScrapeDownloadResponse> DownloadAsync(
        string url, Stream destination, CancellationToken ct = default)
    {
        var session = await _sessions.GetUsableAsync(ct);

        // The transfer happens inside the global gate, so a socket that goes quiet mid-file would
        // hold every other outbound request behind it for as long as it stayed open. HttpClient's
        // own timeout does not cover this: with ResponseHeadersRead it stops applying once the
        // headers are in. So the whole download gets a deadline of its own.
        //
        // Started where the transfer starts, and deliberately not before it. Armed at this line it
        // would also be running through the gate wait, the 5-8s spacing and every retry backoff —
        // so a request that never received a byte would fail as a transfer that stopped part-way,
        // and the caller would tell a reader AO3 had gone quiet when AO3 had never been asked.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // ResponseHeadersRead, so the body is still on the socket when the reader gets it: the
        // whole point of streaming a download is that a large one is never held in memory.
        //
        // Nothing here reads the session back off the response, unlike GetAsync. A file body
        // carries no greeting and no login form, so Ao3LoginPage would read Unknown on every
        // download and change nothing — asking would only mean parsing an EPUB as HTML.
        return await SendAsync(
            _httpClient,
            () => new HttpRequestMessage(HttpMethod.Get, url),
            session?.SessionCookie,
            (response, token) =>
            {
                deadline.CancelAfter(_options.DownloadTimeout);
                return ReadFileAsync(response, destination, token);
            },
            followRedirects: true,
            deadline.Token,
            HttpCompletionOption.ResponseHeadersRead);
    }

    /// <summary>
    /// Copies a response body to the caller's stream, refusing anything that is not a 200 and
    /// stopping at <see cref="Ao3HttpClientOptions.MaxDownloadBytes"/>.
    /// </summary>
    /// <remarks>
    /// The cap is not paranoia about AO3. It is that this writes to the instance's disk on behalf of
    /// whoever clicked a button, and a response with no Content-Length — which is what a chunked
    /// error page is — has no size until it has finished arriving. Copying without a ceiling makes
    /// "how much disk does one request cost" a question only the remote end answers.
    /// </remarks>
    private async Task<ScrapeDownloadResponse> ReadFileAsync(
        HttpResponseMessage response, Stream destination, CancellationToken ct)
    {
        var finalUrl = response.RequestMessage?.RequestUri?.ToString();

        // Anything but 200 has a body that explains the failure rather than being the file. Writing
        // it would leave an AO3 error page on disk under a name claiming to be an EPUB.
        if (response.StatusCode != HttpStatusCode.OK)
            return new ScrapeDownloadResponse(response.StatusCode, 0, finalUrl);

        await using var body = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[81920];
        long written = 0;

        while (true)
        {
            var read = await body.ReadAsync(buffer, ct);
            if (read == 0) break;

            written += read;
            if (written > _options.MaxDownloadBytes)
            {
                _logger.LogWarning(
                    "Download from {Url} passed {Limit} bytes and was abandoned", finalUrl, _options.MaxDownloadBytes);

                return new ScrapeDownloadResponse(
                    response.StatusCode, written, finalUrl, ExceededSizeLimit: true);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        return new ScrapeDownloadResponse(response.StatusCode, written, finalUrl);
    }

    /// <summary>The whole body as text, which is what every caller but a download wants.</summary>
    private static async Task<ScrapeHttpResponse> ReadPageAsync(HttpResponseMessage response, CancellationToken ct)
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

    /// <param name="read">
    /// Turns the response into what the caller wanted — a page as text, or a file copied to a
    /// stream. Passed in rather than switched on inside, so that a download and a page fetch share
    /// one rate gate, one retry policy and one User-Agent by construction rather than by being
    /// written twice.
    /// </param>
    /// <remarks>
    /// The retry loop is here, outside <see cref="SendOnceAsync"/>, so that waiting to retry is not
    /// done holding the gate. Inside it, one <c>Retry-After: 3600</c> would park every outbound
    /// request on the instance — the ship walk and the download drain alike — for an hour, and the
    /// circuit breaker could not intervene, because nothing would be making requests for it to
    /// count. Even the ordinary path held it for roughly 70 seconds across three retries.
    /// </remarks>
    private async Task<T> SendAsync<T>(
        HttpClient client,
        Func<HttpRequestMessage> newRequest,
        string? cookieHeader,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read,
        bool followRedirects,
        CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        // Resolved once per logical fetch, not once per client: the settings UI can change the
        // operator contact at any time, and a pooled HttpClient's default headers would keep
        // sending the old one until its handler was recycled. Throws if no contact is usable,
        // which is the intended fail-closed behaviour — no contact, no request.
        var userAgent = await _userAgents.GetUserAgentAsync(ct);
        var backoff = _options.InitialBackoff;

        for (var attempt = 0; ; attempt++)
        {
            var sent = await SendOnceAsync(
                client, newRequest, cookieHeader, read, userAgent, completion, followRedirects,
                mayRetry: attempt < _options.MaxRetries, ct);

            if (sent.Completed) return sent.Value!;

            // Retry-After is AO3 telling us exactly what it wants; honor it verbatim and do not
            // jitter it — the whole value of an explicit instruction is that it isn't guesswork.
            // Our own backoff is a guess, so that one gets jittered.
            var delay = sent.RetryAfter ?? Jitter(backoff);

            _logger.LogWarning(
                "Scrape request to {Url} got {StatusCode}, retrying in {Delay} (attempt {Attempt}/{MaxRetries})",
                sent.Url, sent.StatusCode, delay, attempt + 1, _options.MaxRetries);

            await Task.Delay(delay, ct);
            backoff *= 2;
        }
    }

    /// <summary>
    /// One request: the gate, the spacing owed since the last one, the send, and either the
    /// caller's reading of the response or a report that it is worth asking again.
    /// </summary>
    /// <param name="mayRetry">
    /// Whether the caller has an attempt left. False means the response is read whatever it says,
    /// which is what turns the last retry into an answer rather than a fourth wait.
    /// </param>
    private async Task<(bool Completed, T? Value, TimeSpan? RetryAfter, HttpStatusCode StatusCode, Uri? Url)>
        SendOnceAsync<T>(
            HttpClient client,
            Func<HttpRequestMessage> newRequest,
            string? cookieHeader,
            Func<HttpResponseMessage, CancellationToken, Task<T>> read,
            string userAgent,
            HttpCompletionOption completion,
            bool followRedirects,
            bool mayRetry,
            CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await WaitForRateLimitSlotAsync(ct);

            try
            {
                using var response = await SendFollowingRedirectsAsync(
                    client, newRequest, cookieHeader, userAgent, completion, followRedirects, ct);

                var url = response.RequestMessage?.RequestUri;

                var isRetryable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                                   (int)response.StatusCode >= 500;

                if (!isRetryable || !mayRetry)
                    return (true, await read(response, ct), null, response.StatusCode, url);

                var asked = AskedToWaitFor(response.Headers.RetryAfter);

                // Honouring Retry-After is not in question — this project waits when AO3 asks it
                // to. What is bounded is how long one request may wait *inside* itself: past the
                // ceiling the answer is to stop asking rather than to come back early, so the
                // response is read as the failure it is, the caller records it, and the run's own
                // circuit breaker ends the pass. Coming back after the ceiling instead would be
                // asking again sooner than AO3 said.
                if (asked > _options.MaxRetryAfter)
                {
                    _logger.LogWarning(
                        "AO3 answered {Url} with {StatusCode} and asked for {Asked}, past the "
                        + "{Ceiling} this instance will hold a request for. Not retrying.",
                        url, response.StatusCode, asked, _options.MaxRetryAfter);

                    return (true, await read(response, ct), null, response.StatusCode, url);
                }

                return (false, default, asked, response.StatusCode, url);
            }
            finally
            {
                // Must be in a finally, not on the success path. A network failure or the 30s
                // HttpClient timeout throws straight out of the send, and if the timestamp were
                // only advanced on success the next caller would compute a negative "time since
                // last request" and fire immediately. That would remove the rate limit precisely
                // when AO3 is failing and least able to absorb load.
                _lastRequestAt = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Hard ceiling on redirects followed for one logical fetch. AO3's real chains are one hop —
    /// a synonym tag to its canonical listing — so anything approaching this is a loop, and the
    /// last 3xx is returned as the answer rather than walked further.
    /// </summary>
    private const int MaxRedirects = 10;

    /// <summary>
    /// One send, with any redirects walked by hand rather than by the handler. Automatic following
    /// is off for every transport here (see Program.cs) because <see cref="HttpClientHandler"/>
    /// copies a hand-set <c>Cookie</c> header onto each next request, wherever its Location points
    /// — so an archive page that 302s off-origin would take the instance's session with it.
    /// Walking the chain here makes that decidable per hop: the session travels only to the
    /// configured archive, and a redirect that leaves the archive is not followed at all — the 3xx
    /// itself is returned, its Location intact, for the caller to refuse with a reason.
    ///
    /// The cookie check is per request rather than once at the top, so the rule holds for the
    /// first hop too: a URL that does not address the archive gets this instance's identity,
    /// never its session.
    ///
    /// Hops run inside the gate without re-owing the 5–8s spacing, exactly as the handler's
    /// automatic following did; the chain is bounded by <see cref="MaxRedirects"/> either way.
    /// </summary>
    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(
        HttpClient client,
        Func<HttpRequestMessage> newRequest,
        string? cookieHeader,
        string userAgent,
        HttpCompletionOption completion,
        bool followRedirects,
        CancellationToken ct)
    {
        // The one address in this flow no page can influence — the measure Ao3Origin's other
        // callers use. Unparsable means nothing is checkable, so the session goes nowhere and no
        // redirect is followed: fail closed, not open.
        var archive = Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var parsed) ? parsed : null;

        Uri? redirectedTo = null;
        for (var hop = 0; ; hop++)
        {
            using var request = newRequest();
            if (redirectedTo is not null) request.RequestUri = redirectedTo;

            request.Headers.UserAgent.ParseAdd(userAgent);

            // Set by hand rather than through a CookieContainer: the session lives in a database
            // row shared by every process reading this deployment's data, so a per-handler cookie
            // jar would be a second, divergent copy of it.
            if (cookieHeader is not null
                && archive is not null
                && request.RequestUri is { } uri
                && Ao3Origin.IsTheConfiguredArchive(uri, archive))
            {
                request.Headers.Add("Cookie", cookieHeader);
            }

            var response = await client.SendAsync(request, completion, ct);

            if (!followRedirects
                || !IsRedirect(response.StatusCode)
                || response.Headers.Location is not { } location)
            {
                return response;
            }

            var target = request.RequestUri is { } from ? new Uri(from, location) : location;

            if (archive is null || !Ao3Origin.IsTheConfiguredArchive(target, archive))
            {
                _logger.LogWarning(
                    "AO3 redirected {Url} off the configured archive, to {Target}. Not following it.",
                    request.RequestUri, target);

                return response;
            }

            if (hop >= MaxRedirects)
            {
                _logger.LogWarning(
                    "{Url} was still redirecting after {MaxRedirects} hops; giving up at {Target}.",
                    request.RequestUri, MaxRedirects, target);

                return response;
            }

            response.Dispose();
            redirectedTo = target;
        }
    }

    /// <summary>The five statuses the handler's automatic follower would have honoured.</summary>
    private static bool IsRedirect(HttpStatusCode status) => status
        is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// How long AO3 asked this instance to wait, in whichever of the two forms it said it in.
    /// </summary>
    /// <remarks>
    /// <c>Retry-After</c> is either a delta or an HTTP-date, and reading only the delta would leave
    /// the date form unbounded by the ceiling and unhonoured in the wait — an hour asked for as a
    /// date would be retried in ten seconds, which is the opposite of honouring it. A date already
    /// past is no wait at all rather than a negative one.
    /// </remarks>
    private static TimeSpan? AskedToWaitFor(RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is not { } date) return null;

        var wait = date - DateTimeOffset.UtcNow;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
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
