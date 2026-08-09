namespace Ao3Tracker.Api.Models;

/// <summary>
/// One user's subscription to a <see cref="Ship"/>. PER-USER.
///
/// Owns nothing but the subscription itself — no watermark, no cursor, no scraped data. Adding
/// or removing a watcher must never affect what has been scraped, and unwatching must never
/// discard data another user still wants.
/// </summary>
public class WatchedShip
{
    public int Id { get; set; }

    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    public int ShipId { get; set; }
    public Ship Ship { get; set; } = null!;

    /// <summary>Optional per-user label, for when the canonical AO3 tag is unwieldy.</summary>
    public string? DisplayNameOverride { get; set; }

    /// <summary>
    /// What this user actually typed, kept only when verification found AO3 treats it as a synonym
    /// and moved them to the canonical tag. Null in the ordinary case where the two agree.
    ///
    /// Per-user rather than on the <see cref="Ship"/> because the ship is shared: two people can
    /// arrive at one canonical tag by different synonyms, and each should be told what happened to
    /// *their* entry. Without it the tag someone typed silently becomes a different string, which
    /// reads as the app having ignored them.
    /// </summary>
    public string? RequestedTagName { get; set; }

    public bool NotificationsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
