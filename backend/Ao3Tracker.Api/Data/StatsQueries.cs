using System.Linq.Expressions;
using Ao3Tracker.Api.Models;

namespace Ao3Tracker.Api.Data;

/// <summary>
/// The aggregates behind <c>GET /api/stats</c> — every one of them a query the database runs,
/// never a fold over a materialized library.
/// </summary>
/// <remarks>
/// Written as <see cref="IQueryable{T}"/>-returning methods rather than inline in the controller
/// for the same reason <see cref="WorkQueries"/> is: it is the only way a test can hand the same
/// expression to the PostgreSQL provider and ask whether it translates. Nothing here is exercised
/// against a real PostgreSQL server anywhere in this repo, so <c>ToQueryString()</c> under Npgsql
/// is the whole of the cross-provider guarantee — see <c>StatsQueryTranslationTests</c>.
/// </remarks>
/// <remarks>
/// Every count starts from <see cref="WorkQueries.Library"/> rather than restating its predicate,
/// so a number on the statistics page and the list it describes can never disagree about which
/// works are in the library. That is the same rule the saved-filter match count follows.
/// </remarks>
public static class StatsQueries
{
    /// <summary>
    /// The kudos histogram's bars, and the only place their boundaries are written down — the SQL
    /// <c>CASE</c> that assigns a work to one is built from this array by <see cref="Distribution"/>,
    /// so a bar's label and the comparison behind it cannot drift apart.
    /// </summary>
    public static readonly StatsBucket[] KudosBuckets =
    [
        new("0-9", 0, 9),
        new("10-49", 10, 49),
        new("50-99", 50, 99),
        new("100-499", 100, 499),
        new("500-999", 500, 999),
        new("1,000-4,999", 1000, 4999),
        new("5,000+", 5000, null),
    ];

    /// <inheritdoc cref="KudosBuckets"/>
    public static readonly StatsBucket[] WordCountBuckets =
    [
        new("under 1,000", 0, 999),
        new("1,000-4,999", 1000, 4999),
        new("5,000-9,999", 5000, 9999),
        new("10,000-29,999", 10000, 29999),
        new("30,000-49,999", 30000, 49999),
        new("50,000-99,999", 50000, 99999),
        new("100,000+", 100000, null),
    ];

    /// <summary>How many creators the "most prolific" list names.</summary>
    public const int TopAuthorCount = 10;

    /// <summary>
    /// The library's works that the caller has marked <see cref="ReadingStatus.Read"/>.
    /// </summary>
    /// <remarks>
    /// An <c>EXISTS</c> over <paramref name="states"/> rather than a navigation on <c>Work</c>, for
    /// the reason <see cref="WorkQueries.StatesOf"/> gives: reading state is per-user, and a query
    /// that forgets whose does not return too much — it returns someone else's.
    /// </remarks>
    public static IQueryable<Work> Read(IQueryable<Work> library, IQueryable<UserWorkState> states) =>
        library.Where(w => states.Any(s => s.WorkId == w.Id && s.Status == ReadingStatus.Read));

    /// <summary>
    /// The ships the caller watches, in the order the page lists them.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PerShip"/>, and the reason the two are separate is that a followed
    /// ship with nothing in it has no works to be grouped and so cannot appear in an aggregate over
    /// the library at all. Driving the list from the subscriptions instead means a fresh install —
    /// or one the AO3-login gate is holding — shows its ships sitting at zero rather than showing an
    /// empty page that explains nothing. <see cref="PerShip"/> fills in the numbers where there are
    /// any.
    /// </remarks>
    /// <remarks>
    /// Ordered by <see cref="Ship.CanonicalTagNameNormalized"/>, not the display name: SQLite and
    /// PostgreSQL disagree about where a lowercase letter sorts, so ordering on the rendered text
    /// would order one instance's page differently from another's. Same rule as
    /// <see cref="WorkQueries.Order"/>'s refusal to sort by title.
    /// </remarks>
    public static IQueryable<WatchedShipRow> WatchedShips(AppDbContext db, string userId, int? shipId)
    {
        var watched = WorkQueries.WatchedShipsOf(db, userId);
        if (shipId is int only) watched = watched.Where(w => w.ShipId == only);

        return watched
            .OrderBy(w => w.Ship.CanonicalTagNameNormalized)
            .Select(w => new WatchedShipRow(w.ShipId, w.Ship.CanonicalTagName));
    }

