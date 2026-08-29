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

    /// <summary>
    /// Where an admin's restart resumes the backfill under way, or null before anything has been
    /// read or asked for. Written by two things and nothing else: every page a run reads raises it,
    /// and a page an admin names on a restart replaces it outright.
    ///
    /// Not a duplicate of <see cref="BackfillNextPage"/>: that is where the *last run landed*, and
    /// Ao3ShipIndexScraper.JumpCursorBackFrom halves it once per stalled run, so a ship written off
    /// after genuinely reading page 39 is stored sitting on page 1 (39 → 20 → 10 → 5 → 2 → 1).
    /// Nothing lowers this one except an admin saying so, which is what makes it the record of what
    /// AO3 has already served: re-walking those pages at the shared 5-8 second gate would be this
    /// instance charging the archive again for work it has already done.
    ///
    /// An admin's page replaces it rather than only raising it because a restart is a statement
    /// about where the walk now stands, in both directions. A shallower page says the listing is
    /// not the length the walk believed; a deeper one says to skip ahead of anything a run reached.
    /// Either way it must survive a walk that stalls again without reading a page, or the restart
    /// after that one silently discards the admin's choice.
    ///
    /// A column rather than a MAX over this ship's <c>ScrapeRun.LastPageFetched</c> because the run
    /// history is prunable and this must outlive it, and because a MAX has nowhere to put the half
    /// of this that no run ever fetched.
    ///
    /// Belongs to one backfill, so it is cleared when a backfill begins. A restart is a
    /// continuation of the same backfill — it leaves <see cref="BackfillStartedAt"/> alone — so it
    /// keeps this, which is what stops a second write-off losing the number again.
    /// </summary>
    public int? BackfillResumePage { get; set; }

    /// <summary>Oldest updated-time seen so far. A non-monotonic reading means pages shifted underneath us.</summary>
    public DateTime? BackfillMinUpdatedAtSeen { get; set; }

    /// <summary>
    /// Reserved for the date-window cursor strategy — the fallback if deep pagination proves
    /// unreliable on very large tags.
    /// </summary>
    public DateTime? BackfillBeforeUpdatedAt { get; set; }

    /// <summary>
    /// Consecutive backfill runs that got nowhere and were told something by AO3 while getting
    /// there — a first request landing on a cursor the listing would not answer, or any page AO3
    /// served a body for that came back unreadable. The second half is wider than the stale cursor
    /// this was first written for, and deliberately so: a backfill whose cursor has reached page 1
    /// can no longer retreat (Ao3ShipIndexScraper.CursorMayBeStale requires `page > 1`), so on the
    /// narrow rule such a ship would re-request one unanswerable page a run for ever with the
    /// backfill never reaching <see cref="ShipBackfillState.Failed"/>. See
    /// Ao3ShipIndexScraper.RecordBackfillProgress. Cleared by a run that gets *further*
    /// through the listing than it started, and by a backfill beginning or being restarted;
    /// reading a page is not enough on its own, because a run that retreated to page 1 and found
    /// that unreadable too has read a page and learned nothing.
    /// It is the bound on how long a ship may go on asking a question the archive keeps refusing:
    /// at the scraper's limit the backfill is <see cref="ShipBackfillState.Failed"/> and the ship
    /// falls back to its incremental pass, rather than spending two requests a run forever. That is
    /// not a one-way door — an admin can restart a written-off backfill, which is the only thing
    /// that moves a ship out of <see cref="ShipBackfillState.Failed"/>.
    /// </summary>
    public int BackfillStalledRuns { get; set; }

    public DateTime? BackfillStartedAt { get; set; }
    public DateTime? BackfillCompletedAt { get; set; }

    // --- Totals and full sweeps ---

    public int? LastKnownTotalWorks { get; set; }
    public DateTime? LastKnownTotalWorksAt { get; set; }

    /// <summary>
    /// Whether the request that produced the total was logged in. Restricted works are invisible to
    /// an anonymous scrape, so a sweep must not conclude works have disappeared when it simply ran
    /// at a lower auth level than the run that first saw them.
    ///
    /// Belongs to <see cref="LastKnownTotalWorks"/>, not to the ship's history and not to the run:
    /// whichever *request* reads that number carries this beside it, so a later anonymous read of a
    /// fresh total clears the flag rather than leaving an earlier session's claim over a number it
    /// never saw. A single run reads pages at both auth levels — a cached page, an unparseable one,
    /// a session that dies partway — so "the run" is not fine-grained enough to be the answer.
    /// </summary>
    public bool LastKnownTotalWasAuthenticated { get; set; }

    /// <summary>
    /// When the sweep currently under way began walking page 1. Also the cutoff it concludes
    /// against: a <see cref="ShipWork"/> whose <see cref="ShipWork.LastSeenAt"/> is older than this
    /// was not seen by the sweep, and a sweep that reached the end of the listing is entitled to
    /// say so. Kept across the several runs one sweep takes, so it dates the sweep and not the run.
    ///
    /// It is also what spaces sweeps out: the next one is due an interval after this, whether the
    /// last one finished or was abandoned — see <c>ScrapeWorker.FullSweepIsDue</c>. Measuring from
    /// the start rather than from <see cref="LastFullSweepCompletedAt"/> is what stops a sweep that
    /// gets nowhere from being retried on every tick.
    /// </summary>
    public DateTime? LastFullSweepStartedAt { get; set; }

    public DateTime? LastFullSweepCompletedAt { get; set; }

    /// <summary>
    /// Next listing page the sweep under way will walk, or null when no sweep is in flight.
    ///
    /// Separate from <see cref="BackfillNextPage"/> rather than shared with it because the two
    /// walks are not the same walk and can both be owed: a sweep is a periodic re-walk of a ship
    /// whose backfill has already finished or been written off, and a Failed backfill keeps its
    /// cursor as the record of where it gave up.
    ///
    /// Non-null is also what says a sweep is under way at all, which is why a sweep that reaches
    /// the end of the listing and one that is abandoned both clear it.
    /// </summary>
    public int? FullSweepNextPage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<WatchedShip> Watchers { get; set; } = new List<WatchedShip>();
    public ICollection<ShipWork> Works { get; set; } = new List<ShipWork>();
}
