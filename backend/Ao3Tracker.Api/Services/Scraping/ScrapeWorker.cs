using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// In-process job scheduler: polls due ScrapeJobs, runs each one through its configured
/// IAo3Scraper, and records a ScrapeRun per execution.
/// This is a BackgroundService (not an OS-level service) because everything — API and
/// worker — ships as one container/process.
///
/// The poll is a floor, not the only trigger: following a tag makes its job due immediately and
/// signals <see cref="ScrapeWakeSignal"/>, so a first pass starts without waiting out a tick.
/// </summary>
public class ScrapeWorker : BackgroundService
{
    /// <summary>
    /// How long the worker sleeps when nothing wakes it. Only jobs that came due while it slept
    /// depend on this — a newly followed ship arrives by signal instead.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a run may go without a heartbeat before startup treats it as dead. Generous
    /// because a single page fetch can legitimately take a while behind the rate limiter.
    /// </summary>
    private static readonly TimeSpan StaleRunThreshold = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Fraction of the interval either side of <c>now + interval</c> within which a job's next run
    /// may land. See <see cref="SpreadWithin"/> and <see cref="NextRunAfter"/>.
    /// </summary>
    private const double ScheduleJitterFactor = 0.1;

    /// <summary>
    /// How many incremental runs in a row must find nothing before a job's interval is stretched,
    /// and the most it is stretched by. See <see cref="QuietStretch"/>.
    /// </summary>
    internal const int QuietRunsBeforeStretching = 2;
    internal const int MaxQuietStretch = 4;

    /// <summary>
    /// How long a ship whose whole listing has never been read logged in waits, after its last walk
    /// of that listing, before the worker schedules a sweep. A ship that has been read logged in is
    /// never scheduled one: see <see cref="FullSweepIsDue"/>.
    ///
    /// A sweep is the most expensive thing this application does to one tag: one request per page of
    /// the whole listing, so a 4,000-work tag is 200 requests against an incremental pass's one, and
    /// at the shared 5–8 second gate it occupies this instance's only outbound channel for the best
    /// part of half an hour. Across the first production instance's 54 ships that was ~12,000 pages
    /// a cycle, more than every incremental pass combined, which is why a sweep is now scheduled only
    /// until it has done the one thing no other pass can: read every page with a session.
    ///
    /// The interval still spaces those attempts. A sweep that is abandoned, or that reads a page
    /// logged out, leaves the ship uncovered, and without the gap it would be retried every tick.
    /// </summary>
    internal static readonly TimeSpan FullSweepInterval = TimeSpan.FromDays(30);

    /// <summary>
    /// How many slots the sweep interval is divided into when spreading ships across it. See
    /// <see cref="FullSweepIsDue"/>; 30 over a 30-day interval is one slot a day.
    /// </summary>
    private const int FullSweepStaggerSlots = 30;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScrapeWorker> _logger;
    private readonly Ao3HttpClientOptions _httpOptions;
    private readonly ScrapeWakeSignal _wake;

    /// <summary>
    /// The outbound channel, read for one thing: whether AO3 has asked the instance to wait, and
    /// until when — so a job can be put back just past that moment rather than run into it.
    /// </summary>
    private readonly Ao3RateGate _gate;

    /// <summary>
    /// The clock every scheduling decision reads: what is due, which pass a due job gets, when a
    /// run started and when it went stale.
    /// </summary>
    /// <remarks>
    /// Injected rather than read off the wall, because the scrapers this worker drives already read
    /// it — <c>Ao3ShipIndexScraper</c> stamps <see cref="Ship.LastFullSweepStartedAt"/> from it —
    /// and a rule comparing one clock against a date written by another cannot be tested at all: a
    /// fixture whose clock sits in the past would find every sweep it had just stamped due again on
    /// the next tick, which is the real bug that shape hides.
    /// </remarks>
    private readonly TimeProvider _time;

    /// <summary>Last logged scraping-enabled state; null until the first check. See RunDueJobsAsync.</summary>
    private bool? _scrapingEnabled;

    /// <summary>Last logged login state; null until the first attempt. See HasSessionAsync.</summary>
    private bool? _loggedIn;

