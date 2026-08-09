namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>Resolves an IAo3Scraper by its Key. Add new scrapers to DI and they show up here automatically.</summary>
public class ScraperRegistry
{
    private readonly Dictionary<string, IAo3Scraper> _scrapersByKey;

    public ScraperRegistry(IEnumerable<IAo3Scraper> scrapers)
    {
        _scrapersByKey = scrapers.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> AvailableKeys => _scrapersByKey.Keys;

    public IAo3Scraper? TryGet(string key) => _scrapersByKey.GetValueOrDefault(key);
}
