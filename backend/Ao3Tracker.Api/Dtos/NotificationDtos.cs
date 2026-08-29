namespace Ao3Tracker.Api.Dtos;

/// <summary>
/// One "a ship you follow gained a work" row, with enough of the ship and the work to render the
/// sentence and link to it.
/// </summary>
/// <remarks>
/// The names are joined at read time rather than copied onto the row when it was written. A work
/// AO3 has since retitled should be named by its current title here, and a stored copy would go on
/// showing the old one forever with nothing to correct it.
/// </remarks>
/// <param name="ReadAt">Null while unread — the same fact <c>unread-count</c> counts.</param>
public record NotificationDto(
    int Id,
    int ShipId,
    string ShipName,
    long WorkId,
    string WorkTitle,
    DateTime CreatedAt,
    DateTime? ReadAt);

/// <summary>What the sidebar polls for. See <c>NotificationsController.GetUnreadCount</c>.</summary>
public record UnreadNotificationsDto(int Unread);

/// <summary>The notifications to mark read. The caller's own only; ids they do not own are ignored.</summary>
public record MarkNotificationsReadRequest(IReadOnlyList<int>? Ids);
