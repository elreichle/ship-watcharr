using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Services.Downloads;

/// <summary>What stands behind one reader's download request, as far as the disk is concerned.</summary>
public abstract record StoredCopy
{
    /// <summary>No such request of this reader's. Someone else's and none at all answer alike.</summary>
    public sealed record NotFound : StoredCopy;

    /// <summary>The request has nothing behind it: still queued, or the fetch failed, with no earlier copy held.</summary>
    public sealed record NoFile : StoredCopy;

    /// <summary>The request says it has a file, and the file has left the disk under it.</summary>
    public sealed record Gone : StoredCopy;

    /// <summary>The row names a path outside the data directory. Logged; nothing is served.</summary>
    public sealed record Unsafe(string RelativePath) : StoredCopy;

    /// <summary>
    /// The file to serve.
    /// </summary>
    /// <param name="IsEarlierCopy">True where the bytes are the copy the reader was holding before the
    /// request was re-armed onto a newer version, rather than the file the request reports.</param>
    public sealed record Found(
        long WorkId,
        string WorkTitle,
        Ao3DownloadFormat Format,
        string AbsolutePath,
        bool IsEarlierCopy) : StoredCopy;
}

/// <summary>
/// Finds the file behind a download request, by the one set of rules every endpoint that serves
/// bytes off a request follows.
/// </summary>
/// <remarks>
/// Two endpoints hand a reader the contents of a stored file — the download itself, and the in-app
/// reader that opens an EPUB — and the rules for which file, if any, that is are the same for
/// both: the caller's own row or nothing; the row's own file only while it is Complete, else the
/// earlier copy it is still holding; a path that has to be inside the data directory; and a file
/// that has to still be there. One resolver, so the two cannot drift apart on any of the four.
///
/// Answers are shapes rather than HTTP results because the two callers say different things
/// about the same shape — a missing file is 410 for both, but what is served on success is a
/// stream in one and a parsed book in the other.
/// </remarks>
public sealed class StoredCopyResolver
{
    private readonly AppDbContext _db;
    private readonly StoragePaths _paths;
    private readonly ILogger<StoredCopyResolver> _logger;

    public StoredCopyResolver(AppDbContext db, StoragePaths paths, ILogger<StoredCopyResolver> logger)
    {
        _db = db;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// The file behind the caller's request.
    /// </summary>
    /// <remarks>
    /// A request that is not Complete is still served where the reader is holding a copy from
    /// before it was re-armed (<see cref="Download.PreviousWorkDownloadFileId"/>): those bytes were
    /// theirs to read a moment ago and asking for a newer version is not a reason to take them
    /// away. That is a different column from the one the request reports, which is what keeps
    /// "a request never answers with bytes of a version it claims to have moved past" true — the
    /// file a non-Complete request <i>names</i> is still refused, and the caller labels what it
    /// offers as the earlier copy rather than as the fetch that has not happened.
    /// </remarks>
    public async Task<StoredCopy> ResolveAsync(int downloadId, string userId, CancellationToken ct)
    {
        var request = await _db.Downloads
            .Where(d => d.Id == downloadId && d.UserId == userId)
            .Select(d => new
            {
                d.WorkId,
                d.Format,
                d.Status,
                WorkTitle = d.Work.Title,
                RelativePath = d.File == null ? null : d.File.RelativePath,
                PreviousRelativePath = d.PreviousFile == null ? null : d.PreviousFile.RelativePath,
            })
            .FirstOrDefaultAsync(ct);

        if (request is null) return new StoredCopy.NotFound();

        // Complete is the only status whose own file is an answer; anything else falls back to the
        // copy the reader was already holding, and to nothing when there is none.
        var reportsItsOwnFile = request.Status == DownloadStatus.Complete && request.RelativePath is not null;
        var relativePath = reportsItsOwnFile ? request.RelativePath : request.PreviousRelativePath;

        if (relativePath is null) return new StoredCopy.NoFile();

        // Resolved through DownloadPaths rather than treated as a path: what is stored is relative
        // to the data directory precisely so the volume can be mounted somewhere else tomorrow.
        var dataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_paths.DataDirectory));
        var absolutePath = Path.GetFullPath(DownloadPaths.Absolute(dataDirectory, relativePath));

        // Nothing writes a RelativePath today but DownloadPaths.Relative, which builds it out of a
        // work id and an enum and can no more escape the data directory than it can misspell it. It
        // is checked anyway because this is the one place in the app where a value out of the
        // database becomes a file handed to whoever asked: a row saying "../../etc/passwd" — from a
        // migration, from a restored database, from a future writer with a different idea of what
        // belongs in that column — would otherwise be served in full to any signed-in reader.
        if (!absolutePath.StartsWith(dataDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            _logger.LogError(
                "Download {DownloadId} names {RelativePath}, which is not inside the data directory. "
                + "Nothing was served.", downloadId, relativePath);

            return new StoredCopy.Unsafe(relativePath);
        }

        if (!File.Exists(absolutePath))
        {
            // Gone only for a request that says it has this file: that row is wrong about the world
            // and re-asking is what repairs it. A queued or failed request whose earlier copy has
            // also gone is not wrong about anything — it has no file, which is what it already
            // says — so it answers as the request it is.
            return reportsItsOwnFile ? new StoredCopy.Gone() : new StoredCopy.NoFile();
        }

        return new StoredCopy.Found(
            request.WorkId,
            request.WorkTitle,
            request.Format,
            absolutePath,
            IsEarlierCopy: !reportsItsOwnFile);
    }
}
