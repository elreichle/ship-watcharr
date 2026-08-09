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

    /// <summary>Circuit breaker opened after too many consecutive failures.</summary>
    public const string Breaker = "breaker";

    public const string Error = "error";
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
