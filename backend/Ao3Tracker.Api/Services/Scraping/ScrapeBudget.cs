namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>Why a run stopped. Persisted to <c>ScrapeRun.StopReason</c>.</summary>
public static class ScrapeStopReason
{
    /// <summary>Reached results already ingested — the normal, healthy end of an incremental pass.</summary>
    public const string Watermark = "watermark";

    /// <summary>Walked off the end of the listing. The normal end of a completed backfill.</summary>
    public const string LastPage = "lastPage";

    /// <summary>Spent the per-run request budget. Expected for backfills; they resume next run.</summary>
    public const string Cap = "cap";

    /// <summary>Spent the per-run wall-clock budget.</summary>
    public const string TimeCap = "timeCap";

    /// <summary>
    /// Circuit breaker opened after too many consecutive failures — the archive answering nothing
    /// usable, MaxConsecutiveFailures times in a row, spaced by the shared 5-8s gate.
    ///
    /// A failed run, unlike the other two budget stops: <see cref="Cap"/> and <see cref="TimeCap"/>
    /// are a run spending an allowance it was given. It is also counted by
    /// <c>Ao3ShipIndexScraper.HeldAfterPageAsync</c>'s streak, because the walk re-asks a page that
    /// fails at the transport level and so reaches a page it cannot get past by this reason rather
    /// than by <see cref="Error"/>.
    /// </summary>
    public const string Breaker = "breaker";

    /// <summary>
    /// The walk stopped short of a page that run after run has refused to answer, without asking
    /// for it. Not a page that failed this run — a page this run deliberately did not spend a
    /// request on, because the last several runs each spent one on it and got nothing back.
    ///
    /// That page can be page 1, in which case the run made no request at all: a tag the archive
    /// 404s has no page any run of it ever read, and the hold is on the whole walk rather than on
    /// its depth. See <see cref="NotFound"/> for what has to be true before the walk reads it that
    /// way.
    ///
    /// Recorded as a failed run, because it is one: the pass could not get through the listing. It
    /// is distinct from <see cref="Error"/> so that the walk can tell its own held runs apart from
    /// the failures that caused them, which is what times the periodic re-probe — see
    /// <c>Ao3ShipIndexScraper.HeldAfterPageAsync</c>. Like <see cref="Error"/>, and for the same
    /// reason, it may never move the watermark.
    /// </summary>
    public const string Held = "held";

    /// <summary>
    /// The run made no request, because the tag it is for is one AO3 has denied. Distinct from
    /// <see cref="LastPage"/>, which it used to be recorded as: that says the walk read to the end
    /// of the listing, and this run never asked for a page of it. Nothing here can fix the ship, and
    /// no scrape ever will: only an admin sending the tag back for checking
    /// (<c>POST /api/admin/ships/{id}/verification/recheck</c>) moves a settled verification back to
    /// Pending. So it is not <see cref="Held"/> either, which names a page the walk means to come
    /// back to.
    /// </summary>
    public const string Denied = "denied";

    /// <summary>
    /// The archive answered 404 for the first page this run asked for, having read none — which is
    /// the archive saying definitively that the page is not there, rather than failing to answer.
    ///
    /// Distinct from <see cref="Error"/>, which it used to be recorded as, because a run that read
    /// nothing names no page and the two readings of that are opposite. A transport failure, a
    /// refused status or the breaker opening on page 1 is the archive being unwell, and a walk that
    /// concluded anything from those would stop scraping the whole instance for the length of an
    /// outage. A 404 is not that. It is the only stop reason
    /// <c>Ao3ShipIndexScraper.HeldAfterPageAsync</c> will build a streak on with no page behind it,
    /// and so the one that lets a tag AO3 no longer serves stop being asked for every tick.
    ///
    /// A failure like <see cref="Error"/>, and for the same reason: the pass got nothing, and the
    /// run history is the only place a headless worker reports itself.
    /// </summary>
    public const string NotFound = "notFound";

    public const string Error = "error";

    /// <summary>
    /// The process died mid-run, and startup reconciliation closed the row. Not a stop the walk can
    /// choose — <c>ScrapeWorker.ReconcileInterruptedRunsAsync</c> is the only writer — but a value
    /// that appears in <c>ScrapeRun.StopReason</c> like any other, and therefore one that anything
    /// reading that column as a closed vocabulary has to know about. It was a bare string literal
    /// until <see cref="Held"/> gave the column a reader.
    /// </summary>
    public const string Interrupted = "interrupted";

