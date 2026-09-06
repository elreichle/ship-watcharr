namespace Ao3Tracker.Api.Models;

/// <summary>
/// One user's own data about one work: reading status, rating, note, favorite. PER-USER.
///
/// Kept strictly separate from <see cref="Work"/> so re-scrapes can overwrite scraped metadata
/// freely without ever touching what the user wrote.
/// </summary>
public class UserWorkState
{
    public int Id { get; set; }

    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    public long WorkId { get; set; }
    public Work Work { get; set; } = null!;

    public ReadingStatus Status { get; set; } = ReadingStatus.None;

    /// <summary>
    /// Rating in half-stars, 1–10 (so 7 means 3.5 stars). Integer rather than decimal because the
    /// UI only ever offers half-steps, and an int compares and indexes exactly. Null means unrated,
    /// which is distinct from a deliberate lowest rating. A check constraint enforces the range.
    /// </summary>
    public int? Rating { get; set; }

    public string? Note { get; set; }

    /// <summary>
    /// When this reader marked the work a favorite, or null while it is not one. A timestamp
    /// rather than a flag because it costs nothing more, and a favorites list is one that will
    /// want ordering by "when I picked it" sooner or later. Set once, when the mark goes on, and
    /// left alone by every later save that keeps the mark: re-saving a rating does not make an old
    /// favorite a new one.
    /// </summary>
    public DateTime? FavoritedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
