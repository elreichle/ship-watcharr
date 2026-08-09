namespace Ao3Tracker.Api.Models;

/// <summary>
/// Records that a work was returned by a ship's tag listing. GLOBAL. Composite PK (ShipId, WorkId).
///
/// This exists rather than joining works to the ship's relationship <see cref="Tag"/> because of
/// AO3 tag synonyms: a work returned by the canonical tag may render a synonym in its own blurb,
/// so its WorkTag rows need not contain the canonical tag at all. Filtering on the tag would
/// silently drop those works. "Appeared in this ship's index" is the fact we actually rely on, so
/// it is the fact we store.
/// </summary>
public class ShipWork
{
    public int ShipId { get; set; }
    public Ship Ship { get; set; } = null!;

    public long WorkId { get; set; }
    public Work Work { get; set; } = null!;

    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// When the work stopped appearing in this ship's listing — usually because the author removed
    /// the relationship tag, not because the work is gone (that is <see cref="Work.IsDeleted"/>).
    ///
    /// Only a *completed* full sweep may set this. Mid-backfill we simply haven't looked everywhere
    /// yet, so absence proves nothing. Cleared if the work reappears.
    /// </summary>
    public DateTime? MissingSinceAt { get; set; }
}
