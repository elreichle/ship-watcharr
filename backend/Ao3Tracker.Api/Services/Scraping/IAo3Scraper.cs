using Ao3Tracker.Api.Models;

namespace Ao3Tracker.Api.Services.Scraping;

public record ScrapedResult(string SourceUrl, string? Title, string PayloadJson);

/// <summary>
/// Pluggable unit of scrape work. Implementations must go through
/// <see cref="IRateLimitedHttpClient"/> for all HTTP access — never construct a raw
/// HttpClient here — so every scraper automatically inherits rate limiting, retry/backoff,
/// and response caching. The scheduling/persistence layer only depends on this interface,
/// so real AO3 scrapers can be added without touching <see cref="ScrapeWorker"/>.
/// </summary>
public interface IAo3Scraper
{
    /// <summary>Stable key used in ScrapeJob.ScraperKey to select this scraper.</summary>
    string Key { get; }

    /// <summary>
    /// Runs one scrape pass for the given user. <paramref name="ao3Credential"/> is the
    /// user's decrypted AO3 username/password, or null if they haven't configured one yet
    /// (placeholder/public scrapers don't need it; real per-account AO3 scrapers should
    /// throw if it's missing).
    /// </summary>
    Task<IReadOnlyList<ScrapedResult>> ScrapeAsync(
        ApplicationUser user,
        (string Ao3Username, string Ao3Password)? ao3Credential,
        CancellationToken ct = default);
}
