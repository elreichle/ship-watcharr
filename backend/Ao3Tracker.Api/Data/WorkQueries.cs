using System.Linq.Expressions;
using Ao3Tracker.Api.Models;

namespace Ao3Tracker.Api.Data;

/// <summary>
/// How the library is narrowed and ordered — the <c>Where</c> and <c>OrderBy</c> clauses behind
/// both the works list and a saved filter's match count.
///
/// Shared rather than duplicated per caller because the two have to agree exactly: a count that
/// says "412 works" while the list it links to shows a different set is worse than no count.
/// </summary>
public static class WorkQueries
{
    /// <summary>
    /// The sorts the library offers, and the only values <see cref="SavedWorkFilter.Sort"/> may
    /// hold. Title is deliberately absent — see the remarks on <see cref="Order"/>.
    /// </summary>
    public static readonly string[] Sorts = ["updated", "kudos", "hits", "bookmarks", "comments", "words"];

    public static bool IsOfferedSort(string sort) => Sorts.Contains(sort);

    /// <summary>
    /// The works one user can see, optionally narrowed to a single ship.
    ///
    /// Works are global rows, so "whose library is this" is answered entirely by the caller's
    /// subscriptions. <paramref name="shipId"/> intersects with them rather than replacing them:
    /// passing a ship the user does not watch yields nothing, which is what keeps a saved filter
    /// naming a since-unwatched ship from reaching outside its author's library.
    /// </summary>
    public static IQueryable<Work> Library(AppDbContext db, string userId, int? shipId)
    {
        var watchedShipIds = db.WatchedShips
            .Where(w => w.UserId == userId)
            .Select(w => w.ShipId);

        // Membership is ShipWork, never the work's own relationship tags: AO3 tag synonyms mean a
        // work returned by the canonical tag can render a synonym in its own blurb, so filtering
        // on tags would silently drop it. See the remarks on ShipWork.
        return db.Works.Where(w => !w.IsDeleted && w.Ships.Any(sw =>
            watchedShipIds.Contains(sw.ShipId) && (shipId == null || sw.ShipId == shipId)));
    }

    /// <summary>
    /// One reader's own states — reading status, rating, note — and nobody else's.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="Library"/> rather than inline at each call site for the same reason: the
    /// works list, the state endpoints and any future count over "unread" all have to narrow by the
    /// same predicate, and <see cref="UserWorkState.UserId"/> is the whole of "whose state is this".
    /// A query that forgets it does not return too much — it returns someone else's opinion of the
    /// same work, which reads as the caller's own.
    /// </remarks>
    public static IQueryable<UserWorkState> StatesOf(AppDbContext db, string userId) =>
        db.UserWorkStates.Where(s => s.UserId == userId);

    /// <summary>
    /// Applies the requested sort, always tie-broken by id. Null for a sort that isn't offered.
    ///
    /// The tie-break is what makes paging correct, not merely tidy: thousands of works share a
    /// kudos count, and without a total order the database may return those rows differently on
    /// each query — so a work could appear on two consecutive pages while another appears on none.
    ///
    /// Title is deliberately not offered. It has no normalized column, and ordering on it directly
    /// would put "apple" before or after "Banana" depending on whether the instance runs SQLite or
    /// PostgreSQL. See the remarks on <see cref="Tag.NameNormalized"/>.
    /// </summary>
    public static IOrderedQueryable<Work>? Order(IQueryable<Work> query, string sort, bool ascending)
    {
        IOrderedQueryable<Work> By<TKey>(Expression<Func<Work, TKey>> key) =>
            ascending ? query.OrderBy(key) : query.OrderByDescending(key);

        IOrderedQueryable<Work>? ordered = sort switch
        {
            "updated" => By(w => w.UpdatedAt),
            "kudos" => By(w => w.Kudos),
            "hits" => By(w => w.Hits),
            "bookmarks" => By(w => w.Bookmarks),
            "comments" => By(w => w.CommentCount),
            "words" => By(w => w.WordCount),
            _ => null,
        };

        if (ordered is null) return null;
        return ascending ? ordered.ThenBy(w => w.Id) : ordered.ThenByDescending(w => w.Id);
    }

