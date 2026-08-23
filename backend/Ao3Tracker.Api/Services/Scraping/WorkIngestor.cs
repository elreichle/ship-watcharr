using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Services.Scraping;

/// <param name="WorksAdded">Works this instance had never seen before, on any ship.</param>
/// <param name="WorksUpdated">Works already known, whose columns were rewritten from this blurb.</param>
public sealed record IngestResult(int WorksSeen, int WorksAdded, int WorksUpdated);

public interface IWorkIngestor
{
    /// <summary>
    /// Writes one page of parsed blurbs, and the ship's claim on them, in a single save.
    /// </summary>
    Task<IngestResult> IngestAsync(Ship ship, IReadOnlyList<Ao3WorkBlurb> blurbs, CancellationToken ct = default);
}

/// <summary>
/// Turns parsed blurbs into rows.
///
/// Separate from <see cref="Ao3BlurbParser"/> and from the scraper because it is the only part of
/// the three that touches the database, and separate from the scraper in particular so that "a page
/// was persisted" is one atomic unit. A backfill's whole resumability rests on that: the cursor may
/// only advance past a page whose works are already committed, or a run that dies mid-page would
/// resume after content it never wrote.
///
/// Everything written here is GLOBAL. Works, tags, pseuds and series are shared across every user
/// and every ship, so this never takes a user id and never writes per-user opinion —
/// <see cref="UserWorkState"/> is nobody's business but its owner's.
/// </summary>
public sealed class WorkIngestor : IWorkIngestor
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;

    public WorkIngestor(AppDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<IngestResult> IngestAsync(
        Ship ship, IReadOnlyList<Ao3WorkBlurb> blurbs, CancellationToken ct = default)
    {
        if (blurbs.Count == 0) return new IngestResult(0, 0, 0);

        var now = _time.GetUtcNow().UtcDateTime;

        // AO3 can render the same work twice on one page — a work in two of the tag's sub-filters,
        // or a listing shifting under deep pagination. Keeping the last occurrence rather than
        // failing keeps the (ShipId, WorkId) key satisfiable.
        var unique = blurbs
            .GroupBy(b => b.WorkId)
            .Select(g => g.Last())
            .ToList();

        // Loaded up front, in three queries rather than three per work. A backfill page carries
        // twenty blurbs with a dozen tags each, so the per-row alternative is a few hundred
        // round-trips for a page that could take one.
        var existingWorks = await LoadExistingWorksAsync(unique, ct);
        var existingLinks = await LoadExistingShipLinksAsync(ship, unique, ct);
        var tagsByKey = await ResolveTagsAsync(unique, now, ct);
        var pseudsByKey = await ResolvePseudsAsync(unique, now, ct);
        var seriesById = await ResolveSeriesAsync(unique, now, ct);

        var added = 0;
        var updated = 0;

        foreach (var blurb in unique)
        {
            if (existingWorks.TryGetValue(blurb.WorkId, out var work))
            {
                updated++;
            }
            else
            {
                work = new Work { Id = blurb.WorkId, FirstSeenAt = now };
                _db.Works.Add(work);
                existingWorks[blurb.WorkId] = work;
                added++;
            }

            Apply(work, blurb, now);
            ApplyTags(work, blurb, tagsByKey);
            ApplyAuthors(work, blurb, pseudsByKey);
            ApplySeries(work, blurb, seriesById, now);
            ApplyShipLink(ship, work, existingLinks, now);
        }

        await _db.SaveChangesAsync(ct);
        return new IngestResult(unique.Count, added, updated);
    }

    // ---- the work row ------------------------------------------------------------------------

    private static void Apply(Work work, Ao3WorkBlurb blurb, DateTime now)
    {
        work.Title = Truncate(blurb.Title, 512)!;
        work.SummaryHtml = blurb.SummaryHtml;
        work.Rating = blurb.Rating;
        work.Categories = blurb.Categories;
        work.Warnings = blurb.Warnings;
        work.IsComplete = blurb.IsComplete;

        work.WordCount = blurb.WordCount;
        work.ChapterCount = blurb.ChapterCount;
        work.PlannedChapterCount = blurb.PlannedChapterCount;

        // Written unconditionally, never gated on UpdatedAt having moved. AO3's revision timestamp
        // tracks content only, so a work that gains ten thousand kudos without being edited keeps
        // the same UpdatedAt — gating here would freeze these columns permanently. See Work.UpdatedAt.
        work.Hits = blurb.Hits;
        work.Kudos = blurb.Kudos;
        work.CommentCount = blurb.CommentCount;
        work.Bookmarks = blurb.Bookmarks;
        work.CollectionCount = blurb.CollectionCount;

        work.LanguageCode = Truncate(blurb.LanguageCode, 16);
        work.LanguageName = Truncate(blurb.LanguageName, 64);

        // An unreadable date leaves whatever a previous run managed to read. Overwriting a real
        // timestamp with MinValue would drag the ship's watermark backwards and re-ingest the tag.
        if (blurb.UpdatedAt > DateTime.MinValue)
        {
            work.UpdatedAt = blurb.UpdatedAt;
            work.UpdatedAtIsApproximate = blurb.UpdatedAtIsApproximate;
        }
        else
        {
            // Nothing to keep on a work first seen through an unreadable date: UpdatedAt stays at
            // its default of year 1. Say so. The parser reports an unreadable date as approximate
            // precisely so the row does not claim a revision time it never read, and leaving this
            // false would have the row assert 0001-01-01 as exact.
            work.UpdatedAtIsApproximate = true;
        }

        // Null is "this blurb's byline could not be read", which is not a claim about the work's
        // authorship at all — see Ao3BlurbParser.ParseByline. Leaving the column alone is the only
        // honest reading: a heading AO3 has reshaped must cost a parse warning, not the credits of
        // every work a pass re-sees.
        if (blurb.IsAnonymous is { } isAnonymous) work.IsAnonymous = isAnonymous;

        work.IsRestricted = blurb.IsRestricted;

        // Appearing in a listing is proof the work exists, so it undoes a deletion recorded earlier.
        work.IsDeleted = false;
        work.DeletedAt = null;

        work.LastSeenAt = now;
        work.LastScrapedAt = now;
    }

    private void ApplyShipLink(Ship ship, Work work, Dictionary<long, ShipWork> existing, DateTime now)
    {
        if (!existing.TryGetValue(work.Id, out var link))
        {
            link = new ShipWork { ShipId = ship.Id, WorkId = work.Id, FirstSeenAt = now };
            _db.ShipWorks.Add(link);
            existing[work.Id] = link;
        }

        link.LastSeenAt = now;

        // Reappearing clears the mark. Only a completed full sweep may set it in the first place,
        // so this is the one direction that is safe on a partial pass.
        link.MissingSinceAt = null;
    }

    // ---- joins -------------------------------------------------------------------------------

    private void ApplyTags(Work work, Ao3WorkBlurb blurb, Dictionary<(Ao3TagType, string), Tag> tagsByKey)
    {
        var desired = blurb.Tags
            .Select(t => tagsByKey.GetValueOrDefault(TagKey(t.Type, t.Name)))
            .Where(t => t is not null)
            .Select(t => t!.Id)
            .ToHashSet();

        Reconcile(work.Tags, desired, wt => wt.TagId, id => new WorkTag { WorkId = work.Id, TagId = id }, _db.WorkTags);
    }

    private void ApplyAuthors(Work work, Ao3WorkBlurb blurb, Dictionary<(string, string), Ao3Pseud> pseudsByKey)
    {
        // The destructive half of the rule above: Reconcile deletes every row not in `desired`, so
        // an unread byline's empty author list would erase the creators a working pass recorded.
        // A work whose byline really did lose its creators reports that as IsAnonymous true, and
        // reaches the reconcile below with the empty list it means.
        if (blurb.IsAnonymous is null) return;

        var ordered = blurb.Authors
            .Select(a => pseudsByKey.GetValueOrDefault(PseudKey(a.Username, a.PseudName)))
            .Where(p => p is not null)
            .Select((p, index) => (Pseud: p!, Position: index))
            .ToList();

        var desired = ordered.Select(x => x.Pseud.Id).ToHashSet();
        Reconcile(work.Authors, desired, wa => wa.PseudId,
            id => new WorkAuthor { WorkId = work.Id, PseudId = id }, _db.WorkAuthors);

        // Position is set after reconciling rather than at construction, so a byline that was
        // reordered updates the rows that survived rather than only the ones just added.
        foreach (var (pseud, position) in ordered)
        {
            var row = work.Authors.FirstOrDefault(wa => wa.PseudId == pseud.Id);
            if (row is not null) row.Position = position;
        }
    }

    private void ApplySeries(
        Work work, Ao3WorkBlurb blurb, Dictionary<long, Ao3Series> seriesById, DateTime now)
    {
        var parts = blurb.Series
            .Where(s => seriesById.ContainsKey(s.Id))
            .ToDictionary(s => s.Id, s => s.Part);

        Reconcile(work.Series, [.. parts.Keys], ws => ws.SeriesId,
            id => new WorkSeries { WorkId = work.Id, SeriesId = id }, _db.WorkSeries);

        foreach (var row in work.Series)
        {
            if (parts.TryGetValue(row.SeriesId, out var part)) row.Part = part;
            seriesById[row.SeriesId].LastSeenAt = now;
        }
    }

    /// <summary>
    /// Brings a work's join rows in line with what the blurb now says, adding what is missing and
    /// removing what is gone.
    ///
    /// The removals are the point: an author who deletes a tag, leaves a series, or drops a
    /// co-creator must stop being recorded, and an add-only ingest would accumulate a work's entire
    /// tag history forever while presenting it as current.
    /// </summary>
    private static void Reconcile<TJoin, TKey>(
        ICollection<TJoin> current,
        HashSet<TKey> desired,
        Func<TJoin, TKey> keyOf,
        Func<TKey, TJoin> create,
        DbSet<TJoin> set)
        where TJoin : class
    {
        foreach (var stale in current.Where(row => !desired.Contains(keyOf(row))).ToList())
        {
            current.Remove(stale);
            set.Remove(stale);
        }

        var held = current.Select(keyOf).ToHashSet();
        foreach (var key in desired.Where(k => !held.Contains(k)))
            current.Add(create(key));
    }

    // ---- shared vocabulary: get-or-create ------------------------------------------------------

    private async Task<Dictionary<long, Work>> LoadExistingWorksAsync(
        List<Ao3WorkBlurb> blurbs, CancellationToken ct)
    {
        var ids = blurbs.Select(b => b.WorkId).ToList();

        // The joins come with them: reconciling a work's tags means knowing which rows it already
        // has, and lazy loading is off, so an un-included collection would read as empty and every
        // existing join would be deleted and re-inserted on every pass.
        return await _db.Works
            .Where(w => ids.Contains(w.Id))
            .Include(w => w.Tags)
            .Include(w => w.Authors)
            .Include(w => w.Series)
            .ToDictionaryAsync(w => w.Id, ct);
    }

    /// <summary>
    /// The ship's existing claims on these works.
    ///
    /// Read from the database rather than from the change tracker, which is the whole point: every
    /// incremental pass re-reads works this ship linked on an earlier run, in a fresh scope with an
    /// empty tracker. Trusting the tracker would insert a second (ShipId, WorkId) row and fail the
    /// save on the unique key.
    /// </summary>
    private async Task<Dictionary<long, ShipWork>> LoadExistingShipLinksAsync(
        Ship ship, List<Ao3WorkBlurb> blurbs, CancellationToken ct)
    {
        var ids = blurbs.Select(b => b.WorkId).ToList();

        return await _db.ShipWorks
            .Where(sw => sw.ShipId == ship.Id && ids.Contains(sw.WorkId))
            .ToDictionaryAsync(sw => sw.WorkId, ct);
    }

    private async Task<Dictionary<(Ao3TagType, string), Tag>> ResolveTagsAsync(
        List<Ao3WorkBlurb> blurbs, DateTime now, CancellationToken ct)
    {
        var wanted = blurbs
            .SelectMany(b => b.Tags)
            .Select(t => (t.Type, Name: Truncate(t.Name, MaxTagNameLength)!))
            .DistinctBy(t => TagKey(t.Type, t.Name))
            .ToList();

        if (wanted.Count == 0) return [];

        var names = wanted.Select(t => Normalize(t.Name)).Distinct().ToList();

        // Filtered by name in the database and by type in memory: the unique key is composite, and
        // a composite IN translates to a clause per pair, which SQLite's parameter limit will not
        // take for a page's worth of tags.
        var existing = await _db.Tags
            .Where(t => names.Contains(t.NameNormalized))
            .ToListAsync(ct);

        var byKey = existing.ToDictionary(t => (t.Type, t.NameNormalized));

        foreach (var (type, name) in wanted)
        {
            var key = TagKey(type, name);
            if (byKey.ContainsKey(key)) continue;

            var tag = new Tag { Type = type, Name = name, NameNormalized = Normalize(name), FirstSeenAt = now };
            _db.Tags.Add(tag);
            byKey[key] = tag;
        }

        // Saved before the works reference them: the join rows need real ids, and EF cannot order
        // an insert against a key it has not generated yet.
        await _db.SaveChangesAsync(ct);
        return byKey;
    }

    private async Task<Dictionary<(string, string), Ao3Pseud>> ResolvePseudsAsync(
        List<Ao3WorkBlurb> blurbs, DateTime now, CancellationToken ct)
    {
        var wanted = blurbs
            .SelectMany(b => b.Authors)
            .Select(a => new Ao3BlurbAuthor(
                Truncate(a.Username, MaxPseudNameLength)!,
                Truncate(a.PseudName, MaxPseudNameLength)!,
                Truncate(a.DisplayName, MaxDisplayNameLength)!))
            .DistinctBy(a => PseudKey(a.Username, a.PseudName))
            .ToList();

        if (wanted.Count == 0) return [];

        var usernames = wanted.Select(a => Normalize(a.Username)).Distinct().ToList();

        // Matched on the normalized column, not the rendered one. Both providers compare text
        // case-sensitively, so filtering on Username would miss a row stored under a different
        // capitalisation of the same account — and then this method would insert a second row for
        // that author and the whole page's save would fail on the unique key.
        var existing = await _db.Ao3Pseuds
            .Where(p => usernames.Contains(p.UsernameNormalized))
            .ToListAsync(ct);

        var byKey = existing.ToDictionary(p => (p.UsernameNormalized, p.PseudNameNormalized));

        foreach (var author in wanted)
        {
            var key = PseudKey(author.Username, author.PseudName);
            if (byKey.ContainsKey(key)) continue;

            var pseud = new Ao3Pseud
            {
                Username = author.Username,
                PseudName = author.PseudName,
                UsernameNormalized = key.Username,
                PseudNameNormalized = key.PseudName,
                DisplayName = author.DisplayName,
                DisplayNameNormalized = Normalize(author.DisplayName),
                FirstSeenAt = now,
            };
            _db.Ao3Pseuds.Add(pseud);
            byKey[key] = pseud;
        }

        await _db.SaveChangesAsync(ct);
        return byKey;
    }

    private async Task<Dictionary<long, Ao3Series>> ResolveSeriesAsync(
        List<Ao3WorkBlurb> blurbs, DateTime now, CancellationToken ct)
    {
        var wanted = blurbs.SelectMany(b => b.Series).DistinctBy(s => s.Id).ToList();
        if (wanted.Count == 0) return [];

        var ids = wanted.Select(s => s.Id).ToList();
        var byId = await _db.Ao3Series.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

        foreach (var blurbSeries in wanted)
        {
            if (byId.TryGetValue(blurbSeries.Id, out var series))
            {
                series.Title = Truncate(blurbSeries.Title, 512)!;
                continue;
            }

            series = new Ao3Series
            {
                Id = blurbSeries.Id,
                Title = Truncate(blurbSeries.Title, 512)!,
                FirstSeenAt = now,
                LastSeenAt = now,
            };
            _db.Ao3Series.Add(series);
            byId[blurbSeries.Id] = series;
        }

        await _db.SaveChangesAsync(ct);
        return byId;
    }

    private static string Normalize(string value) => value.ToUpperInvariant();

    // Column widths, named because identity depends on them: a key built from an untruncated name
    // does not match the row stored under the truncated one, so the lookup misses and the tag or
    // author is silently dropped from the work. Truncating inside the key functions is what keeps
    // "what we store" and "what we look up by" the same thing by construction.
    private const int MaxTagNameLength = 200;
    private const int MaxPseudNameLength = 100;
    private const int MaxDisplayNameLength = 200;

    /// <summary>How a tag is identified: its type, and its stored, normalized name.</summary>
    private static (Ao3TagType Type, string Name) TagKey(Ao3TagType type, string name) =>
        (type, Normalize(Truncate(name, MaxTagNameLength)!));

    /// <summary>How a creator is identified: the stored, normalized username and pseud.</summary>
    private static (string Username, string PseudName) PseudKey(string username, string pseudName) =>
        (Normalize(Truncate(username, MaxPseudNameLength)!),
            Normalize(Truncate(pseudName, MaxPseudNameLength)!));

    /// <summary>
    /// Clips a value to its column width. AO3 enforces its own limits, but they are AO3's to change,
    /// and a tag one character over would otherwise fail the save for the whole page rather than
    /// just itself.
    /// </summary>
    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
