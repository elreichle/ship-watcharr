namespace Ao3Tracker.Api.Models;

/// <summary>
/// A relationship tag being tracked. GLOBAL, shared, and deliberately separate from
/// <see cref="WatchedShip"/>.
///
/// All scrape state lives here rather than on the per-user subscription. If the watermark and
/// backfill cursor were per-user, two users watching the same ship would each drive their own
/// multi-hour backfill of identical data — exactly the duplicate fetching the whole design is
/// meant to prevent. Users subscribe; the ship is scraped once.
/// </summary>
public class Ship
{
    public int Id { get; set; }

    /// <summary>The exact AO3 relationship tag, e.g. "Clarke Griffin/Lexa".</summary>
    public string CanonicalTagName { get; set; } = null!;

    /// <summary>Uppercase invariant form. See <see cref="Tag.NameNormalized"/> for why this exists.</summary>
    public string CanonicalTagNameNormalized { get; set; } = null!;

    /// <summary>
    /// The tag as it appears in a URL path, with AO3's own escaping applied
    /// (<c>/</c> becomes <c>*s*</c>, <c>&amp;</c> becomes <c>*a*</c>, and so on).
    /// </summary>
    public string TagUrlSegment { get; set; } = null!;

    /// <summary>
    /// AO3's numeric tag id, harvested from the tag page's RSS link. Once known, requests can use
    /// <c>/tags/{id}/works</c>, which survives the tag being renamed.
    /// </summary>
    public long? Ao3TagId { get; set; }

    /// <summary>The relationship <see cref="Tag"/> row, once resolved.</summary>
    public int? TagId { get; set; }
    public Tag? Tag { get; set; }

    // --- Verification against AO3 ---

    /// <summary>
    /// Whether AO3 has confirmed this tag exists. Follows are accepted on trust and checked
    /// afterwards; see <see cref="ShipVerificationState"/>.
    /// </summary>
    public ShipVerificationState VerificationState { get; set; } = ShipVerificationState.Pending;

    /// <summary>When the last check ran, whatever its outcome. Null until one has.</summary>
    public DateTime? VerificationCheckedAt { get; set; }

    /// <summary>Why the last check did not settle the question. Cleared once one does.</summary>
    public string? VerificationError { get; set; }

    /// <summary>
    /// Consecutive inconclusive checks. Drives the retry backoff, and is reset by any outcome that
    /// actually answers the question.
    /// </summary>
    public int VerificationAttempts { get; set; }

    /// <summary>Null means "due now". Set to a backed-off time after an inconclusive check.</summary>
    public DateTime? NextVerificationAttemptAt { get; set; }

    // --- Incremental sync ---

    /// <summary>
    /// Newest work-updated time fully ingested by a successful run. Fed back to AO3 as a
    /// server-side date filter, which is what keeps routine runs to roughly one request.
    ///
    /// Advanced only on a fully successful run: a partial run that moved this forward would
    /// permanently skip everything it hadn't reached.
    /// </summary>
    public DateTime? IncrementalWatermarkUtc { get; set; }

    public DateTime? LastIncrementalRunAt { get; set; }

    // --- Backfill (resumable, and independent of the watermark above) ---

    public ShipBackfillState BackfillState { get; set; } = ShipBackfillState.NotStarted;

    /// <summary>Next listing page to walk, 1-based. Survives across runs so a capped run resumes.</summary>
    public int? BackfillNextPage { get; set; }

    /// <summary>Oldest updated-time seen so far. A non-monotonic reading means pages shifted underneath us.</summary>
    public DateTime? BackfillMinUpdatedAtSeen { get; set; }

    /// <summary>
    /// Reserved for the date-window cursor strategy — the fallback if deep pagination proves
    /// unreliable on very large tags.
    /// </summary>
    public DateTime? BackfillBeforeUpdatedAt { get; set; }

    /// <summary>
    /// Consecutive backfill runs whose first request landed on a cursor the listing could not
    /// answer — see Ao3ShipIndexScraper's stale-cursor rule. Reset by any run that reads a page.
    /// It is the bound on how long a ship may go on asking a question the archive keeps refusing:
    /// at the scraper's limit the backfill is <see cref="ShipBackfillState.Failed"/> and the ship
    /// falls back to its incremental pass, rather than spending two requests a run forever.
    /// </summary>
    public int BackfillStalledRuns { get; set; }

    public DateTime? BackfillStartedAt { get; set; }
    public DateTime? BackfillCompletedAt { get; set; }

    // --- Totals and full sweeps ---

    public int? LastKnownTotalWorks { get; set; }
    public DateTime? LastKnownTotalWorksAt { get; set; }

    /// <summary>
    /// Whether the run that produced the total was logged in. Restricted works are invisible to an
    /// anonymous scrape, so a sweep must not conclude works have disappeared when it simply ran at
    /// a lower auth level than the run that first saw them.
    ///
    /// Belongs to <see cref="LastKnownTotalWorks"/>, not to the ship's history: whichever run
    /// writes that number writes this beside it, so a later anonymous run reading a fresh total
    /// clears the flag rather than leaving an earlier session's claim over a number it never saw.
    /// </summary>
    public bool LastKnownTotalWasAuthenticated { get; set; }

    public DateTime? LastFullSweepStartedAt { get; set; }
    public DateTime? LastFullSweepCompletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<WatchedShip> Watchers { get; set; } = new List<WatchedShip>();
    public ICollection<ShipWork> Works { get; set; } = new List<ShipWork>();
}
