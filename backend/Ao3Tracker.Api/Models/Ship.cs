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

    public DateTime? BackfillStartedAt { get; set; }
    public DateTime? BackfillCompletedAt { get; set; }

    // --- Totals and full sweeps ---

    public int? LastKnownTotalWorks { get; set; }
    public DateTime? LastKnownTotalWorksAt { get; set; }

    /// <summary>
    /// Whether the run that produced the total was logged in. Restricted works are invisible to an
    /// anonymous scrape, so a sweep must not conclude works have disappeared when it simply ran at
    /// a lower auth level than the run that first saw them.
    /// </summary>
    public bool LastKnownTotalWasAuthenticated { get; set; }

    public DateTime? LastFullSweepStartedAt { get; set; }
    public DateTime? LastFullSweepCompletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<WatchedShip> Watchers { get; set; } = new List<WatchedShip>();
    public ICollection<ShipWork> Works { get; set; } = new List<ShipWork>();
}