    public ScrapeWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<ScrapeWorker> logger,
        IOptions<Ao3HttpClientOptions> httpOptions,
        ScrapeWakeSignal wake,
        TimeProvider time,
        Ao3RateGate gate)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _httpOptions = httpOptions.Value;
        _wake = wake;
        _time = time;
        _gate = gate;
    }

    /// <summary>Now, from the injected clock — see <see cref="_time"/>.</summary>
    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Scrape worker starting, polling every {PollInterval}, request spacing {Min}-{Max}",
            PollInterval, _httpOptions.MinDelayBetweenRequests, _httpOptions.MaxDelayBetweenRequests);

        await ReconcileInterruptedRunsAsync(stoppingToken);

        // Sweep, then sleep until woken or until the interval expires — rather than a PeriodicTimer,
        // whose tick cannot be brought forward by a follow. Measuring the wait from the end of a
        // sweep also stops a long backfill from stacking up ticks it owed while it ran.
        while (true)
        {
            try
            {
                await RunDueJobsAsync(stoppingToken);
            }
            catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, stoppingToken))
            {
                // Nothing a scraper can throw may end this loop. A BackgroundService that throws
                // stops the host, so an escaping exception does not merely lose one ship's run — it
                // takes the API down with it. See ScrapeCancellation.
                _logger.LogError(ex, "Unhandled error while polling scrape jobs");
            }

            await _wake.WaitAsync(PollInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Closes out runs left mid-flight by a crash or a hard shutdown. Without this they stay
    /// Running forever, and since backfills legitimately run for hours there is no timeout that
    /// could distinguish "still working" from "died three days ago" at read time.
    /// </summary>
    private async Task ReconcileInterruptedRunsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var cutoff = UtcNow - StaleRunThreshold;
            var stale = await db.ScrapeRuns
                .Where(r => r.Status == ScrapeRunStatus.Running)
                .Where(r => (r.HeartbeatAt ?? r.StartedAt) < cutoff)
                .ToListAsync(ct);

            if (stale.Count == 0) return;

            foreach (var run in stale)
            {
                run.Status = ScrapeRunStatus.Interrupted;
                run.CompletedAt = UtcNow;
                run.StopReason = ScrapeStopReason.Interrupted;
                run.ErrorMessage ??= "Run did not complete — the application stopped while it was in progress.";
            }

            await db.SaveChangesAsync(ct);
            _logger.LogWarning("Marked {Count} stale scrape run(s) as Interrupted on startup", stale.Count);
        }
        catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
        {
            _logger.LogError(ex, "Failed to reconcile interrupted scrape runs");
        }
    }

    internal async Task RunDueJobsAsync(CancellationToken ct)
    {
        // The scheduled middle of the gate's order: behind whatever a reader is waiting on, ahead
        // of the detail pages filling in behind the scenes. Stated rather than left to the default
        // so that the four workers' places in the queue are all written down somewhere.
        using var _ = Ao3AmbientPriority.Enter(Ao3RequestPriority.Scheduled);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Re-checked every poll rather than once at startup. A fresh install boots with neither an
        // operator contact nor an AO3 login — both only appear once someone saves them in the
        // settings UI — so a one-shot check would latch scraping off and never notice that
        // happening.
        var gate = scope.ServiceProvider.GetRequiredService<ScrapingGate>();
        var state = await gate.EvaluateAsync(ct);

        if (!state.CanScrape)
        {
            // Logged on transition only. This runs every minute, and an instance that is not
            // configured to scrape is a steady state, not an event worth repeating 1,440 times a
            // day. Every reason at once, so fixing one does not merely reveal the next.
            //
            // Due jobs are left exactly as they are: no run recorded, no NextRunAt advanced, no
            // breaker touched. They are held, not failed — nothing has been attempted.
            if (_scrapingEnabled != false)
            {
                _logger.LogError("Scraping is disabled.\n\n{Problem}", state.Problem);
                _scrapingEnabled = false;
            }
            return;
        }

        if (_scrapingEnabled != true)
        {
            // Logged verbatim so the operator can see exactly what this instance tells AO3 about
            // itself, rather than reconstructing it from three separate settings.
            _logger.LogInformation("Scraping enabled. Identifying to AO3 as: {UserAgent}", state.UserAgent);
            _scrapingEnabled = true;
        }

        var now = UtcNow;

        // Ids, not entities: each job is run in a scope of its own below, and an entity tracked by
        // this scope's context has no business being written through that one.
        var dueJobIds = await db.ScrapeJobs
            .Where(j => j.IsEnabled)
            .Where(j => !j.NextRunAt.HasValue || j.NextRunAt.Value <= now)
            .OrderBy(j => j.NextRunAt)
            .Select(j => j.Id)
            .ToListAsync(ct);

        // Nothing is due, so nothing needs a session. Checked before logging in rather than after:
        // an idle instance re-authenticating on a timer would be two requests an hour that read
        // nothing, which is exactly the load this scraper exists to avoid putting on AO3.
        if (dueJobIds.Count == 0) return;

        if (!await HasSessionAsync(scope.ServiceProvider, ct)) return;

        foreach (var jobId in dueJobIds)
        {
            ct.ThrowIfCancellationRequested();

            // AO3 has asked the instance to wait, and for longer than any one request will hold
            // itself open. Running the job would send its first request to the gate to park there
            // for the rest of the hold, and every job behind it in this tick to queue behind that;
            // deferring is the same wait without the queue, and it leaves the run history clean —
            // nothing was attempted, so nothing is recorded, the way the configuration gates hold.
            // A shorter hold is left to the gate: the request waits it out inside itself.
            if (_gate.HeldUntil is { } heldUntil && heldUntil - _time.GetUtcNow() > _httpOptions.MaxRetryAfter)
            {
                using var deferScope = _scopeFactory.CreateScope();
                await DeferPastTheHoldAsync(deferScope.ServiceProvider, jobId, heldUntil, ct);
                continue;
            }

            // One scope per job, which is what Program.cs says the worker does. Sharing a scope
            // across the tick shares the AppDbContext, the scraper and the ingestor between every
            // due job, so a change set one job's database refused is still tracked when the next
            // job saves — one ship's bad page then fails every ship behind it.
            using var jobScope = _scopeFactory.CreateScope();

            try
            {
                await RunJobAsync(jobScope.ServiceProvider, jobId, ct);
            }
            catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
            {
                // RunJobAsync records its own failures; anything still escaping happened around
                // the run rather than inside it. Contained here so it costs one job and not the
                // rest of the poll.
                _logger.LogError(ex, "ScrapeJob {JobId} could not be run", jobId);
            }
        }
    }

    /// <summary>
    /// Establishes the AO3 session the due jobs are about to scrape as, if there is not already a
    /// usable one cached. Normally this sends nothing: the session outlives many polls.
    ///
    /// A login that fails holds the jobs exactly as the configuration gates do — no run recorded, no
    /// NextRunAt advanced, no breaker touched. A wrong password and an archive that is down are both
    /// states someone has to fix, and burning the jobs against either would turn one problem into a
    /// schedule full of failures. Logged on transition only, for the same reason RunDueJobsAsync
    /// logs its gates that way.
    /// </summary>
    private async Task<bool> HasSessionAsync(IServiceProvider services, CancellationToken ct)
    {
        var result = await services.GetRequiredService<IAo3SessionProvider>().EnsureSessionAsync(ct);

        if (!result.Success)
        {
            if (_loggedIn != false)
            {
                _logger.LogError(
                    "Scraping is held: this instance is not logged in to AO3.\n\n{Error}", result.Error);
                _loggedIn = false;
            }

            return false;
        }

        if (_loggedIn != true)
        {
            _logger.LogInformation("Scraping as AO3 account {Ao3Username}", result.Username ?? "(stored login)");
            _loggedIn = true;
        }

        return true;
    }

    /// <summary>
    /// Whether this tick is one the ship spends re-walking its whole listing rather than reading the
    /// newest end of it.
    ///
    /// A sweep already under way always wins: it holds a cursor, and the pages it has walked so far
    /// are worth nothing until it reaches the end of the listing, so leaving one part-finished for a
    /// tick is leaving it part-finished for ever. That does mean a ship the sweep takes several runs
    /// to walk gets no incremental pass while it walks — its new works are picked up when the sweep
    /// ends, later than usual but not lost, since the watermark has not moved.
    ///
    /// Otherwise the worker schedules one only for a ship whose whole listing has never been read
    /// logged in (<see cref="Ship.WholeListingReadLoggedInAt"/>). That walk is what puts the tag's
    /// restricted works in the library, and nothing else will; once it has happened, re-walking on a
    /// schedule would only refresh old works' kudos and hits and notice old works leaving the tag,
    /// which was judged not worth a whole listing's requests a month (DECISIONS 2026-09-12). After
    /// that only an admin's request walks the whole listing.
    ///
    /// For a ship still owed that walk it is the interval, measured from the last sweep's *start*.
    /// From the start rather than its completion because a sweep that got nowhere is abandoned rather
    /// than completed (see
    /// <c>Ao3ShipIndexScraper.RecordSweepProgressAsync</c>), and measuring from a completion it never
    /// reached would make the next tick due it again — a ship whose listing refuses a page would then
    /// spend every tick on a sweep that cannot finish, and never run an incremental pass again.
    ///
    /// A ship that has never swept measures from its backfill instead: the backfill is a walk of the
    /// whole listing too, so a ship that has just finished one has exactly the coverage a sweep would
    /// have given it. A backfill that was written off leaves no completion date, and that ship falls
    /// back to when it was first followed — which is the case the comment in
    /// <c>RecordBackfillProgress</c> means by "a full sweep is what can close the gap".
    ///
    /// That last fallback is the one date here outside the injected clock: <see cref="Ship.CreatedAt"/>
    /// is stamped by the model as a row is added, so a fixture that sets its clock to a fixed past
    /// date sees a never-swept ship as followed in the future and never due. Left rather than
    /// clock-injected at follow time because it would put the clock through every writer of a Ship
    /// row for one fallback; a test of this arm sets one of the two dates above instead.
    /// </summary>
    internal static bool FullSweepIsDue(Ship ship, DateTime now)
    {
        if (ship.FullSweepNextPage is not null) return true;

        // An admin asked for one. Not spaced like the scheduled sweep, because the request is the
        // spacing: it is one walk, and starting that walk is what clears it.
        if (ship.FullSweepRequestedAt is not null) return true;

        if (ship.WholeListingReadLoggedInAt is not null) return false;

        var lastWholeListing =
            ship.LastFullSweepStartedAt ?? ship.BackfillCompletedAt ?? ship.CreatedAt;

        return now - lastWholeListing >= FullSweepInterval + StaggerOf(ship.Id);
    }

    /// <summary>
    /// A fixed per-ship offset on the sweep interval, so that ships do not all take their first
    /// logged-in walk at once.
    ///
    /// The case this exists for is a tick on which many uncovered ships come due together — the
    /// first after an upgrade, when every ship already followed has a backfill that completed, or a
    /// follow date, well over an interval ago. Without an offset every one of them is due on the same
    /// poll — and since a sweep in flight
    /// beats the incremental pass, the instance would stop collecting new works on every ship at
    /// once until the whole backlog of full-listing walks drained, one after another behind the
    /// shared gate. It is <see cref="NextRunAfter"/>'s problem one level up, and the same answer.
    ///
    /// Derived from the ship id rather than drawn at random, because this is read on every poll and
    /// must give the same answer each time: a random offset would re-roll the due date every minute
    /// and average out to no spread at all. It makes an uncovered ship's attempts one interval plus
    /// up to one more apart — 30 to 60 days as configured. A covered ship is never scheduled, so the
    /// offset never applies to it.
    /// </summary>
    private static TimeSpan StaggerOf(int shipId) =>
        FullSweepInterval * ((shipId % FullSweepStaggerSlots) / (double)FullSweepStaggerSlots);

    /// <summary>
    /// Schedules a run at <c>now + interval</c>, spread at random by ±<see cref="ScheduleJitterFactor"/>.
    ///
    /// The fallback spread, for the waits that are not about the other ships: a run deferred past
    /// AO3's hold, a job whose scraper is missing. Random so that several such jobs do not all
    /// come back on one tick. A job's ordinary next run goes through <see cref="NextRunSpreadAsync"/>
    /// instead, which places it by where the other ships already are.
    /// </summary>
    internal static DateTime NextRunAfter(TimeSpan interval, DateTime now)
    {
        var multiplier = 1 + ((Random.Shared.NextDouble() * 2 - 1) * ScheduleJitterFactor);
        return now + (interval * multiplier);
    }

    /// <summary>
    /// Schedules the job's next run at <c>now + interval</c>, moved by up to
    /// ±<see cref="ScheduleJitterFactor"/> to the point in that window farthest from any other
    /// job's next run.
    /// </summary>
    /// <remarks>
    /// <para>The ships share one outbound channel, so what matters about a next-run time is not
    /// only when it is but who else is due then. Fifty ships on a six-hour interval have room for
    /// one every seven minutes; what production had instead was thirty-three of them due inside
    /// ninety minutes, each then queued behind the others' runs — a backfill among them holding
    /// the channel for half an hour — and each rescheduled from whenever it finally finished, so
    /// the cluster carried itself forward.</para>
    /// <para>Random jitter, which this replaces, did not fix that; it only stopped the times being
    /// identical. A random walk of ±36 minutes per cycle leaves ships as likely to drift together
    /// as apart. Placing each run in the largest gap the others leave is a rule the ships apply to
    /// each other every cycle, so a spread once made is kept, and a cluster — a batch followed at
    /// once, an outage that made everything due together — dissolves over a few cycles as each
    /// job steps toward its nearest empty stretch.</para>
    /// <para>Read from the jobs table on every completion. Fifty rows, one query, once per run;
    /// and in a <c>finally</c>, so a failed read falls back to the random spread rather than losing
    /// the schedule.</para>
    /// </remarks>
    private async Task<DateTime> NextRunSpreadAsync(
        AppDbContext db, int jobId, TimeSpan interval, DateTime now, CancellationToken ct)
    {
        try
        {
            // A margin of one spread either side, so a neighbour just outside the window still
            // pushes this run away from the edge nearest it.
            var margin = interval * ScheduleJitterFactor;
            var from = now + interval - (2 * margin);
            var to = now + interval + (2 * margin);

            var neighbours = await db.ScrapeJobs
                .Where(j => j.Id != jobId && j.IsEnabled && j.NextRunAt != null)
                .Where(j => j.NextRunAt >= from && j.NextRunAt <= to)
                .Select(j => j.NextRunAt!.Value)
                .ToListAsync(ct);

            return SpreadWithin(interval, now, neighbours);
        }
        catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
        {
            _logger.LogWarning(ex, "Could not read the other jobs' schedules; spreading ScrapeJob {JobId} at random", jobId);
            return NextRunAfter(interval, now);
        }
    }

    /// <summary>
    /// The point within ±<see cref="ScheduleJitterFactor"/> of <c>now + interval</c> farthest from
    /// every time in <paramref name="neighbours"/>; the centre when there are none.
    /// </summary>
    internal static DateTime SpreadWithin(TimeSpan interval, DateTime now, IReadOnlyList<DateTime> neighbours)
    {
        var centre = now + interval;
        var reach = interval * ScheduleJitterFactor;
        var start = centre - reach;
        var end = centre + reach;

        if (neighbours.Count == 0) return centre;

        // Candidates: the window's ends and the midpoint of every gap between neighbours inside
        // it. The best point of a piecewise-linear "distance to nearest neighbour" is always one
        // of those.
        var sorted = neighbours.Order().ToList();
        var candidates = new List<DateTime> { start, end };

        for (var i = 0; i + 1 < sorted.Count; i++)
        {
            var midpoint = sorted[i] + ((sorted[i + 1] - sorted[i]) / 2);
            if (midpoint > start && midpoint < end) candidates.Add(midpoint);
        }

        TimeSpan NearestNeighbour(DateTime at) => sorted.Min(n => (n - at).Duration());

        // Ties go to the earlier candidate, which keeps the choice deterministic.
        return candidates
            .OrderByDescending(NearestNeighbour)
            .ThenBy(c => c)
            .First();
    }

    private async Task RunJobAsync(IServiceProvider services, int jobId, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var registry = services.GetRequiredService<ScraperRegistry>();

        // Re-read inside this job's own scope. The poll selected ids; this is where the row and its
        // ship become entities, tracked by the context the scrape will write through.
        var job = await db.ScrapeJobs
            .Include(j => j.Ship)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null)
        {
            // Unfollowed between the poll's query and now. Nothing to run and nothing to record.
            _logger.LogInformation("ScrapeJob {JobId} no longer exists; skipping", jobId);
            return;
        }

        var scraper = registry.TryGet(job.ScraperKey);
        if (scraper is null)
        {
            _logger.LogWarning("ScrapeJob {JobId} references unknown scraper key {ScraperKey}", job.Id, job.ScraperKey);
            job.NextRunAt = NextRunAfter(job.Interval, UtcNow);
            await db.SaveChangesAsync(ct);
            return;
        }

        // A ship still working through its back catalogue keeps backfilling; everything else takes
        // the cheap newest-first pass, or the sweep when one is owed.
        var mode = job.Ship.BackfillState is ShipBackfillState.NotStarted or ShipBackfillState.InProgress
            ? ScrapeRunMode.Backfill
            : FullSweepIsDue(job.Ship, UtcNow)
                ? ScrapeRunMode.FullSweep
                : ScrapeRunMode.Incremental;

        if (!scraper.Supports(mode))
        {
            _logger.LogWarning(
                "Scraper {ScraperKey} does not support {Mode} for ScrapeJob {JobId}", job.ScraperKey, mode, job.Id);
            job.NextRunAt = NextRunAfter(job.Interval, UtcNow);
            await db.SaveChangesAsync(ct);
            return;
        }

        var run = new ScrapeRun
        {
            ScrapeJobId = job.Id,
            Status = ScrapeRunStatus.Running,
            Mode = mode,

            // Both dates from this clock, overriding the model's own default for StartedAt. It is
            // what the run history and "last run" order by, so leaving it on the wall clock while
            // its heartbeat and completion came from here would give one row two clocks — and a
            // fixture that moved time would file runs 90 days apart within a millisecond of each
            // other, or complete one before it started.
            StartedAt = UtcNow,
            HeartbeatAt = UtcNow,
        };
        db.ScrapeRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var budget = new ScrapeBudget(_httpOptions);

        try
        {
            var outcome = await scraper.ExecuteAsync(new ScrapeContext(job, job.Ship, mode, budget), ct);

            run.PagesFetched = outcome.PagesFetched;
            run.RequestsMade = outcome.RequestsMade;
            run.WorksSeen = outcome.WorksSeen;
            run.WorksAdded = outcome.WorksAdded;
            run.WorksUpdated = outcome.WorksUpdated;
            run.ParseWarnings = outcome.ParseWarnings;
            run.FirstPageFetched = outcome.FirstPage;
            run.LastPageFetched = outcome.LastPage;
            run.StopReason = outcome.StopReason;
            run.ErrorMessage = outcome.ErrorMessage;

            // Returning is how a scraper reports most of its failures — a 404 on the first page, a
            // non-OK status mid-walk, a page nothing on which could be dated all stop the run with
            // Error rather than throwing. Recording those as Succeeded leaves the run history, the
            // only place this worker reports itself, agreeing that everything went fine.
            //
            // `Held` is recorded the same way for the same reason: the run stopped short of a page
            // the last several runs could not get an answer from, and declined to spend a request
            // on it. It read and ingested everything up to that page, but it did not get through
            // the listing — filing that as a success would take the one ship on the instance that
            // needs looking at and hide it among the healthy ones.
            //
            // `Denied` likewise: the job ran, asked for nothing, and cannot ask for anything until
            // the tag is verified again. A success is what an operator scrolls past.
            //
            // `Breaker` is the one budget stop on that list. `Cap` and `TimeCap` are a run spending
            // an allowance it was given, which is the expected end of a backfill; the breaker only
            // opens after MaxConsecutiveFailures requests in a row reached AO3 and came back
            // unusable, spaced by the shared 5-8s gate. That is the archive failing this ship, and
            // it is the state the Schedules page most needs to show — a ship collecting nothing
            // while its run history reads green is exactly what this rule exists to prevent.
            run.Status = ScrapeStopReason.RecordsAsFailure(outcome.StopReason)
                ? ScrapeRunStatus.Failed
                : ScrapeRunStatus.Succeeded;
        }
        catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
        {
            // A timed-out request lands here rather than escaping to kill the host, and is recorded
            // as this run failing — which is what it is. See ScrapeCancellation.
            _logger.LogError(ex, "ScrapeJob {JobId} ({ScraperKey}) failed", job.Id, job.ScraperKey);
            run.Status = ScrapeRunStatus.Failed;
            run.StopReason = ScrapeStopReason.Error;
            run.ErrorMessage = ex.Message;
        }
        finally
        {
            // Taken from the budget rather than the outcome: a run that threw part-way has no
            // outcome, and how much of its allowance it had already spent is exactly what the
            // run history needs to show.
            run.HitRequestCap = budget.HitRequestCap;
            run.HitTimeCap = budget.HitTimeCap;

            run.CompletedAt = UtcNow;
            run.HeartbeatAt = run.CompletedAt;
            job.LastRunAt = run.CompletedAt;
            job.NextRunAt = run.StopReason == ScrapeStopReason.Throttled
                ? JustPastTheHold(UtcNow)
                : await NextRunSpreadAsync(
                    db, job.Id, job.Interval * await QuietStretchAsync(db, job, run, ct), UtcNow, ct);

            await PersistCompletionAsync(db, run, job, ct);
        }
    }

    /// <summary>
    /// Puts a due job back to when AO3 said, without running it. See the caller for when.
    /// </summary>
    private async Task DeferPastTheHoldAsync(
        IServiceProvider services, int jobId, DateTimeOffset heldUntil, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var job = await db.ScrapeJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return;

        job.NextRunAt = JustPastTheHold(UtcNow);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "ScrapeJob {JobId} is deferred to {NextRunAt:u}: AO3 has asked this instance to wait until {HeldUntil:u}, "
            + "longer than a request is held open for.",
            job.Id, job.NextRunAt, heldUntil);
    }

    /// <summary>
    /// The next-run time for a job that ran into AO3's throttling: the end of the hold, spread the
    /// way every other next-run time is, or the retry ceiling from now when the gate has no hold on
    /// record — which happens when the ask was never read, and is a wait AO3 was already owed.
    /// </summary>
    /// <remarks>
    /// Never sooner than a minute: a hold that ends inside this poll would otherwise be re-run on
    /// the next tick, and a run that stopped for throttling has told AO3 nothing that makes the
    /// next request more welcome than the last.
    /// </remarks>
    private DateTime JustPastTheHold(DateTime now)
    {
        var remaining = _gate.HeldUntil is { } heldUntil
            ? heldUntil.UtcDateTime - now
            : _httpOptions.MaxRetryAfter;

        if (remaining < TimeSpan.FromMinutes(1)) remaining = TimeSpan.FromMinutes(1);

        return NextRunAfter(remaining, now);
    }

    /// <summary>
    /// How many intervals to wait before this job's next incremental pass, given how the last few
    /// went: one, until <see cref="QuietRunsBeforeStretching"/> passes in a row have found nothing,
    /// then doubling per further quiet pass up to <see cref="MaxQuietStretch"/>, and back to one the
    /// moment a pass finds a work.
    /// </summary>
    /// <remarks>
    /// <para>The instance has one outbound channel, and every ship's six-hourly page 1 is a request
    /// on it whether the tag has moved or not. A tag that updates twice a month answers a hundred
    /// and twenty of those a month with two that found anything. Stretching the quiet ones to a day
    /// keeps most of that channel for the tags that are moving, the backfills, and the downloads a
    /// reader is waiting on — at the cost of seeing a quiet tag's rare new work up to a day late
    /// instead of six hours, once it has been quiet for the best part of one already.</para>
    /// <para>Only a succeeded incremental pass counts as quiet, and only incremental passes are
    /// looked at: a backfill or a sweep touching rows says nothing about whether the tag is active,
    /// and an error says nothing about the tag at all. A run with anything to show for it resets the
    /// count — the ship is moving, and the next check comes at the plain interval.</para>
    /// <para>Read from the run history rather than kept as a column: the history already records
    /// exactly what this needs, and the count is short. Anything going wrong in the read answers
    /// one — this runs in a <c>finally</c>, and the schedule must not be lost to a failed query.</para>
    /// </remarks>
    private async Task<int> QuietStretchAsync(AppDbContext db, ScrapeJob job, ScrapeRun run, CancellationToken ct)
    {
        if (!IsQuiet(run.Mode, run.Status, run.WorksAdded, run.WorksUpdated)) return 1;

        try
        {
            var earlier = await db.ScrapeRuns
                .Where(r => r.ScrapeJobId == job.Id && r.Id != run.Id && r.Mode == ScrapeRunMode.Incremental)
                .OrderByDescending(r => r.StartedAt)
                .ThenByDescending(r => r.Id)
                .Take(QuietRunsBeforeStretching + 2)
                .Select(r => new { r.Status, r.WorksAdded, r.WorksUpdated })
                .ToListAsync(ct);

            var quietInARow = 1 + earlier
                .TakeWhile(r => IsQuiet(ScrapeRunMode.Incremental, r.Status, r.WorksAdded, r.WorksUpdated))
                .Count();

            return QuietStretch(quietInARow);
        }
        catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
        {
            _logger.LogWarning(ex, "Could not read ScrapeJob {JobId}'s recent runs; scheduling at the plain interval", job.Id);
            return 1;
        }
    }

    private static bool IsQuiet(ScrapeRunMode mode, ScrapeRunStatus status, int worksAdded, int worksUpdated) =>
        mode == ScrapeRunMode.Incremental
        && status == ScrapeRunStatus.Succeeded
        && worksAdded == 0
        && worksUpdated == 0;

    /// <summary>The multiplier for a job whose last <paramref name="quietInARow"/> incremental passes found nothing.</summary>
    internal static int QuietStretch(int quietInARow)
    {
        if (quietInARow < QuietRunsBeforeStretching) return 1;

        var stretch = 1 << (quietInARow - QuietRunsBeforeStretching + 1);
        return stretch > MaxQuietStretch ? MaxQuietStretch : stretch;
    }

    /// <summary>
    /// Writes a finished run and its job's new schedule, and never throws.
    ///
    /// This is called from a <c>finally</c>, so an exception here does not merely lose the write —
    /// it replaces whatever the run was doing and escapes the job entirely. That matters most in
    /// the case it is most likely: a scrape that failed because <c>SaveChangesAsync</c> was
    /// refused leaves the rejected change set tracked on this very context, EF Core having no
    /// reason to detach it, so saving again asks the database the same rejected question. The run
    /// would stay <c>Running</c> — reconciled only by a restart, half an hour later — and
    /// <c>NextRunAt</c> would stay where it was, making the job due again on the next minute-poll
    /// and putting this app in a tight retry loop against AO3.
    ///
    /// So the fallback writes the same two rows through a context that never saw the scrape.
    /// </summary>
    private async Task PersistCompletionAsync(AppDbContext db, ScrapeRun run, ScrapeJob job, CancellationToken ct)
    {
        Exception saveError;

        try
        {
            await db.SaveChangesAsync(ct);
            return;
        }
        catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
        {
            _logger.LogError(
                ex, "Could not record ScrapeRun {RunId} for ScrapeJob {JobId} on the run's own context",
                run.Id, job.Id);
            saveError = ex;
        }

        try
        {
            using var recovery = _scopeFactory.CreateScope();
            var recoveryDb = recovery.ServiceProvider.GetRequiredService<AppDbContext>();

            var freshRun = await recoveryDb.ScrapeRuns.FirstOrDefaultAsync(r => r.Id == run.Id, ct);
            var freshJob = await recoveryDb.ScrapeJobs.FirstOrDefaultAsync(j => j.Id == job.Id, ct);

            if (freshRun is null || freshJob is null)
            {
                _logger.LogError(
                    "ScrapeRun {RunId} or ScrapeJob {JobId} vanished while being closed out", run.Id, job.Id);
                return;
            }

            CopyCompletion(run, freshRun);

            // Failed however the scrape itself ended: a run whose results could not be written is
            // not a run that succeeded, and the save error is the part a reader needs.
            freshRun.Status = ScrapeRunStatus.Failed;
            freshRun.StopReason = ScrapeStopReason.Error;
            freshRun.ErrorMessage = run.ErrorMessage is null
                ? saveError.Message
                : $"{run.ErrorMessage} — and the run could not be recorded: {saveError.Message}";

            freshJob.LastRunAt = job.LastRunAt;
            freshJob.NextRunAt = job.NextRunAt;

            await recoveryDb.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
        {
            // Out of ways to record it. Swallowed rather than rethrown because this is still a
            // finally: the run is left Running for startup reconciliation to close out, which is
            // the outcome this whole method exists to make rare rather than routine.
            _logger.LogError(ex, "Could not close out ScrapeRun {RunId} at all", run.Id);
        }
    }

    /// <summary>Everything the finally had just written onto the run, onto a freshly loaded copy.</summary>
    private static void CopyCompletion(ScrapeRun from, ScrapeRun to)
    {
        to.PagesFetched = from.PagesFetched;
        to.RequestsMade = from.RequestsMade;
        to.WorksSeen = from.WorksSeen;
        to.WorksAdded = from.WorksAdded;
        to.WorksUpdated = from.WorksUpdated;
        to.ParseWarnings = from.ParseWarnings;
        to.FirstPageFetched = from.FirstPageFetched;
        to.LastPageFetched = from.LastPageFetched;
        to.HitRequestCap = from.HitRequestCap;
        to.HitTimeCap = from.HitTimeCap;
        to.CompletedAt = from.CompletedAt;
        to.HeartbeatAt = from.HeartbeatAt;
    }
}
