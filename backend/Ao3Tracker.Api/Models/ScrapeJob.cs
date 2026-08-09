namespace Ao3Tracker.Api.Models;

/// <summary>
/// A recurring scrape configuration for one <see cref="Ship"/>. The scheduler polls these to
/// decide when to trigger a run; <see cref="ScraperKey"/> selects which IAo3Scraper runs.
///
/// Scoped to a ship, not to a user. Scraped data is shared, so a per-user job would mean N users
/// watching one ship produce N identical jobs hammering AO3 for the same pages. A job is created
/// when the first user watches a ship and disabled when the last one stops.
/// </summary>
public class ScrapeJob
{
    public int Id { get; set; }

    public int ShipId { get; set; }
    public Ship Ship { get; set; } = null!;

    public string Name { get; set; } = null!;

    /// <summary>Key used to resolve the IAo3Scraper implementation to run (see ScraperRegistry).</summary>
    public string ScraperKey { get; set; } = null!;

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    public bool IsEnabled { get; set; } = true;

    // DateTime (UTC), not DateTimeOffset: NextRunAt is range-compared by the scheduler,
    // and the SQLite provider can't translate >/</<=/>= on DateTimeOffset — see README.
    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<ScrapeRun> Runs { get; set; } = new List<ScrapeRun>();
}
