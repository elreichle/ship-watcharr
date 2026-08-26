using System.Reflection;
using Ao3Tracker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// Every statistics query, translated by the PostgreSQL provider.
///
/// This repo's tests run on SQLite, and no PostgreSQL server exists in the loop's shell — so
/// without this, "it works on both providers" would be a claim nothing checks until somebody
/// self-hosts on Postgres and opens the page. <c>ToQueryString()</c> compiles the expression tree
/// through the real Npgsql translator without connecting to anything, which turns an untranslatable
/// aggregate from a runtime 500 on someone else's instance into a failure here.
/// </summary>
/// <remarks>
/// It cannot check that the SQL is <em>right</em> — that is what <c>StatsControllerTests</c> does,
/// against a database that actually runs it. What this checks is that there is any SQL at all:
/// EF throws rather than falling back to client evaluation, so a query it cannot translate fails
/// these tests loudly.
/// </remarks>
public class StatsQueryTranslationTests
{
    private const string UserId = "reader-1";

    /// <summary>
    /// A context on a connection string that is never opened. Building one costs nothing and
    /// connects to nothing; the model and the SQL generator are all these tests touch.
    /// </summary>
    private static AppDbContext Postgres() => new PostgresAppDbContext(
        new DbContextOptionsBuilder<PostgresAppDbContext>()
            .UseNpgsql("Host=postgres.invalid;Database=none;Username=none;Password=none")
            .Options);

    public static TheoryData<string> QueryNames() =>
        [.. Queries(Postgres()).Keys];

    [Theory]
    [MemberData(nameof(QueryNames))]
    public void Translates_on_PostgreSQL(string name)
    {
        using var db = Postgres();

        var sql = Queries(db)[name]();

        Assert.Contains("SELECT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Covers_every_query_StatsQueries_offers()
    {
        // A new aggregate on the statistics page that nobody added here would be untranslated code
        // shipping green, and a list maintained by hand is exactly the list that stops being
        // maintained. Read off the class instead, so adding a query without a case fails here.
        using var db = Postgres();

        var offered = typeof(StatsQueries)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType.IsGenericType
                && m.ReturnType.GetGenericTypeDefinition() == typeof(IQueryable<>))
            .Select(m => m.Name);

        // Keys may name a variant of the same query — "PerShip narrowed" beside "PerShip" — so the
        // method is the first word of the key.
        var covered = Queries(db).Keys.Select(k => k.Split(' ')[0]);

        Assert.Equal(offered.Order(), covered.Distinct().Order());
    }

    [Fact]
    public void Buckets_months_in_UTC_rather_than_in_the_database_session_s_timezone()
    {
        // The one place translating is not enough. On PostgreSQL Work.UpdatedAt is a
        // `timestamp with time zone`, and date_part over one of those is evaluated in the session's
        // TimeZone — which nothing in this app pins, so it is whatever the self-hoster's server
        // defaults to. Npgsql normalizes with AT TIME ZONE 'UTC' before extracting the parts, which
        // is what keeps a work revised at 03:00 UTC on the first of a month in that month rather
        // than in the previous one for a reader whose database sits in Chicago.
        //
        // Nothing else here would notice if that stopped being true: the SQL would still compile,
        // still run, and quietly answer differently on two instances holding the same library.
        using var db = Postgres();

        var sql = StatsQueries.WorksByUpdatedMonth(WorkQueries.Library(db, UserId, null)).ToQueryString();

        var extractions = Occurrences(sql, "date_part(");
        Assert.Equal(2, extractions);
        Assert.Equal(extractions, Occurrences(sql, "AT TIME ZONE 'UTC'"));
    }

    private static int Occurrences(string haystack, string needle) =>
        haystack.Split(needle, StringSplitOptions.None).Length - 1;

    private static Dictionary<string, Func<string>> Queries(AppDbContext db)
    {
        var library = WorkQueries.Library(db, UserId, shipId: null);
        var states = WorkQueries.StatesOf(db, UserId);

        return new Dictionary<string, Func<string>>
        {
            ["Read"] = () => StatsQueries.Read(library, states)
                .Select(w => (long)w.WordCount).ToQueryString(),
            ["WatchedShips"] = () => StatsQueries.WatchedShips(db, UserId, shipId: null).ToQueryString(),
            ["WatchedShips narrowed"] = () => StatsQueries.WatchedShips(db, UserId, shipId: 7).ToQueryString(),
            ["PerShip"] = () => StatsQueries.PerShip(db, UserId, shipId: null).ToQueryString(),
            ["PerShip narrowed"] = () => StatsQueries.PerShip(db, UserId, shipId: 7).ToQueryString(),
            ["WorksByUpdatedMonth"] = () => StatsQueries.WorksByUpdatedMonth(library).ToQueryString(),
            ["RatingMix"] = () => StatsQueries.RatingMix(library).ToQueryString(),
            ["KudosDistribution"] = () => StatsQueries.KudosDistribution(library).ToQueryString(),
            ["WordCountDistribution"] = () => StatsQueries.WordCountDistribution(library).ToQueryString(),
            ["TopAuthors"] = () => StatsQueries.TopAuthors(library, StatsQueries.TopAuthorCount).ToQueryString(),
            ["StatusMix"] = () => StatsQueries.StatusMix(library, states).ToQueryString(),
            ["RatingsAgainstReception"] = () => StatsQueries.RatingsAgainstReception(library, states).ToQueryString(),
        };
    }
}

/// <summary>
/// The rules a histogram's bucket array has to obey for the labels this API publishes to describe
/// the <c>CASE</c> it generates.
/// </summary>
/// <remarks>
/// Both shipped arrays obey them, so nothing in the app can reach these throws today. They are here
/// for the array somebody edits later: the comparisons are a chain of <c>&lt;= Max</c>, so a wrong
/// array does not fail — it counts works under a label whose stated range excludes them.
/// </remarks>
public class StatsBucketTests
{
    [Fact]
    public void Accepts_the_arrays_the_statistics_page_actually_uses()
    {
        StatsQueries.Validate(StatsQueries.KudosBuckets);
        StatsQueries.Validate(StatsQueries.WordCountBuckets);
    }

    [Fact]
    public void Refuses_a_closed_top_bucket()
    {
        // Everything above the second bar falls into it whatever its Max says, so a work of a
        // million words would be counted under a bar labelled as ending at 999.
        var closed = new StatsBucket[] { new("0-9", 0, 9), new("10-999", 10, 999) };

        var error = Assert.Throws<ArgumentException>(() => StatsQueries.Validate(closed));

        Assert.Contains("10-999", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_bucket_that_is_open_ended_before_the_end()
    {
        var early = new StatsBucket[] { new("anything", 0, null), new("10+", 10, null) };

        var error = Assert.Throws<ArgumentException>(() => StatsQueries.Validate(early));

        Assert.Contains("anything", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_gap_between_two_buckets()
    {
        // 10 through 19 belong to no bar's stated range, and the chain of comparisons files them
        // under "20+" — a bar the client is told starts at 20.
        var gapped = new StatsBucket[] { new("0-9", 0, 9), new("20+", 20, null) };

        var error = Assert.Throws<ArgumentException>(() => StatsQueries.Validate(gapped));

        Assert.Contains("gap", error.Message, StringComparison.Ordinal);
    }
}
