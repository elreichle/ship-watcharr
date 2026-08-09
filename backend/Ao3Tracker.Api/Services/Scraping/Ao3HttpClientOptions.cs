namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Configuration for <see cref="RateLimitedAo3HttpClient"/>. Bound from the "Ao3HttpClient"
/// config section. Defaults are deliberately conservative — AO3 is volunteer-run
/// infrastructure and this client must stay a well-behaved, low-load citizen.
/// </summary>
public class Ao3HttpClientOptions
{
    public const string SectionName = "Ao3HttpClient";

    /// <summary>Minimum spacing enforced between any two outgoing requests, regardless of origin.</summary>
    public TimeSpan MinDelayBetweenRequests { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a successful GET response is served from cache before being re-fetched.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(15);

    public int MaxRetries { get; set; } = 3;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sent as the User-Agent on every request. Must identify the tool and provide a way to
    /// reach the operator — replace the contact URL before pointing this at real AO3 pages.
    /// </summary>
    public string UserAgent { get; set; } =
        "Ao3TrackerBot/0.1 (self-hosted personal scrape tool; contact: set Ao3HttpClient:UserAgent in config)";
}
