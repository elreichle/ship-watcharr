using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Drains the detail backlog: the works whose own page has never been read, or has been read and
/// then revised. See <see cref="Ao3WorkDetailScraper"/> for the pass itself.
///
/// A sibling of <see cref="ScrapeWorker"/> and <c>DownloadWorker</c> rather than a mode of either.
/// What is due to be scraped is decided by a ship's schedule and what is due to be downloaded by
/// someone having asked; what is due here is decided by a column, and by nobody waiting. All three
/// share the one thing that matters — the global rate gate inside
/// <see cref="IRateLimitedHttpClient"/> — so a detail fetch, a scrape and a download queue behind
/// one another rather than trebling this instance's load on AO3.
///
/// Its own timer rather than the scrape worker's minute, because this is the background work of the
/// three: nobody is waiting for a publication date, and a backlog that takes a week to fill in costs
/// a reader nothing but a row saying "Not fetched yet" in the meantime.
/// </summary>
public class WorkDetailWorker : BackgroundService
{
    /// <summary>
    /// How long the worker waits between passes.
    ///
    /// With <see cref="Ao3WorkDetailScraper.MaxWorksPerPass"/> this is the pacing of the whole
    /// feature: ten works every quarter of an hour is about a thousand a day, and about a minute in
    /// every fifteen spent on the shared gate. A fresh install with a few thousand works fills in
    /// over a few days, in an order that puts the never-fetched newest works first, and an instance
    /// that has caught up spends one database query per quarter of an hour and no requests at all.
    ///
    /// Nothing signals this worker awake, unlike the other two. A newly ingested work is the one
    /// thing that could, and it is not worth a wake: the pass that would pick it up is minutes away
    /// and nobody is waiting on it.
    /// </summary>
    internal static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkDetailWorker> _logger;
    private readonly Ao3HttpClientOptions _httpOptions;

    /// <summary>Last logged held/allowed state; null until the first pass with a backlog.</summary>
    private bool? _allowed;

    public WorkDetailWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<WorkDetailWorker> logger,
        IOptions<Ao3HttpClientOptions> httpOptions)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _httpOptions = httpOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Work detail worker starting, reading up to {Works} work page(s) every {Interval}",
            Ao3WorkDetailScraper.MaxWorksPerPass, PassInterval);

        while (true)
        {
            try
            {
                await RunPassAsync(stoppingToken);
            }
            catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, stoppingToken))
            {
                // Nothing may end this loop. A BackgroundService that throws stops the host, so an
                // escaping exception would not merely lose one pass — it would take the API down
                // with it. The same rule ScrapeWorker's and DownloadWorker's loops are written to.
                _logger.LogError(ex, "Unhandled error while reading work detail pages");
            }

            // Measured from the end of a pass rather than on a fixed schedule: a pass that spent its
            // whole budget behind a busy rate gate must not be followed immediately by another.
            await Task.Delay(PassInterval, stoppingToken);
        }
    }

    internal async Task RunPassAsync(CancellationToken ct)
    {
        // Nobody is waiting on a detail page in particular, so at the gate these yield to
        // everything else — the ship walks, and anything a reader asked for.
        using var _ = Ao3AmbientPriority.Enter(Ao3RequestPriority.Background);

        using var scope = _scopeFactory.CreateScope();

        if (!await MayFetchAsync(scope.ServiceProvider, ct)) return;

        // One budget per pass, the way a drain and a run each have one: the cap, the wall-clock
        // ceiling and the circuit breaker are all per-pass, and works left unread stay at the head of
        // the backlog for the next one.
        var budget = new ScrapeBudget(_httpOptions);

        await scope.ServiceProvider.GetRequiredService<IAo3WorkDetailScraper>().RunAsync(budget, ct);
    }

    /// <summary>
    /// Whether this instance may make an AO3 request at all: configured to identify itself, and
    /// logged in.
    /// </summary>
    /// <remarks>
    /// The same two gates the scrape worker and the download worker apply, because they are about
    /// the instance rather than about any one kind of request — reading a work's page without an
    /// honest User-Agent is the thing this project refuses to do. A held pass reads nothing, records
    /// nothing and fails nothing: the backlog is exactly where it was, and the next pass re-checks,
    /// so a fresh install starts filling in as soon as its login is saved.
    ///
    /// Unlike those two this checks the gates before looking for work, and so may evaluate them on an
    /// instance with an empty backlog. That is deliberate — the alternative is a query per pass on a
    /// database the gate would refuse to act on anyway — and it costs nothing outbound: the gate is a
    /// local read, and the session check below sends a request only when there is no usable session
    /// cached.
    /// </remarks>
    private async Task<bool> MayFetchAsync(IServiceProvider services, CancellationToken ct)
    {
        var gate = await services.GetRequiredService<ScrapingGate>().EvaluateAsync(ct);

        if (!gate.CanScrape)
        {
            // Logged on transition only, and below the scrape worker's level: that worker reports the
            // identical blockers in full whenever they change, and repeating them here would say the
            // same thing twice.
            if (_allowed != false)
            {
                _logger.LogWarning("Work detail fetching is held.\n\n{Problem}", gate.Problem);
                _allowed = false;
            }

            return false;
        }

        var session = await services.GetRequiredService<IAo3SessionProvider>().EnsureSessionAsync(ct);

        if (!session.Success)
        {
            if (_allowed != false)
            {
                _logger.LogWarning(
                    "Work detail fetching is held: this instance is not logged in to AO3.\n\n{Error}",
                    session.Error);
                _allowed = false;
            }

            return false;
        }

        _allowed = true;
        return true;
    }
}