    /// <summary>
    /// Both lenses per ship: the size of its corpus, and how much of it the caller has marked.
    /// </summary>
    /// <remarks>
    /// One grouped pass over the library rather than a set of counts correlated to each watched
    /// ship. The correlated form reads well and is a trap: every aggregate becomes its own subquery
    /// over the whole works table, re-executed per ship, so a reader following twenty ships pays
    /// twenty scans per figure. Grouping reads each work once however many ships are followed.
    /// </remarks>
    /// <remarks>
    /// A work carrying two watched relationship tags is counted under both, so these rows do not
    /// sum to the corpus total — which is the honest shape for "how big is this ship", and the
    /// reason the total is counted separately rather than added up from here. They can also sum to
    /// less than it: a work the reader marked and every watched tag has since dropped is in their
    /// corpus and under no ship.
    /// </remarks>
    /// <remarks>
    /// The caller's status and rating are looked up once per work and then folded, rather than
    /// tested with an <c>EXISTS</c> inside each aggregate. A missing row yields
    /// <see cref="ReadingStatus.None"/> and a null rating, which are already the values that mean
    /// "this reader has said nothing" — see <see cref="WorkQueries.ApplyFilter"/>.
    /// </remarks>
    public static IQueryable<ShipStatsRow> PerShip(AppDbContext db, string userId, int? shipId)
    {
        var library = WorkQueries.Library(db, userId, shipId);
        var states = WorkQueries.StatesOf(db, userId);
        var watchedShipIds = WorkQueries.WatchedShipIdsOf(db, userId);

        return library
            .SelectMany(
                w => w.Ships.Where(sw => watchedShipIds.Contains(sw.ShipId)
                    && (shipId == null || sw.ShipId == shipId)),
                (w, sw) => new
                {
                    sw.ShipId,
                    sw.MissingSinceAt,
                    w.WordCount,
                    Marked = states.Any(s => s.WorkId == w.Id),
                    Status = states.Where(s => s.WorkId == w.Id).Select(s => s.Status).FirstOrDefault(),
                    Rating = states.Where(s => s.WorkId == w.Id).Select(s => s.Rating).FirstOrDefault(),
                })

            // WorkQueries.Library's membership test, restated per ship rather than per work: a work
            // in the library through one watched tag may have left another, and a figure under the
            // tag it left is one the feed narrowed to that tag contradicts. A work this reader has
            // marked counts under the tag it left for the same reason — that feed lists it. Applied
            // to the flattened rows rather than inside the SelectMany above, where a correlated
            // EXISTS makes the whole query need SQL APPLY, which SQLite does not have.
            .Where(x => x.MissingSinceAt == null || x.Marked)
            .GroupBy(x => x.ShipId)
            .Select(g => new ShipStatsRow(
                g.Key,
                g.Count(),
                g.Sum(x => (long)x.WordCount),
                g.Sum(x => x.Status != ReadingStatus.None ? 1 : 0),
                g.Sum(x => x.Status == ReadingStatus.Read ? 1 : 0),
                g.Sum(x => x.Rating != null ? 1 : 0)));
    }

    /// <summary>
    /// How many works the library holds per calendar month of <see cref="Work.UpdatedAt"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Work.UpdatedAt"/> and not <see cref="Work.PublishedAt"/>, which is null until
    /// somebody opens a work's own page and would therefore describe what has been clicked on
    /// rather than what the ship's corpus looks like. So this is "works last revised in this month",
    /// which is what the DTO's name says; it is not a publication curve.
    /// </remarks>
    /// <remarks>
    /// Carries only the months the library actually has works in — no zero-filling between them.
    /// The span is data, and a single work carrying a misparsed year would otherwise make the server
    /// materialize centuries of empty buckets. A caller wanting a gapless axis has the year and
    /// month on every row and can fill it exactly.
    /// </remarks>
    public static IQueryable<MonthCountRow> WorksByUpdatedMonth(IQueryable<Work> library) =>
        library
            .GroupBy(w => new { w.UpdatedAt.Year, w.UpdatedAt.Month })
            .OrderBy(g => g.Key.Year)
            .ThenBy(g => g.Key.Month)
            .Select(g => new MonthCountRow(g.Key.Year, g.Key.Month, g.Count()));

