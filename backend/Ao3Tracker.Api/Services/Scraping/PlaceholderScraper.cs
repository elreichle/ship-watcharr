using Ao3Tracker.Api.Models;
using AngleSharp.Html.Parser;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Proves the scrape pipeline end-to-end (HTTP call → parse → persist → dashboard) without
/// touching real AO3 infrastructure. Deliberately targets example.com — IANA's reserved
/// domain for documentation/testing — rather than AO3, since real scraping logic hasn't
/// been implemented yet. Swap this out (or add scrapers alongside it) once real AO3 scrape
/// targets are defined; nothing else in the pipeline needs to change to do that.
/// </summary>
public class PlaceholderScraper : IAo3Scraper
{
    private const string TargetUrl = "https://example.com/";

    private readonly IRateLimitedHttpClient _httpClient;
    private readonly ILogger<PlaceholderScraper> _logger;

    public string Key => "placeholder";

    public PlaceholderScraper(IRateLimitedHttpClient httpClient, ILogger<PlaceholderScraper> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ScrapedResult>> ScrapeAsync(
        ApplicationUser user,
        (string Ao3Username, string Ao3Password)? ao3Credential,
        CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync(TargetUrl, ct);

        var parser = new HtmlParser();
        using var document = await parser.ParseDocumentAsync(response.Content, ct);
        var title = document.Title?.Trim();

        _logger.LogInformation(
            "Placeholder scrape for user {UserId} fetched {Url} (cached={FromCache}), title={Title}",
            user.Id, TargetUrl, response.FromCache, title);

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            statusCode = (int)response.StatusCode,
            fromCache = response.FromCache,
            fetchedAt = DateTimeOffset.UtcNow,
        });

        return [new ScrapedResult(TargetUrl, title, payload)];
    }
}
