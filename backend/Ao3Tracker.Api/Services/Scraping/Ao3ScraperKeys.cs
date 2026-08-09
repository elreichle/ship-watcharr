namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// The <c>ScrapeJob.ScraperKey</c> values this app schedules work under.
///
/// A key is written into the database the moment someone watches a ship, and it outlives any
/// particular build — so these are constants rather than literals scattered across controllers.
/// An implementation claims one by returning it from <c>IAo3Scraper.Key</c>.
/// </summary>
public static class Ao3ScraperKeys
{
    /// <summary>
    /// Walks a relationship tag's works index. Nothing registers under this key yet — the AO3
    /// parser hasn't been written (see README, "Extending the scaffold") — so jobs created for it
    /// are scheduled, log one "unknown scraper key" warning per due tick, and reschedule. That is
    /// deliberate: the alternative is inventing a placeholder key that the real scraper would then
    /// have to migrate away from.
    /// </summary>
    public const string ShipIndex = "ao3-ship-index";
}
