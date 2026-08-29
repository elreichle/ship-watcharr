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

    /// <summary>
    /// The copy the reader already has, while this request is out looking for a newer one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="WorkDownloadFileId"/> because that one is the request's own answer
    /// and may never name bytes of a version the work has moved past — a row saying Complete beside
    /// an older file is worse than one saying Pending. This says something else, which that column
    /// cannot: the reader is already holding these bytes, and a re-fetch that fails must not be
    /// what takes them away. Set when a request is re-armed off a copy it was reporting, and
    /// cleared the moment a replacement is on disk, since the copy it names is then superseded.
    /// </remarks>
    public int? PreviousWorkDownloadFileId { get; set; }
    public WorkDownloadFile? PreviousFile { get; set; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
    public string? ErrorMessage { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
