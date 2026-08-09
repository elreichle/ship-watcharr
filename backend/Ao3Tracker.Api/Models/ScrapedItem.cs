namespace Ao3Tracker.Api.Models;

/// <summary>
/// Generic placeholder for whatever a scraper produces. This is intentionally loose
/// (a source URL + title + free-form JSON payload) since real AO3 scrape targets
/// (works, bookmarks, series, stats...) haven't been defined yet — expect this to be
/// redesigned into target-specific tables once they are.
/// </summary>
public class ScrapedItem
{
    public int Id { get; set; }

    public int ScrapeRunId { get; set; }
    public ScrapeRun ScrapeRun { get; set; } = null!;

    public string SourceUrl { get; set; } = null!;
    public string? Title { get; set; }

    /// <summary>Raw JSON payload for whatever fields the scraper extracted.</summary>
    public string PayloadJson { get; set; } = "{}";

    // DateTime (UTC), not DateTimeOffset: ScrapedAt is ordered by — see README.
    public DateTime ScrapedAt { get; set; } = DateTime.UtcNow;
}
