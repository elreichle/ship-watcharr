using System.Net;
using System.Text.RegularExpressions;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>What one verification attempt concluded.</summary>
public enum ShipVerificationOutcome
{
    /// <summary>The tag exists and is already what AO3 calls it.</summary>
    Verified,

    /// <summary>A synonym, and no ship existed for the canonical tag — so this one was renamed to it.</summary>
    RenamedToCanonical,

    /// <summary>A synonym, and the canonical tag was already tracked — so the two were merged.</summary>
    MergedIntoCanonical,

    /// <summary>AO3 has no such tag.</summary>
    NotFound,

    /// <summary>The check failed in a way that says nothing about the tag. Will be retried.</summary>
    Inconclusive,
}

/// <param name="ShipId">
/// The ship the watcher ended up on, which is not always the one that went in — a merge deletes
/// the row it was called with.
/// </param>
public sealed record ShipVerificationResult(ShipVerificationOutcome Outcome, int ShipId, string? Message);

public interface IShipVerifier
{
    Task<ShipVerificationResult> VerifyAsync(int shipId, CancellationToken ct = default);
}

/// <summary>
/// Confirms a followed tag actually exists on AO3, and reconciles synonyms with canonical tags.
///
/// This runs after the fact rather than during <c>POST /api/ships</c> on purpose. Every outbound
/// request queues behind one global 5–8s gate shared with every running scrape, so a verifying
/// request cannot be given a predictable latency — blocking the form on it would mean an
/// indeterminate wait, and would also make following a tag impossible whenever AO3 is unreachable.
/// Accepting on trust and correcting shortly after is the better trade.
/// </summary>
public sealed class Ao3ShipVerifier : IShipVerifier
{
    /// <summary>
    /// Ceiling on the retry backoff. There is deliberately no attempt limit to go with it: an
    /// archive being unreachable is not evidence about a tag, however long it lasts, so a check
    /// that cannot reach AO3 must keep waiting rather than eventually declare the tag missing.
    /// </summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(6);

