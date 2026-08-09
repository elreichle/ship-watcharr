namespace Ao3Tracker.Api.Models;

/// <summary>
/// One user's request for a downloadable copy of a work. PER-USER.
///
/// Points at a shared <see cref="WorkDownloadFile"/> rather than owning bytes. Deleting this row
/// removes the user's copy from their library but leaves the file for other users; a file is only
/// removable once nothing references it.
/// </summary>
public class Download
{
    public int Id { get; set; }

    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    public long WorkId { get; set; }
    public Work Work { get; set; } = null!;

    public Ao3DownloadFormat Format { get; set; }

    public int? WorkDownloadFileId { get; set; }
    public WorkDownloadFile? File { get; set; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
    public string? ErrorMessage { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
