namespace Ao3Tracker.Api.Models;

/// <summary>
/// One reader being told that one ship they follow has gained one work. PER-USER.
///
/// In-app only — there is no SMTP and no push, by design. A row here is the whole of the
/// notification: the sidebar's unread count and the notifications list are both reads of this
/// table, and marking one read is a write to <see cref="ReadAt"/> and nothing else.
/// </summary>
/// <remarks>
/// It carries no message text and no kind. What happened is fixed — a followed ship gained a work
/// — so the row names the ship and the work and lets the reader render the sentence. A second kind
/// of notification would earn a discriminator then; inventing one now would be a column every
/// query has to filter on to say the only thing it can say.
/// </remarks>
/// <remarks>
/// Deliberately not uniquely indexed on (user, ship, work). Nothing can produce a duplicate — a
/// row is written only where a <see cref="ShipWork"/> link is *created*, which happens once per
/// pair — and a unique index here would be a constraint that fails the whole page's save rather
/// than the one row, on the same reasoning that keeps an over-long tag name from doing so. The
/// per-user cap bounds the table regardless of how the rows got there.
/// </remarks>
public class Notification
{
    /// <summary>
    /// The newest notifications one user keeps. Anything past this is deleted as the next one is
    /// produced, so the table is bounded at (users x this) however long an instance runs and
    /// however little anyone reads.
    ///
    /// Two hundred rather than a handful because deleting a notification a reader has not seen is
    /// the one thing this cap must almost never do, and rather than thousands because a list
    /// nobody has looked at in that long is a backlog, not a notification.
    /// </summary>
    public const int MaxPerUser = 200;

    public int Id { get; set; }

    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    /// <summary>The followed ship the work turned up under — not the work's other ships.</summary>
    public int ShipId { get; set; }
    public Ship Ship { get; set; } = null!;

    public long WorkId { get; set; }
    public Work Work { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    /// <summary>When this reader marked it read. Null is unread, and is what the count counts.</summary>
    public DateTime? ReadAt { get; set; }
}