    /// <summary>
    /// Narrows <paramref name="works"/> by every criterion the filter sets, leaving the ones it
    /// leaves null alone.
    /// </summary>
    /// <remarks>
    /// <see cref="SavedWorkFilter.ShipId"/> is NOT applied here. Ship scoping is the caller's,
    /// because the works list has to intersect it with the reader's current subscriptions rather
    /// than trust what the filter names — see the remarks on that property.
    /// </remarks>
    /// <remarks>
    /// <paramref name="filter"/>'s <c>Tags</c> and <c>Authors</c> must already be loaded. They are
    /// read into arrays here rather than queried through the navigation, so the criteria become
    /// parameters of one SQL statement instead of a lazy-load per clause.
    /// </remarks>
    /// <param name="callerStates">
    /// The states of the user applying the set — <see cref="StatesOf"/> for their id, and nobody
    /// else's. A parameter rather than a <c>userId</c> this method resolves itself, so that every
    /// call site has to name whose reading it is filtering by: the reading-status and rating
    /// criteria are the only ones whose result depends on who asks, and a defaulted or forgotten
    /// user would answer with someone else's opinion of the same works.
    /// </param>
    public static IQueryable<Work> ApplyFilter(
        IQueryable<Work> works,
        SavedWorkFilter filter,
        IQueryable<UserWorkState> callerStates)
    {
        if (filter.IsComplete is bool complete) works = works.Where(w => w.IsComplete == complete);

        // Written out rather than driven from a column expression: each bound has to close over a
        // local so EF sends it as a parameter, and a helper building the comparison by hand would
        // bake the number into the SQL text instead, giving every distinct bound its own query plan.
        if (filter.MinWordCount is int minWords) works = works.Where(w => w.WordCount >= minWords);
        if (filter.MaxWordCount is int maxWords) works = works.Where(w => w.WordCount <= maxWords);
        if (filter.MinChapterCount is int minChapters) works = works.Where(w => w.ChapterCount >= minChapters);
        if (filter.MaxChapterCount is int maxChapters) works = works.Where(w => w.ChapterCount <= maxChapters);
        if (filter.MinKudos is int minKudos) works = works.Where(w => w.Kudos >= minKudos);
        if (filter.MaxKudos is int maxKudos) works = works.Where(w => w.Kudos <= maxKudos);
        if (filter.MinHits is int minHits) works = works.Where(w => w.Hits >= minHits);
        if (filter.MaxHits is int maxHits) works = works.Where(w => w.Hits <= maxHits);
        if (filter.MinComments is int minComments) works = works.Where(w => w.CommentCount >= minComments);
        if (filter.MaxComments is int maxComments) works = works.Where(w => w.CommentCount <= maxComments);
        if (filter.MinBookmarks is int minBookmarks) works = works.Where(w => w.Bookmarks >= minBookmarks);
        if (filter.MaxBookmarks is int maxBookmarks) works = works.Where(w => w.Bookmarks <= maxBookmarks);

        // Ao3Rating ascends by explicitness, so a band is a pair of comparisons on the indexed
        // column. Note that Unknown sorts below every real rating: a filter that sets only an upper
        // bound therefore keeps works whose rating we never parsed, which is the honest reading of
        // "Teen and below" when the alternative is silently hiding rows for a scraper's shortfall.
        if (filter.MinRating is Ao3Rating min) works = works.Where(w => w.Rating >= min);
        if (filter.MaxRating is Ao3Rating max) works = works.Where(w => w.Rating <= max);

        // The reader's own state, matched through a correlated EXISTS over callerStates rather than
        // a navigation on Work — a navigation would be loadable without saying whose state it is.
        if (filter.ReadingStatus is ReadingStatus status)
        {
            // "Unread" is the absence of a mark, and the absence has two shapes: no row at all, and
            // a row left saying None because a status was cleared while a rating or note stayed. A
            // NOT EXISTS over "marked as anything" covers both, where an equality against None would
            // find only the second and silently drop every work nobody has ever touched — which is
            // most of a fresh library.
            works = status == ReadingStatus.None
                ? works.Where(w => !callerStates.Any(s => s.WorkId == w.Id && s.Status != ReadingStatus.None))
                : works.Where(w => callerStates.Any(s => s.WorkId == w.Id && s.Status == status));
        }

        // Half-stars, 1-10. An unrated work has a null Rating, so it satisfies neither comparison
        // and drops out of a bounded set — see the remarks on SavedWorkFilter.MinUserRating.
        if (filter.MinUserRating is int minMine)
            works = works.Where(w => callerStates.Any(s => s.WorkId == w.Id && s.Rating >= minMine));
        if (filter.MaxUserRating is int maxMine)
            works = works.Where(w => callerStates.Any(s => s.WorkId == w.Id && s.Rating <= maxMine));

        // Flags masks, compared bitwise against the column rather than expanded into a set of
        // equality checks — the whole reason Ao3Category and Ao3Warning are stored as ints.
        if (filter.IncludeCategories is Ao3Category anyCategory && anyCategory != Ao3Category.None)
            works = works.Where(w => (w.Categories & anyCategory) != 0);

        if (filter.ExcludeCategories is Ao3Category noCategory && noCategory != Ao3Category.None)
            works = works.Where(w => (w.Categories & noCategory) == 0);

        if (filter.IncludeWarnings is Ao3Warning anyWarning && anyWarning != Ao3Warning.None)
            works = works.Where(w => (w.Warnings & anyWarning) != 0);

        if (filter.ExcludeWarnings is Ao3Warning noWarning && noWarning != Ao3Warning.None)
            works = works.Where(w => (w.Warnings & noWarning) == 0);

        if (!string.IsNullOrEmpty(filter.LanguageCode))
        {
            var language = filter.LanguageCode;
            works = works.Where(w => w.LanguageCode == language);
        }

        if (filter.UpdatedAfter is DateTime after) works = works.Where(w => w.UpdatedAt >= after);
        if (filter.UpdatedBefore is DateTime before) works = works.Where(w => w.UpdatedAt <= before);

        // Included tags are AND'ed, so each one gets its own Exists clause — a single
        // Any(t => included.Contains(t)) would widen the filter with every box ticked instead of
        // narrowing it. See the remarks on SavedWorkFilterTag.
        foreach (var tagId in filter.Tags.Where(t => !t.Exclude).Select(t => t.TagId).ToArray())
        {
            var required = tagId;
            works = works.Where(w => w.Tags.Any(t => t.TagId == required));
        }

        var excludedTagIds = filter.Tags.Where(t => t.Exclude).Select(t => t.TagId).ToArray();
        if (excludedTagIds.Length > 0)
            works = works.Where(w => !w.Tags.Any(t => excludedTagIds.Contains(t.TagId)));

        // Authors are OR'ed, unlike tags: a work has one byline per creator, so requiring two at
        // once would only ever match co-authored works.
        var includedPseudIds = filter.Authors.Where(a => !a.Exclude).Select(a => a.PseudId).ToArray();
        if (includedPseudIds.Length > 0)
            works = works.Where(w => w.Authors.Any(a => includedPseudIds.Contains(a.PseudId)));

        var excludedPseudIds = filter.Authors.Where(a => a.Exclude).Select(a => a.PseudId).ToArray();
        if (excludedPseudIds.Length > 0)
            works = works.Where(w => !w.Authors.Any(a => excludedPseudIds.Contains(a.PseudId)));

        return works;
    }
}
