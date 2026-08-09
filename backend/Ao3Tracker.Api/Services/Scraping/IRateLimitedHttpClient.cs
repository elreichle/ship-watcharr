using System.Net;

namespace Ao3Tracker.Api.Services.Scraping;

/// <param name="FinalUrl">
/// Where the request ended up after redirects, which is not always where it was sent. AO3 answers
/// a synonym tag by redirecting to its canonical one, so this is the only thing in the response
/// that says "the tag you asked for is really called something else" — the body of a synonym's
/// works index is otherwise indistinguishable from the canonical tag's. Null if the transport
/// didn't report one.
/// </param>
public record ScrapeHttpResponse(
    string Content,
    HttpStatusCode StatusCode,
    bool FromCache,
    string? FinalUrl = null);

/// <summary>
/// Shared HTTP entry point for every scraper. Enforces a minimum delay between requests,
/// retries with backoff on throttling/server errors, and serves unchanged pages from cache
/// instead of re-fetching them. Scrapers must not construct their own HttpClient — going
/// through this wrapper is what keeps the whole scraping subsystem rate-limited.
/// </summary>
public interface IRateLimitedHttpClient
{
    Task<ScrapeHttpResponse> GetAsync(string url, CancellationToken ct = default);
}
