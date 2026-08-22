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
    /// Walks a relationship tag's works index. Claimed by <see cref="Ao3ShipIndexScraper"/>, which
    /// implements the incremental and backfill passes; a full sweep is still to come.
    /// </summary>
    public const string ShipIndex = "ao3-ship-index";
}
