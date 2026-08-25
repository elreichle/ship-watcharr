using Ao3Tracker.Api.Models;

namespace Ao3Tracker.Api.Services.Downloads;

/// <summary>
/// Where a downloaded file lives, as a path relative to the data directory.
///
/// Relative and never absolute, because <see cref="WorkDownloadFile.RelativePath"/> is written to
/// the database and the data directory is a Docker volume: an absolute path breaks the moment that
/// volume is mounted somewhere else, and a database restored onto another machine would name files
/// under a directory that never existed there.
/// </summary>
public static class DownloadPaths
{
    /// <summary>The one subdirectory of the data directory this feature writes under.</summary>
    public const string Root = "downloads";

    /// <summary>
    /// Where a part-written fetch lives until it is whole. Kept beside the files rather than in the
    /// system temp directory so the move into place is a rename within one filesystem, which is
    /// atomic — a move across devices is a copy, and a copy can be interrupted half way.
    /// </summary>
    public const string PartialsRoot = Root + "/partial";

    /// <summary>
    /// The path for one work's file in one format at one version. Deterministic, so the same
    /// (work, format, version) always names the same file — the identity the shared-file row is
    /// keyed by, spelled out on disk.
    /// </summary>
    /// <param name="workUpdatedAt">
    /// The work's version identity, in ticks. Not a formatted date: two revisions of a busy work
    /// can share a second, and a filename that collapsed them would have one version's bytes served
    /// as the other's.
    /// </param>
    public static string Relative(long workId, Ao3DownloadFormat format, DateTime workUpdatedAt) =>
        $"{Root}/{workId}/{workId}-{workUpdatedAt.Ticks}.{Extension(format)}";

    /// <summary>Resolves a stored relative path against wherever the data directory is today.</summary>
    public static string Absolute(string dataDirectory, string relativePath) =>
        Path.Combine(dataDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The extension AO3 addresses a format with, which is also what a reader's e-reader expects
    /// the file to be called.
    /// </summary>
    public static string Extension(Ao3DownloadFormat format) => format switch
    {
        Ao3DownloadFormat.Epub => "epub",
        Ao3DownloadFormat.Mobi => "mobi",
        Ao3DownloadFormat.Pdf => "pdf",
        Ao3DownloadFormat.Html => "html",
        Ao3DownloadFormat.Azw3 => "azw3",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not a format this library fetches."),
    };
}
