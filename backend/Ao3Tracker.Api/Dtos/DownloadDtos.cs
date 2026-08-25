using Ao3Tracker.Api.Models;

namespace Ao3Tracker.Api.Dtos;

/// <summary>
/// One reader's request for a downloadable copy of a work. PER-USER — the bytes behind it are
/// shared, but this row is not.
/// </summary>
/// <remarks>
/// There is at most one of these per (reader, work, format), so a second request for something
/// already asked for reads back as the first one rather than as a new row. What distinguishes them
/// is <paramref name="Status"/>, not identity.
/// </remarks>
/// <param name="Format">An <see cref="Ao3DownloadFormat"/> name — "Epub", "Mobi", "Pdf", "Html",
/// "Azw3". Enum names rather than numbers, as everywhere else on this API's wire.</param>
/// <param name="Status">A <see cref="DownloadStatus"/> name — "Pending", "Downloading",
/// "Complete" or "Failed".</param>
/// <param name="SizeBytes">The stored file's size, or null while no file stands behind this
/// request — which is every status but Complete.</param>
/// <param name="ErrorMessage">Why the fetch failed, for a Failed request. Null otherwise.</param>
public record DownloadDto(
    int Id,
    long WorkId,
    string WorkTitle,
    string Format,
    string Status,
    long? SizeBytes,
    string? ErrorMessage,
    DateTime RequestedAt,
    DateTime? CompletedAt);

/// <summary>Which format of a work the caller wants a copy of.</summary>
public record RequestDownloadRequest(string? Format = null);
