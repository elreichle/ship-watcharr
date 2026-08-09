namespace Ao3Tracker.Api.Models;

/// <summary>
/// A per-user, recurring scrape configuration. The scheduler polls these to decide
/// when to trigger a run; <see cref="ScraperKey"/> selects which IAo3Scraper implementation runs.
/// </summary>
public class ScrapeJob
{
    public int Id { get; set; }

    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>Key used to resolve the IAo3Scraper implementation to run (see ScraperRegistry).</summary>
    public string ScraperKey { get; set; } = null!;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ScrapeRun> Runs { get; set; } = new List<ScrapeRun>();
}