    /// <summary>
    /// Whether a run that stopped for this reason failed, and so must be recorded
    /// <c>ScrapeRunStatus.Failed</c> rather than <c>Succeeded</c>.
    ///
    /// Here rather than inline at <c>ScrapeWorker</c>'s one call site because the run history is
    /// read back as well as written: <c>Ao3ShipIndexScraper.HeldAfterPageAsync</c> reads these rows
    /// to decide what it may ask for, and the test fixtures that arrange a run history have to
    /// agree with the worker about which stops are failures or they arrange histories no instance
    /// can produce. See the call site for why each value is on this list.
    /// </summary>
    public static bool RecordsAsFailure(string? stopReason) =>
        stopReason is Error or Held or Denied or Breaker or NotFound;
}

/// <summary>
/// Per-run spending limits and the circuit breaker, kept as one small accounting object so the
/// stopping rules are testable without HTTP, a database, or a real scrape.
///
/// Deliberately not shared between runs and deliberately separate from
/// <see cref="RateLimitedAo3HttpClient"/>: the client enforces how *fast* requests may go out
/// globally, this enforces how *many* one run may make. A backfill exhausting its budget is a
/// normal outcome, not a failure — it saves its cursor and resumes on the next tick.
/// </summary>
public sealed class ScrapeBudget
{
    private readonly int _maxRequests;
    private readonly int _maxConsecutiveFailures;
    private readonly TimeSpan _maxDuration;
    private readonly DateTime _startedAt;
    private readonly TimeProvider _time;

    public ScrapeBudget(Ao3HttpClientOptions options, TimeProvider? timeProvider = null)
        : this(options.MaxRequestsPerRun, options.MaxConsecutiveFailures, options.MaxRunDuration, timeProvider)
    {
    }

    public ScrapeBudget(
        int maxRequests,
        int maxConsecutiveFailures,
        TimeSpan maxDuration,
        TimeProvider? timeProvider = null)
    {
        _maxRequests = maxRequests;
        _maxConsecutiveFailures = maxConsecutiveFailures;
        _maxDuration = maxDuration;
        _time = timeProvider ?? TimeProvider.System;
        _startedAt = _time.GetUtcNow().UtcDateTime;
    }

    /// <summary>Requests that actually reached AO3. Cache hits are excluded — they cost AO3 nothing.</summary>
    public int RequestsMade { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public bool HitRequestCap { get; private set; }
    public bool HitTimeCap { get; private set; }
    public bool BreakerOpen { get; private set; }

    /// <summary>
    /// Whether another request may be made, and if not, why. Call before each fetch; the returned
    /// reason is what belongs in <c>ScrapeRun.StopReason</c>.
    /// </summary>
    public bool CanContinue(out string? stopReason)
    {
        if (BreakerOpen)
        {
            stopReason = ScrapeStopReason.Breaker;
            return false;
        }

        if (RequestsMade >= _maxRequests)
        {
            HitRequestCap = true;
            stopReason = ScrapeStopReason.Cap;
            return false;
        }

        if (_time.GetUtcNow().UtcDateTime - _startedAt >= _maxDuration)
        {
            HitTimeCap = true;
            stopReason = ScrapeStopReason.TimeCap;
            return false;
        }

        stopReason = null;
        return true;
    }

    /// <summary>
    /// Records a request that reached AO3 and succeeded. Resets the breaker: the threshold counts
    /// *consecutive* failures, so an archive that is flaky but functional does not trip it.
    /// </summary>
    public void RecordSuccess()
    {
        RequestsMade++;
        ConsecutiveFailures = 0;
    }

    /// <summary>
    /// Records a request that reached AO3 and failed. Still counts against the request cap — a
    /// failed request costs AO3 real work, so it must not be free.
    /// </summary>
    public void RecordFailure()
    {
        RequestsMade++;
        ConsecutiveFailures++;

        if (ConsecutiveFailures >= _maxConsecutiveFailures) BreakerOpen = true;
    }

    /// <summary>
    /// Records a response served from the local cache. Neither the cap nor the breaker moves:
    /// nothing left this process.
    /// </summary>
    public void RecordCacheHit()
    {
    }
}