    /// <summary>
    /// AO3's numeric tag id, from the feed link every tag index carries. Best-effort and narrow on
    /// purpose: it is a bonus (an id addresses a tag in a way that survives renaming), never a
    /// condition of verification, so failing to find it must not fail the check. The real parser
    /// arrives with the scraper.
    /// </summary>
    private static readonly Regex TagIdInFeedLink =
        new(@"/tags/(?<id>\d+)/feed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly AppDbContext _db;
    private readonly IRateLimitedHttpClient _http;
    private readonly Ao3HttpClientOptions _options;
    private readonly ILogger<Ao3ShipVerifier> _logger;

    public Ao3ShipVerifier(
        AppDbContext db,
        IRateLimitedHttpClient http,
        IOptions<Ao3HttpClientOptions> options,
        ILogger<Ao3ShipVerifier> logger)
    {
        _db = db;
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ShipVerificationResult> VerifyAsync(int shipId, CancellationToken ct = default)
    {
        var ship = await _db.Ships.FirstOrDefaultAsync(s => s.Id == shipId, ct);
        if (ship is null)
            return new(ShipVerificationOutcome.Inconclusive, shipId, "The ship no longer exists.");

        if (ship.VerificationState != ShipVerificationState.Pending)
            return new(ShipVerificationOutcome.Verified, shipId, "Already settled.");

        var url = $"{_options.BaseUrl.TrimEnd('/')}/tags/{ship.TagUrlSegment}/works";

        ScrapeHttpResponse response;
        try
        {
            response = await _http.GetAsync(url, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Covers the no-operator-contact case too: the client throws rather than sending a
            // request it cannot identify, and an instance that is not allowed to talk to AO3 has
            // learned nothing about the tag.
            return await InconclusiveAsync(ship, ex.Message, ct);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            return await NotFoundAsync(ship, ct);

        if (response.StatusCode != HttpStatusCode.OK)
            return await InconclusiveAsync(ship, $"AO3 returned {(int)response.StatusCode}.", ct);

        // A redirect away from the tag index — to a login page, an error page, anything — means the
        // response describes something other than this tag. Reading a canonical name out of it
        // would rename the user's ship to whatever that page happened to be.
        string canonicalSegment;
        if (response.FinalUrl is null)
        {
            canonicalSegment = ship.TagUrlSegment;
        }
        else if (Ao3TagUrl.TryGetTagSegment(response.FinalUrl) is { } redirected)
        {
            canonicalSegment = redirected;
        }
        else
        {
            return await InconclusiveAsync(
                ship, $"AO3 redirected the tag index to {response.FinalUrl}, which is not a tag index.", ct);
        }

        var tagId = TryHarvestTagId(response.Content);
        var canonicalName = Ao3TagUrl.FromUrlSegment(canonicalSegment);

        return canonicalName.ToUpperInvariant() == ship.CanonicalTagNameNormalized
            ? await VerifiedAsync(ship, tagId, ct)
            : await ResolveSynonymAsync(ship, canonicalName, tagId, ct);
    }

    // ---- outcomes ----------------------------------------------------------------------------

    private async Task<ShipVerificationResult> VerifiedAsync(Ship ship, long? tagId, CancellationToken ct)
    {
        MarkVerified(ship, tagId);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Verified ship {ShipId} ({Tag}) against AO3", ship.Id, ship.CanonicalTagName);
        return new(ShipVerificationOutcome.Verified, ship.Id, null);
    }

    private async Task<ShipVerificationResult> NotFoundAsync(Ship ship, CancellationToken ct)
    {
        ship.VerificationState = ShipVerificationState.NotFoundOnAo3;
        ship.VerificationCheckedAt = DateTime.UtcNow;
        ship.VerificationAttempts = 0;
        ship.NextVerificationAttemptAt = null;
        ship.VerificationError = null;

        // Nothing to scrape, so stop asking. Left in place rather than deleted: the subscription is
        // what tells the user their tag was wrong, and deleting it would just make it vanish.
        var jobs = await _db.ScrapeJobs.Where(j => j.ShipId == ship.Id && j.IsEnabled).ToListAsync(ct);
        foreach (var job in jobs) job.IsEnabled = false;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "AO3 has no tag {Tag}; ship {ShipId} marked not found and its schedule disabled",
            ship.CanonicalTagName, ship.Id);

        return new(ShipVerificationOutcome.NotFound, ship.Id, $"AO3 has no tag “{ship.CanonicalTagName}”.");
    }

    private async Task<ShipVerificationResult> InconclusiveAsync(Ship ship, string error, CancellationToken ct)
    {
        ship.VerificationAttempts++;
        ship.VerificationCheckedAt = DateTime.UtcNow;
        ship.NextVerificationAttemptAt = DateTime.UtcNow + RetryDelay(ship.VerificationAttempts);

        // Truncated to the column: the message can be an exception's, which has no length bound,
        // and a save that throws here would lose the backoff along with the error.
        ship.VerificationError = error.Length > 500 ? error[..500] : error;

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "Could not verify ship {ShipId} ({Tag}), attempt {Attempt}, retrying after {NextAttempt}: {Error}",
            ship.Id, ship.CanonicalTagName, ship.VerificationAttempts, ship.NextVerificationAttemptAt, error);

        return new(ShipVerificationOutcome.Inconclusive, ship.Id, error);
    }

    /// <summary>Doubles per attempt, capped. Attempt 1 waits two minutes, attempt 9 waits the cap.</summary>
    internal static TimeSpan RetryDelay(int attempts)
    {
        var minutes = Math.Pow(2, Math.Min(attempts, 30));
        return minutes >= MaxRetryDelay.TotalMinutes ? MaxRetryDelay : TimeSpan.FromMinutes(minutes);
    }

    private static void MarkVerified(Ship ship, long? tagId)
    {
        ship.VerificationState = ShipVerificationState.Verified;
        ship.VerificationCheckedAt = DateTime.UtcNow;
        ship.VerificationAttempts = 0;
        ship.NextVerificationAttemptAt = null;
        ship.VerificationError = null;

        // Only ever set, never cleared: a page that failed to yield an id does not disprove one we
        // already have.
        if (tagId is not null) ship.Ao3TagId = tagId;
    }

    private static long? TryHarvestTagId(string html) =>
        TagIdInFeedLink.Match(html) is { Success: true } match && long.TryParse(match.Groups["id"].Value, out var id)
            ? id
            : null;

    // ---- synonyms ------------------------------------------------------------------------------

    /// <summary>
    /// AO3 answered a different tag than the one asked for, meaning the followed tag is a synonym.
    ///
    /// Left alone, this is the duplicate-fetching the shared-ship design exists to prevent: two
    /// spellings of one pairing become two ships, two schedules, and two walks over identical
    /// works. So the synonym is folded into the canonical tag — renamed if nothing holds that name
    /// yet, merged into the existing ship if something does.
    /// </summary>
    private async Task<ShipVerificationResult> ResolveSynonymAsync(
        Ship ship, string canonicalName, long? tagId, CancellationToken ct)
    {
        var normalized = canonicalName.ToUpperInvariant();
        var synonym = ship.CanonicalTagName;

        var canonical = await _db.Ships
            .Include(s => s.Watchers)
            .FirstOrDefaultAsync(s => s.CanonicalTagNameNormalized == normalized, ct);

        if (canonical is null)
        {
            await RecordRequestedNameAsync(ship.Id, synonym, ct);

            ship.CanonicalTagName = canonicalName;
            ship.CanonicalTagNameNormalized = normalized;
            ship.TagUrlSegment = Ao3TagUrl.ToUrlSegment(canonicalName);
            MarkVerified(ship, tagId);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Ship {ShipId} followed as “{Synonym}” is AO3's synonym for “{Canonical}”; renamed",
                ship.Id, synonym, canonicalName);

            return new(ShipVerificationOutcome.RenamedToCanonical, ship.Id,
                $"“{synonym}” is AO3's synonym for “{canonicalName}”.");
        }

        await MergeAsync(ship, canonical, tagId, ct);

        _logger.LogInformation(
            "Ship {ShipId} followed as “{Synonym}” merged into existing ship {CanonicalId} (“{Canonical}”)",
            ship.Id, synonym, canonical.Id, canonicalName);

        return new(ShipVerificationOutcome.MergedIntoCanonical, canonical.Id,
            $"“{synonym}” is AO3's synonym for “{canonicalName}”.");
    }

    /// <summary>
    /// Remembers what each of a ship's watchers originally typed, so the UI can explain why the tag
    /// they entered is not the one now on screen. Only ever set once — a second rename should still
    /// show what the person actually typed, not an intermediate name they never saw.
    /// </summary>
    private async Task RecordRequestedNameAsync(int shipId, string requested, CancellationToken ct)
    {
        var watchers = await _db.WatchedShips
            .Where(w => w.ShipId == shipId && w.RequestedTagName == null)
            .ToListAsync(ct);

        foreach (var watcher in watchers) watcher.RequestedTagName = requested;
    }

    /// <summary>
    /// Folds <paramref name="synonym"/> into <paramref name="canonical"/> and deletes it.
    ///
    /// Deliberately two saves inside one transaction rather than a single save. Everything hanging
    /// off the synonym cascade-deletes with it at the database level, so if the DELETE were ordered
    /// before the UPDATEs that move those rows across, the cascade would take them out from under
    /// the updates and the save would fail on rows that had vanished. Moving first and deleting
    /// second makes that ordering explicit instead of trusting EF's; the transaction keeps the pair
    /// atomic, so a failure between them leaves no half-merged ship.
    /// </summary>
    private async Task MergeAsync(Ship synonym, Ship canonical, long? tagId, CancellationToken ct)
    {
        var synonymName = synonym.CanonicalTagName;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var alreadyWatching = canonical.Watchers.Select(w => w.UserId).ToHashSet(StringComparer.Ordinal);
        var watchers = await _db.WatchedShips.Where(w => w.ShipId == synonym.Id).ToListAsync(ct);

        foreach (var watcher in watchers)
        {
            // Someone who followed both spellings would otherwise end up with two subscriptions to
            // one ship, which the unique index on (UserId, ShipId) rejects outright.
            if (alreadyWatching.Contains(watcher.UserId))
            {
                _db.WatchedShips.Remove(watcher);
                continue;
            }

            watcher.ShipId = canonical.Id;
            watcher.RequestedTagName ??= synonymName;
            alreadyWatching.Add(watcher.UserId);
        }

        // ShipId is half of ShipWork's primary key, and EF refuses to modify a key on a tracked
        // entity — so these move by delete-and-insert rather than reassignment.
        var canonicalWorkIds = await _db.ShipWorks
            .Where(sw => sw.ShipId == canonical.Id)
            .Select(sw => sw.WorkId)
            .ToListAsync(ct);
        var held = canonicalWorkIds.ToHashSet();

        var moving = await _db.ShipWorks.Where(sw => sw.ShipId == synonym.Id).ToListAsync(ct);
        foreach (var link in moving)
        {
            _db.ShipWorks.Remove(link);
            if (held.Contains(link.WorkId)) continue;

            _db.ShipWorks.Add(new ShipWork
            {
                ShipId = canonical.Id,
                WorkId = link.WorkId,
                FirstSeenAt = link.FirstSeenAt,
                LastSeenAt = link.LastSeenAt,
                MissingSinceAt = link.MissingSinceAt,
            });
            held.Add(link.WorkId);
        }

        // The synonym's schedule is deleted rather than repointed: jobs are unique per ship, and
        // the canonical ship's own job already holds the run history worth keeping. A fresh job is
        // created rather than the synonym's adopted, so nothing has to survive the ship's deletion.
        var synonymJobs = await _db.ScrapeJobs.Where(j => j.ShipId == synonym.Id).ToListAsync(ct);
        _db.ScrapeJobs.RemoveRange(synonymJobs);

        var canonicalJob = await _db.ScrapeJobs.FirstOrDefaultAsync(j => j.ShipId == canonical.Id, ct);
        if (canonicalJob is null)
        {
            _db.ScrapeJobs.Add(new ScrapeJob
            {
                ShipId = canonical.Id,
                Name = canonical.CanonicalTagName,
                ScraperKey = Ao3ScraperKeys.ShipIndex,
                IsEnabled = true,
            });
        }
        else
        {
            canonicalJob.IsEnabled = true;
        }

        // The canonical ship may itself still be unverified; AO3 just answered for it, so this is
        // the same evidence its own check would have gathered.
        MarkVerified(canonical, tagId);

        await _db.SaveChangesAsync(ct);

        // Now nothing references the synonym, so the cascade has nothing left to take with it.
        _db.Ships.Remove(synonym);
        await _db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
    }
}
