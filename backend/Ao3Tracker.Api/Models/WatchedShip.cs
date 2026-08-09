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

    public bool NotificationsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
