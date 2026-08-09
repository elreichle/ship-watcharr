namespace Ao3Tracker.Api.Models;

/// <summary>
/// A downloaded file on disk. GLOBAL, keyed by (work, format, work version).
///
/// Separate from <see cref="Download"/> so that two users wanting the same format of the same
/// unchanged work share one file and one fetch. Folding the path into the per-user row would mean
/// downloading the identical bytes from AO3 once per user.
/// </summary>
public class WorkDownloadFile
{
    public int Id { get; set; }

    public long WorkId { get; set; }
    public Work Work { get; set; } = null!;

    public Ao3DownloadFormat Format { get; set; }

    /// <summary>
    /// The work's <see cref="Work.UpdatedAt"/> at fetch time — the version identity of these bytes.
    /// A work whose updated time has moved past this needs re-downloading; one that hasn't does not.
    /// </summary>
    public DateTime WorkUpdatedAt { get; set; }

    /// <summary>
    /// Path relative to the configured data directory, never absolute: the Docker volume can be
    /// mounted at a different location, and absolute paths would break on every such move.
    /// </summary>
    public string RelativePath { get; set; } = null!;

    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }

    public DateTime FetchedAt { get; set; }
}
