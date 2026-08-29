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
    /// <param name="announceToWatchers">
    /// Whether a work this page adds to the ship is news. Only the caller knows: the same method
    /// writes a backfill's thousand-work back catalogue and an incremental pass's one new work, and
    /// the rows are identical. See <see cref="WorkIngestor.AnnounceAsync"/> for the rule the
    /// scraper applies before passing true.
    /// </param>
    Task<IngestResult> IngestAsync(
        Ship ship,
        IReadOnlyList<Ao3WorkBlurb> blurbs,
        bool announceToWatchers = false,
        CancellationToken ct = default);

    /// <summary>
    /// Writes what a work's own page said about it: its publication date, its complete tag list, and
    /// the fact that the page was read at all.
    /// </summary>
    /// <returns>
    /// False where the work is not in this library — a work can be unfollowed, merged away or
    /// deleted between a detail pass selecting it and the page arriving, and none of that is a
    /// failure worth recording against the fetch.
    /// </returns>
    Task<bool> IngestDetailAsync(long workId, Ao3WorkPage page, CancellationToken ct = default);
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
        Ship ship,
        IReadOnlyList<Ao3WorkBlurb> blurbs,
        bool announceToWatchers = false,
        CancellationToken ct = default)
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

        // Loaded up front, in a fixed handful of queries rather than a handful per work. A backfill
        // page carries twenty blurbs with a dozen tags each, so the per-row alternative is a few
        // hundred round-trips for a page that could take a few.
        var existingWorks = await LoadExistingWorksAsync(unique, ct);
        var existingLinks = await LoadExistingShipLinksAsync(ship, unique, ct);
        var tagsByKey = await ResolveTagsAsync([.. unique.SelectMany(b => b.Tags)], now, ct);
        var pseudsByKey = await ResolvePseudsAsync(unique, now, ct);
        var seriesById = await ResolveSeriesAsync(unique, now, ct);

        var added = 0;
        var updated = 0;

        // Works this ship did not have before this page. Collected here rather than re-derived
        // afterwards because this loop is the only place the distinction exists: once the links are
        // saved, a work the ship gained a second ago and one it has had for a year are the same row.
        var newToTheShip = new List<long>();

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

            if (ApplyShipLink(ship, work, existingLinks, now)) newToTheShip.Add(work.Id);
        }

        var notified = announceToWatchers && newToTheShip.Count > 0
            ? await AnnounceAsync(ship, newToTheShip, now, ct)
            : [];

        await _db.SaveChangesAsync(ct);

        // After the save, not in it: a crash between the two costs one reader an over-long list,
        // where folding it in would risk the page.
        if (notified.Count > 0) await CapNotificationsAsync(notified, ct);

        return new IngestResult(unique.Count, added, updated);
    }

    /// <summary>
    /// The other side of <see cref="ApplyTags"/>'s rule: the observation that is complete.
    /// </summary>
    /// <remarks>
    /// <para>A work's own page carries its whole tag list, so this reconciles — adding what the page
    /// shows and removing what it does not — where a listing blurb, which carries an abbreviated
    /// one, may only add once a page has been read. That is the whole of what
    /// <see cref="Work.DetailFetchedAt"/> decides, and this is the only writer of it.</para>
    /// <para><b>An empty tag list deletes nothing.</b> AO3 requires a fandom of every work, so a page
    /// that parsed with no tags is a markup change rather than a work that lost them, and the same
    /// rule that lets this method delete at all — a source may delete only within a scope it observed
    /// completely — is what says an empty read observed nothing. <see cref="Ao3WorkDetailScraper"/>
    /// does not call this for such a page at all, precisely so that the stamp below cannot be written
    /// from one: <see cref="Work.DetailFetchedAt"/> is also what puts the listing pass into add-only
    /// mode, and a work stamped from a tagless page would have no source left that may drop a tag.
    /// The guard stays because this is a public method and the rule is its own, not its caller's.</para>
    /// <para>Only the tags: the rating, the categories and the stats are on the page too, and are
    /// left to the listing pass that already writes them. A detail fetch happens once per work and
    /// then not again until the work is revised, while a listing pass re-reads a work's kudos every
    /// six hours — so writing those here would put a stale copy in front of a fresh one.</para>
    /// </remarks>
    public async Task<bool> IngestDetailAsync(long workId, Ao3WorkPage page, CancellationToken ct = default)
    {
        // The joins come with it for the reason LoadExistingWorksAsync gives: lazy loading is off,
        // so an un-included collection reads as empty, and reconciling against that would delete
        // every tag the work has.
        var work = await _db.Works
            .Include(w => w.Tags)
            .FirstOrDefaultAsync(w => w.Id == workId, ct);

        if (work is null) return false;

        var now = _time.GetUtcNow().UtcDateTime;
        var tagsByKey = await ResolveTagsAsync(page.Tags, now, ct);

        if (page.Tags.Count > 0)
        {
            var desired = page.Tags
                .Select(t => tagsByKey.GetValueOrDefault(TagKey(t.Type, t.Name)))
                .Where(t => t is not null)
                .Select(t => t!.Id)
                .ToHashSet();

            Reconcile(
                work.Tags, desired, wt => wt.TagId,
                id => new WorkTag { WorkId = work.Id, TagId = id }, _db.WorkTags);
        }

        // A date the page did not carry leaves whatever an earlier fetch managed to read, the same
        // way an unreadable blurb date leaves UpdatedAt alone. Null here is this parse's shortfall,
        // not a claim that the work has no publication date.
        if (page.PublishedAt is { } published) work.PublishedAt = published;

        // Written even when nothing above it was: the expensive thing — the request — happened, and
        // a page read but not stamped is a page re-fetched on every pass for ever. What the fetch
        // could not read is reported as a parse warning by the pass that made it.
        work.DetailFetchedAt = now;

        await _db.SaveChangesAsync(ct);
        return true;
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
            // Stamped only when the revision actually moved, which is the whole value of the column:
            // written on every pass it would be LastScrapedAt under another name, and nothing could
            // read it as "everything fetched before this moment describes the previous version".
            // That reading is what lets a download tell a cached work page from a current one.
            if (work.UpdatedAt != blurb.UpdatedAt) work.UpdatedAtObservedAt = now;

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

    /// <returns>True where the ship did not have this work before — see <see cref="AnnounceAsync"/>.</returns>
    private bool ApplyShipLink(Ship ship, Work work, Dictionary<long, ShipWork> existing, DateTime now)
    {
        var isNew = !existing.TryGetValue(work.Id, out var link);

        if (isNew)
        {
            link = new ShipWork { ShipId = ship.Id, WorkId = work.Id, FirstSeenAt = now };
            _db.ShipWorks.Add(link);
            existing[work.Id] = link;
        }

        link!.LastSeenAt = now;

        // Reappearing clears the mark. Only a completed full sweep may set it in the first place,
        // so this is the one direction that is safe on a partial pass.
        link.MissingSinceAt = null;

        return isNew;
    }

    // ---- telling the watchers ------------------------------------------------------------------

    /// <summary>
    /// One <see cref="Notification"/> per watcher per work the ship has just gained.
    /// </summary>
    /// <remarks>
    /// <para><b>The rule for what counts as news</b>, which is three conditions and needs to be all
    /// three:</para>
    /// <list type="number">
    /// <item><description>
    /// The work is <b>new to this ship</b>, not new to the instance. A work already in the library
    /// under another followed tag is still news to someone who follows this one, and the same work
    /// re-read by a later pass is not news to anyone.
    /// </description></item>
    /// <item><description>
    /// The pass is <b>incremental</b>. A backfill walking backwards into a tag's history and a full
    /// sweep re-walking all of it both create links in bulk for works that are years old — four
    /// thousand of them on a large tag — and none of it is new. Only the watermark-bounded pass
    /// looks at the end of the listing where new works appear.
    /// </description></item>
    /// <item><description>
    /// The ship <b>already had a watermark</b> when the run started. The first incremental pass over
    /// a newly followed tag has nothing to stop at, so its idea of "newer than the watermark" is
    /// every work in the tag — which is exactly the back catalogue nobody asked to be told about.
    /// </description></item>
    /// </list>
    /// <para>The first is decided here; the other two are the caller's, since this method is handed
    /// one page and cannot see which pass produced it. See <c>Ao3ShipIndexScraper</c>.</para>
    /// <para>A reader who follows a tag someone else already follows is told about its next new
    /// work straight away, and that is right: the ship has a watermark, so the pass really is
    /// reporting an arrival rather than a history.</para>
    /// </remarks>
    /// <returns>The users given at least one row, for <see cref="CapNotificationsAsync"/>.</returns>
    private async Task<List<string>> AnnounceAsync(
        Ship ship, List<long> workIds, DateTime now, CancellationToken ct)
    {
        // The existing per-user switch, and the only thing that reads it. Off means the reader still
        // follows the ship and still gets its works in their library — they have just said they do
        // not want to be told.
        var watchers = await _db.WatchedShips
            .Where(w => w.ShipId == ship.Id && w.NotificationsEnabled)
            .Select(w => w.UserId)
            .ToListAsync(ct);

        foreach (var userId in watchers)
        {
            foreach (var workId in workIds)
            {
                _db.Notifications.Add(new Notification
                {
                    UserId = userId,
                    ShipId = ship.Id,
                    WorkId = workId,
                    CreatedAt = now,
                });
            }
        }

        return watchers;
    }

    /// <summary>
    /// Drops everything past <see cref="Notification.MaxPerUser"/> for the readers just notified,
    /// so the table cannot grow without bound on an instance nobody reads.
    /// </summary>
    /// <remarks>
    /// Only those readers: a sweep of every account on every page would be a query for a table that
    /// is almost always already inside its bound. Ordered by id rather than
    /// <see cref="Notification.CreatedAt"/> because a page ingested in one save stamps every row
    /// with the same instant, and a cap that cannot break that tie deterministically would pick
    /// arbitrarily among the newest rows.
    /// </remarks>
    /// <remarks>
    /// A reader inside their bound costs one index-only seek that returns nothing, and no delete —
    /// which is what makes running this once per page affordable. Per page rather than once per
    /// run because this class's unit is the page and it has no notion of the run around it; an
    /// announcing pass is one or two pages, since a pass with more than that to say is one whose
    /// ship had no watermark and therefore announced nothing at all.
    /// </remarks>
    private async Task CapNotificationsAsync(List<string> userIds, CancellationToken ct)
    {
        foreach (var userId in userIds)
        {
            // The id of the oldest row this reader is allowed to keep. Zero means they have fewer
            // than the cap allows, since no row's id is zero — and that is the ordinary answer.
            var oldestKept = await _db.Notifications
                .Where(n => n.UserId == userId)
                .OrderByDescending(n => n.Id)
                .Skip(Notification.MaxPerUser - 1)
                .Select(n => n.Id)
                .FirstOrDefaultAsync(ct);

            if (oldestKept == 0) continue;

            await _db.Notifications
                .Where(n => n.UserId == userId && n.Id < oldestKept)
                .ExecuteDeleteAsync(ct);
        }
    }

    // ---- joins -------------------------------------------------------------------------------

    /// <summary>
    /// Brings a work's tags in line with the blurb — adding what it shows, and removing what it
    /// does not <b>only while the blurb is the whole of what anyone has observed</b>.
    /// </summary>
    /// <remarks>
    /// <para><b>The rule, which T10's detail fetch writes through:</b> a source may delete only
    /// within a scope it observed completely. A listing blurb is not a complete observation of a
    /// work's tags — the spec's user story 11 says so outright, and it is the entire reason a
    /// per-work detail fetch exists. So the blurb is authoritative until the work's own page has
    /// been read, and from then on it may add but never delete. The moment a work has a
    /// <see cref="Work.DetailFetchedAt"/>, the reconcile below would otherwise erase every tag only
    /// that page carried, on the next incremental pass over the ship, silently, on a run recorded
    /// as a success.</para>
    /// <para>Keyed on the column rather than on who is calling, because the ingestor is handed a
    /// page and cannot see which pass produced it — and because the fact that matters is about the
    /// work, not the caller.</para>
    /// <para><b>Two alternatives were weighed and rejected.</b> Reconciling only within the tag
    /// types a blurb is authoritative for needs an answer to which types those are, which is a
    /// markup question nobody has captured; and it still deletes wrongly if a blurb is short
    /// <i>within</i> a type. A provenance column per <see cref="WorkTag"/> would let a listing pass
    /// reconcile its own rows precisely, at the cost of a migration on both providers — and it is
    /// still wrong for a tag both sources saw, which the listing would then delete out from under
    /// the detail page. Neither buys anything over deferring to the fuller observation.</para>
    /// <para><b>The accepted cost is a stale tag, never a lost one.</b> A tag the author has since
    /// removed survives on a detail-fetched work until the next detail fetch — and AO3's revision
    /// timestamp tracks content, so a tag-only edit may not schedule one. That is the right side to
    /// err on: this direction shows a reader a tag too many, the other loses tags nobody can get
    /// back without re-fetching every work.</para>
    /// <para><see cref="ApplyAuthors"/> and <see cref="ApplySeries"/> share the reconcile and are
    /// deliberately left with it: a blurb carries a work's whole byline and whole series list, so
    /// its observation of those really is complete. Should anything ever write them from a second
    /// source, this rule applies to them too.</para>
    /// </remarks>
    private void ApplyTags(Work work, Ao3WorkBlurb blurb, Dictionary<(Ao3TagType, string), Tag> tagsByKey)
    {
        var desired = blurb.Tags
            .Select(t => tagsByKey.GetValueOrDefault(TagKey(t.Type, t.Name)))
            .Where(t => t is not null)
            .Select(t => t!.Id)
            .ToHashSet();

        WorkTag Create(int id) => new() { WorkId = work.Id, TagId = id };

        if (work.DetailFetchedAt is not null)
        {
            AddMissing(work.Tags, desired, wt => wt.TagId, Create);
            return;
        }

        Reconcile(work.Tags, desired, wt => wt.TagId, Create, _db.WorkTags);
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

        AddMissing(current, desired, keyOf, create);
    }

    /// <summary>
    /// The non-destructive half of <see cref="Reconcile"/>, on its own: what a source that observed
    /// something real but not everything is entitled to do. See <see cref="ApplyTags"/>.
    /// </summary>
    private static void AddMissing<TJoin, TKey>(
        ICollection<TJoin> current,
        HashSet<TKey> desired,
        Func<TJoin, TKey> keyOf,
        Func<TKey, TJoin> create)
    {
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
        //
        // Split, because three collection Includes in one query is a cartesian product: a page of
        // twenty known works with ~15 tags, ~2 authors and ~1 series each is 20 x 15 x 2 x 1 rows,
        // every work column repeated in each of them, on every incremental pass over works that
        // have not changed. Four small queries beat one that multiplies out. Nothing in this project
        // sets QuerySplittingBehavior globally, so it is said here.
        //
        // Safe without an OrderBy only because this query is unpaged: split queries can tear when
        // Skip/Take runs over a non-deterministic order, and there is no row limit here to tear.
        return await _db.Works
            .Where(w => ids.Contains(w.Id))
            .Include(w => w.Tags)
            .Include(w => w.Authors)
            .Include(w => w.Series)
            .AsSplitQuery()
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

    /// <summary>
    /// Get-or-create over the shared tag vocabulary, for a whole page of blurbs or for one work's
    /// own page — hence a flat list of tags rather than the blurbs they came off. Both sources
    /// resolve through here, which is what stops the same tag becoming two rows depending on which
    /// pass saw it first.
    /// </summary>
    private async Task<Dictionary<(Ao3TagType, string), Tag>> ResolveTagsAsync(
        IReadOnlyList<Ao3BlurbTag> tags, DateTime now, CancellationToken ct)
    {
        var wanted = tags
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
