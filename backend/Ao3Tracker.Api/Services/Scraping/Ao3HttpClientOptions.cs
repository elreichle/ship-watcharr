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
    /// Root of the archive being scraped. Configurable so tests can point at a local stub, not so
    /// deployments can retarget it — every other assumption in this codebase is AO3-shaped.
    /// </summary>
    public string BaseUrl { get; set; } = "https://archiveofourown.org";

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
    /// The longest <c>Retry-After</c> this instance will hold a request open for. AO3 asking for
    /// more than this is not disregarded — it is taken as an answer rather than a wait: the request
    /// stops being retried, its caller records the failure, and the run's circuit breaker ends the
    /// pass. Coming back after this ceiling instead would be asking again sooner than AO3 said.
    ///
    /// It exists because <c>Retry-After</c> is a number the archive chooses and this instance has
    /// to schedule around: without a ceiling a single hour-long ask would keep one request — and,
    /// before the retry wait was moved off the shared gate, every other request on the instance —
    /// parked with nothing to show for it.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromMinutes(2);

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
    /// Ceiling on a single downloaded file. A response longer than this is abandoned part-way and
    /// the request that asked for it fails with a message.
    ///
    /// 64 MB is far above any real AO3 download — the longest works on the archive are a few
    /// megabytes as EPUB — and the number is not a guess at what AO3 sends. It is a bound on what
    /// one click may cost this instance's disk, since a chunked response has no length until it has
    /// finished arriving.
    /// </summary>
    public long MaxDownloadBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// How long one download's transfer may take, measured from the response headers arriving.
    ///
    /// Deliberately not from where the request was made: the gate wait, the 5–8s spacing and any
    /// retry backoff all come first, and a deadline covering those would fail a request that never
    /// received a byte as a transfer that stopped part-way.
    ///
    /// Separate from <c>HttpClient.Timeout</c>, which only bounds the wait for response *headers*
    /// once a body is being streamed. Without this a stalled transfer holds the global rate gate —
    /// the semaphore every outbound request queues behind — for as long as the socket stays open,
    /// so one hung download would stop this instance scraping at all rather than merely failing.
    /// </summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(5);

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