    /// <summary>How the library's works are spread across AO3's content ratings.</summary>
    public static IQueryable<RatingCountRow> RatingMix(IQueryable<Work> library) =>
        library
            .GroupBy(w => w.Rating)
            .OrderBy(g => g.Key)
            .Select(g => new RatingCountRow(g.Key, g.Count()));

    /// <inheritdoc cref="Distribution"/>
    public static IQueryable<BucketCountRow> KudosDistribution(IQueryable<Work> library) =>
        Distribution(library.Select(w => w.Kudos), KudosBuckets);

    /// <inheritdoc cref="Distribution"/>
    public static IQueryable<BucketCountRow> WordCountDistribution(IQueryable<Work> library) =>
        Distribution(library.Select(w => w.WordCount), WordCountBuckets);

    /// <summary>
    /// The creators with the most works in the library, richest first, with the kudos those works
    /// have drawn.
    /// </summary>
    /// <remarks>
    /// Anonymous works have no <see cref="WorkAuthor"/> rows at all — see <c>WorkIngestor</c> — so
    /// they drop out here rather than piling up under a creator called "Anonymous".
    /// </remarks>
    /// <remarks>
    /// Tie-broken by kudos and then by pseud id, for the reason <see cref="WorkQueries.Order"/>
    /// tie-breaks the library: ten creators with three works each are ordinary, and without a total
    /// order the database may pick a different ten every time the page is opened.
    /// </remarks>
    public static IQueryable<AuthorStatsRow> TopAuthors(IQueryable<Work> library, int count) =>
        library
            .SelectMany(w => w.Authors, (w, a) => new { a.PseudId, a.Pseud.DisplayName, w.Kudos })
            .GroupBy(x => new { x.PseudId, x.DisplayName })
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Sum(x => (long)x.Kudos))
            .ThenBy(g => g.Key.PseudId)
            .Select(g => new AuthorStatsRow(
                g.Key.PseudId, g.Key.DisplayName, g.Count(), g.Sum(x => (long)x.Kudos)))
            .Take(count);

    /// <summary>
    /// How many of the library's works sit at each reading status, for one reader.
    /// </summary>
    /// <remarks>
    /// The key is a correlated lookup that yields <see cref="ReadingStatus.None"/> where there is no
    /// row, so a work nobody has ever touched counts as unmarked rather than dropping out of the
    /// total. That is the same reading of "unread" the saved-filter predicate takes — see
    /// <see cref="WorkQueries.ApplyFilter"/> — and the reason these counts sum to the library.
    /// </remarks>
    public static IQueryable<StatusCountRow> StatusMix(
        IQueryable<Work> library, IQueryable<UserWorkState> states) =>
        library
            .GroupBy(w => states.Where(s => s.WorkId == w.Id).Select(s => s.Status).FirstOrDefault())
            .OrderBy(g => g.Key)
            .Select(g => new StatusCountRow(g.Key, g.Count()));

    /// <summary>
    /// One row per half-star the caller has actually awarded, carrying what the archive made of the
    /// works they put there — the "my ratings against its reception" lens.
    /// </summary>
    /// <remarks>
    /// A <c>SelectMany</c> over the caller's rated states rather than a left join, so unrated works
    /// contribute nothing: null is "no opinion", and averaging it in as a zero would drag every bar
    /// toward the bottom of the scale.
    /// </remarks>
    public static IQueryable<RatingReceptionRow> RatingsAgainstReception(
        IQueryable<Work> library, IQueryable<UserWorkState> states) =>
        library
            .SelectMany(
                w => states.Where(s => s.WorkId == w.Id && s.Rating != null),
                (w, s) => new { Rating = s.Rating!.Value, w.Kudos, w.WordCount })
            .GroupBy(x => x.Rating)
            .OrderBy(g => g.Key)
            .Select(g => new RatingReceptionRow(
                g.Key,
                g.Count(),
                g.Average(x => (double)x.Kudos),
                g.Average(x => (double)x.WordCount)));

    /// <summary>
    /// Counts <paramref name="values"/> into <paramref name="buckets"/> with a single grouped
    /// query, keyed by bucket index.
    /// </summary>
    /// <remarks>
    /// The <c>CASE</c> is built from <paramref name="buckets"/> rather than written out, which is
    /// the opposite of what <see cref="WorkQueries.ApplyFilter"/> does with a filter's bounds — and
    /// for the reason that rule gives. There, each bound is a user's number and has to reach the
    /// database as a parameter or every distinct bound earns its own query plan. Here the
    /// boundaries are compile-time constants shared by every caller, so there is exactly one plan
    /// whatever happens, and generating the comparisons buys the thing that matters instead: a
    /// bar's label and the comparison that fills it come from one array and cannot drift.
    /// </remarks>
    /// <remarks>
    /// Buckets are half-open upwards: the last one has no <see cref="StatsBucket.Max"/> and catches
    /// everything the earlier ones did not, so no value can fall outside the histogram.
    /// </remarks>
    private static IQueryable<BucketCountRow> Distribution(
        IQueryable<int> values, IReadOnlyList<StatsBucket> buckets)
    {
        Validate(buckets);

        var value = Expression.Parameter(typeof(int), "v");

        // Built inside out: the last bucket is the fall-through, and each earlier one wraps what is
        // there so far in "<= my Max ? me : that".
        Expression index = Expression.Constant(buckets.Count - 1);
        for (var i = buckets.Count - 2; i >= 0; i--)
            index = Expression.Condition(
                Expression.LessThanOrEqual(value, Expression.Constant(buckets[i].Max!.Value)),
                Expression.Constant(i),
                index);

        return values
            .GroupBy(Expression.Lambda<Func<int, int>>(index, value))
            .OrderBy(g => g.Key)
            .Select(g => new BucketCountRow(g.Key, g.Count()));
    }

    /// <summary>
    /// Checks that a bucket array describes the histogram the generated <c>CASE</c> will actually
    /// produce, and throws naming the offending bar if it does not.
    /// </summary>
    /// <remarks>
    /// The comparisons are a chain of <c>&lt;= Max</c>, so a bar catches everything above the
    /// previous bar's <see cref="StatsBucket.Max"/> whatever its own <see cref="StatsBucket.Min"/>
    /// says — and <see cref="StatsBucket.Min"/> is what the DTO reports to the client. That makes
    /// the two ways of writing a wrong array silent rather than loud: closing the top bar leaves
    /// everything above it counted under a label that excludes it, and a gap between two bars files
    /// the values in the gap under a bar whose stated range does not contain them. Both are caught
    /// here instead, which is also what keeps <see cref="StatsBucket.Min"/> load-bearing rather than
    /// decorative.
    /// </remarks>
    internal static void Validate(IReadOnlyList<StatsBucket> buckets)
    {
        if (buckets[^1].Max is not null)
            throw new ArgumentException(
                $"The last bucket catches everything above the one before it, so '{buckets[^1].Label}' "
                + "must be open-ended.",
                nameof(buckets));

        for (var i = 0; i < buckets.Count - 1; i++)
        {
            if (buckets[i].Max is not int max)
                throw new ArgumentException(
                    $"Only the last bucket may be open-ended; '{buckets[i].Label}' is not.",
                    nameof(buckets));

            if (buckets[i + 1].Min != max + 1)
                throw new ArgumentException(
                    $"'{buckets[i + 1].Label}' starts at {buckets[i + 1].Min}, leaving a gap after "
                    + $"'{buckets[i].Label}' that it would silently be counted in.",
                    nameof(buckets));
        }
    }
}

/// <summary>
/// One bar of a histogram: what it is called and the closed range it covers.
/// <paramref name="Max"/> is null on the open-ended last bucket only.
/// </summary>
public sealed record StatsBucket(string Label, int Min, int? Max);

public sealed record WatchedShipRow(int ShipId, string TagName);

public sealed record ShipStatsRow(
    int ShipId,
    int WorkCount,
    long WordCount,
    int MarkedCount,
    int ReadCount,
    int RatedCount);

public sealed record MonthCountRow(int Year, int Month, int WorkCount);

public sealed record RatingCountRow(Ao3Rating Rating, int WorkCount);

public sealed record StatusCountRow(ReadingStatus Status, int WorkCount);

/// <param name="Bucket">Index into the bucket array the query was built from.</param>
public sealed record BucketCountRow(int Bucket, int WorkCount);

public sealed record AuthorStatsRow(int PseudId, string Name, int WorkCount, long Kudos);

public sealed record RatingReceptionRow(
    int Rating, int WorkCount, double AverageKudos, double AverageWordCount);
