namespace Ao3Tracker.Api.Models;

public enum ScrapeRunStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,

    /// <summary>
    /// The process died mid-run. Backfills can run for hours, so a crash would otherwise leave a
    /// row claiming to be Running forever; startup reconciliation converts stale ones to this.
    /// </summary>
    Interrupted,
}

/// <summary>One execution attempt of a ScrapeJob, with status/timing/error history.</summary>
public class ScrapeRun
{
    public int Id { get; set; }

    public int ScrapeJobId { get; set; }
    public ScrapeJob ScrapeJob { get; set; } = null!;

    public ScrapeRunStatus Status { get; set; } = ScrapeRunStatus.Pending;

    public ScrapeRunMode Mode { get; set; } = ScrapeRunMode.Incremental;

    // DateTime (UTC), not DateTimeOffset: StartedAt is ordered by, and the SQLite
    // provider can't translate ordering/comparisons on DateTimeOffset — see README.
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    /// <summary>Bumped as the run progresses so a stalled run is distinguishable from a dead one.</summary>
    public DateTime? HeartbeatAt { get; set; }

    public string? ErrorMessage { get; set; }

    // --- Counters ---

    /// <summary>
    /// Listing pages successfully parsed. Tracked separately from <see cref="RequestsMade"/>
    /// because AO3 frequently returns transient Cloudflare errors, so a page can cost several
    /// requests — the two numbers are not interchangeable and the budget counts the latter.
    /// </summary>
    public int PagesFetched { get; set; }

    /// <summary>Requests that actually reached AO3. Cache hits don't count; they cost AO3 nothing.</summary>
    public int RequestsMade { get; set; }

    public int WorksSeen { get; set; }
    public int WorksAdded { get; set; }
    public int WorksUpdated { get; set; }

    /// <summary>Blurbs that failed to parse but stayed under the abort threshold.</summary>
    public int ParseWarnings { get; set; }

    public int? FirstPageFetched { get; set; }
    public int? LastPageFetched { get; set; }

    /// <summary>Stopped at the per-run request budget. Expected for backfills, not an error.</summary>
    public bool HitRequestCap { get; set; }

    /// <summary>Stopped at the wall-clock budget.</summary>
    public bool HitTimeCap { get; set; }

    /// <summary>
    /// One of <c>ScrapeStopReason</c>: "watermark" | "cap" | "timeCap" | "breaker" | "lastPage" |
    /// "held" | "denied" | "notFound" | "throttled" | "error" | "interrupted". Read
    /// as a closed vocabulary by <c>Ao3ShipIndexScraper.HeldAfterPageAsync</c>, so a new value
    /// belongs in that class rather than at its write site.
    /// </summary>
    public string? StopReason { get; set; }
}
