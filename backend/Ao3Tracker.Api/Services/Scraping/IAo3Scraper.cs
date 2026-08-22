using Ao3Tracker.Api.Models;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>What a scraper is being asked to do.</summary>
/// <remarks>
/// Carries the ship rather than a user: scraped data is shared, so a pass is defined by which tag
/// it walks, not by who asked. <see cref="Budget"/> is per-run and must be consulted before every
/// fetch — it owns the request cap, the wall-clock cap, and the circuit breaker.
/// </remarks>
public sealed record ScrapeContext(ScrapeJob Job, Ship Ship, ScrapeRunMode Mode, ScrapeBudget Budget);

/// <summary>
/// What a scraper did. Returned rather than accumulated in memory because a backfill persists
/// page by page and may legitimately stop part-way through when it exhausts its budget —
/// there is no final list of results to hand back.
/// </summary>
public sealed record ScrapeOutcome(
    int PagesFetched,
    int RequestsMade,
    int WorksSeen,
    int WorksAdded,
    int WorksUpdated,
    int ParseWarnings,
    int? FirstPage,
    int? LastPage,
    string StopReason,
    /// <summary>
    /// What went wrong, when <see cref="StopReason"/> says something did. Copied onto
    /// <c>ScrapeRun.ErrorMessage</c>, which until now only ever carried the message of an exception
    /// that escaped the scraper — so a run that stopped on a refused page recorded nothing a reader
    /// could act on.
    /// </summary>
    string? ErrorMessage = null)
{
    public static ScrapeOutcome Empty(string stopReason) =>
        new(0, 0, 0, 0, 0, 0, null, null, stopReason);
}

/// <summary>
/// Pluggable unit of scrape work. Implementations must go through
/// <see cref="IRateLimitedHttpClient"/> for all HTTP access — never construct a raw
/// HttpClient here — so every scraper automatically inherits rate limiting, retry/backoff,
/// and response caching. The scheduling layer only depends on this interface, so real AO3
/// scrapers can be added without touching <see cref="ScrapeWorker"/>.
/// </summary>
public interface IAo3Scraper
{
    /// <summary>Stable key used in ScrapeJob.ScraperKey to select this scraper.</summary>
    string Key { get; }

    /// <summary>Whether this scraper implements the requested pass.</summary>
    bool Supports(ScrapeRunMode mode);

    Task<ScrapeOutcome> ExecuteAsync(ScrapeContext context, CancellationToken ct = default);
}
