namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Configuration for <see cref="RateLimitedAo3HttpClient"/>. Bound from the "Ao3HttpClient"
/// config section. Defaults are deliberately conservative — AO3 is volunteer-run
/// infrastructure and this client must stay a well-behaved, low-load citizen.
/// </summary>
public class Ao3HttpClientOptions
{
    public const string SectionName = "Ao3HttpClient";

    /// <summary>
    /// Lower bound on the spacing enforced between any two outgoing requests, regardless of origin.
    /// The actual delay is drawn uniformly from [Min, Max] — see <see cref="MaxDelayBetweenRequests"/>.
    /// </summary>
    public TimeSpan MinDelayBetweenRequests { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Upper bound on request spacing. Randomizing across the range raises the *average* delay
    /// (5s fixed becomes ~6.5s), so this makes the scraper gentler, not stealthier. It also stops
    /// several instances from settling into lockstep and delivering synchronized load spikes.
    ///
    /// Values below <see cref="MinDelayBetweenRequests"/> are clamped up to it rather than
    /// throwing, so a misconfiguration can only ever slow the scraper down.
    /// </summary>
    public TimeSpan MaxDelayBetweenRequests { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>How long a successful GET response is served from cache before being re-fetched.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(15);

    public int MaxRetries { get; set; } = 3;

    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Fraction of the computed backoff added or subtracted at random on each retry (0.2 = ±20%).
    /// Prevents a struggling AO3 from receiving a perfectly synchronized retry volley from every
    /// client that failed at the same moment.
    /// </summary>
    public double BackoffJitterFactor { get; set; } = 0.2;

    /// <summary>
    /// Consecutive transport/server failures before a run aborts. Stops a scrape from grinding
    /// through its whole budget against an archive that is plainly down.
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 3;

    /// <summary>
    /// Hard ceiling on requests a single run may make. Guards against a pagination bug walking
    /// forever; a backfill legitimately hits this and resumes from its cursor next run.
    /// </summary>
    public int MaxRequestsPerRun { get; set; } = 500;

    /// <summary>Wall-clock ceiling on a single run, so one ship cannot monopolize the shared gate.</summary>
    public TimeSpan MaxRunDuration { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Product token identifying the software, not the operator. Constant and public on purpose:
    /// it is what lets AO3 recognise this tool's traffic as a known, well-behaved client.
    /// </summary>
    public string ProductToken { get; set; } = "ShipWatcharr/0.1";

    /// <summary>
    /// How AO3 can reach whoever runs THIS deployment — an email or a project URL.
    ///
    /// Deliberately has no default here, so that no personal detail is ever committed to the
    /// repository and the tool's author is never the contact for a stranger's instance. Set it
    /// via user secrets locally, or AO3_OPERATOR_CONTACT in .env under Docker.
    ///
    /// Empty is legitimate rather than invalid: <see cref="IOperatorContactResolver"/> falls back
    /// to the first admin's email, so a deployment can supply a contact through the UI instead of
    /// through configuration. That email is optional, though, so it may be absent too — only when
    /// *every* source is empty does scraping stay disabled.
    /// </summary>
    public string OperatorContact { get; set; } = "";
}
