using System.Net;

namespace Ao3Tracker.Api.Services.Scraping;

public record ScrapeHttpResponse(string Content, HttpStatusCode StatusCode, bool FromCache);

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
