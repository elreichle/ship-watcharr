using System.Security.Claims;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// Statistics over the caller's library: the corpus as it stands, and their own reading laid over
/// it.
/// </summary>
/// <remarks>
/// Read-only and derived — no schema of its own, and nothing here is ever written back. Every
/// figure is an aggregate the database computes at request time over exactly the works
/// <c>GET /api/works</c> would list, so the two can never disagree about what the library is.
/// </remarks>
/// <remarks>
/// Scoped to the ships the caller watches, like the rest of the library API. A reader who has
/// unfollowed a ship keeps their reading state on its works — see <c>UnwatchShip</c> — but it stops
/// being reachable here, which is the same answer the feed gives.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/stats")]
public class StatsController : ControllerBase
{
    private readonly AppDbContext _db;

    public StatsController(AppDbContext db)
    {
        _db = db;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request missing user id claim.");

    /// <param name="shipId">Narrow every figure to one watched ship. 404s if the caller does not
    /// watch it — the same rule the works list applies, and what stops this being a way to measure
    /// somebody else's library by guessing ship numbers.</param>
    [HttpGet]
    public async Task<ActionResult<StatsDto>> GetStats(
        [FromQuery] int? shipId = null,
        CancellationToken ct = default)
    {
        var userId = CurrentUserId;

        if (shipId is int requested
            && !await WorkQueries.WatchedShipIdsOf(_db, userId).ContainsAsync(requested, ct))
        {
            return NotFound();
        }

        var library = WorkQueries.Library(_db, userId, shipId);
        var myStates = WorkQueries.StatesOf(_db, userId);

        // Deliberately several small aggregates rather than one fused GROUP BY: each of these
        // translates unambiguously on both providers, which a single aggregate over a constant
        // grouping key does not — PostgreSQL reads a bare `GROUP BY 1` as an ordinal reference to
        // the first output column. They run against a local database on a page nobody polls.
        var workCount = await library.CountAsync(ct);
        var completeCount = await library.CountAsync(w => w.IsComplete, ct);
        var wordCount = await library.SumAsync(w => (long)w.WordCount, ct);
        var kudos = await library.SumAsync(w => (long)w.Kudos, ct);
        var readWordCount = await StatsQueries.Read(library, myStates).SumAsync(w => (long)w.WordCount, ct);

        var watchedShips = await StatsQueries.WatchedShips(_db, userId, shipId).ToListAsync(ct);
        var shipStats = await StatsQueries.PerShip(_db, userId, shipId).ToListAsync(ct);
        var months = await StatsQueries.WorksByUpdatedMonth(library).ToListAsync(ct);
        var ratings = await StatsQueries.RatingMix(library).ToListAsync(ct);
        var kudosBuckets = await StatsQueries.KudosDistribution(library).ToListAsync(ct);
        var wordBuckets = await StatsQueries.WordCountDistribution(library).ToListAsync(ct);
        var authors = await StatsQueries.TopAuthors(library, StatsQueries.TopAuthorCount).ToListAsync(ct);
        var statuses = await StatsQueries.StatusMix(library, myStates).ToListAsync(ct);
        var reception = await StatsQueries.RatingsAgainstReception(library, myStates).ToListAsync(ct);

        var byStatus = statuses.ToDictionary(r => r.Status, r => r.WorkCount);
        var ratedCount = reception.Sum(r => r.WorkCount);

        var corpus = new CorpusStatsDto(
            workCount,
            completeCount,
            wordCount,
            kudos,
            Mean(kudos, workCount),
            Mean(wordCount, workCount),
            [.. months.Select(m => new MonthCountDto(m.Year, m.Month, m.WorkCount))],
            Spread(ratings.ToDictionary(r => r.Rating, r => r.WorkCount), Ao3Labels.Describe),
            Histogram(StatsQueries.KudosBuckets, kudosBuckets),
            Histogram(StatsQueries.WordCountBuckets, wordBuckets),
            [.. authors.Select(a => new AuthorStatsDto(a.PseudId, a.Name, a.WorkCount, a.Kudos))]);

        var reading = new ReadingStatsDto(
            byStatus.Where(s => s.Key != ReadingStatus.None).Sum(s => s.Value),
            byStatus.GetValueOrDefault(ReadingStatus.Read),
            readWordCount,
            ratedCount,
            // Weighted by how many works sit at each half-star, since a row is a bucket rather than
            // a work. Null rather than zero for a reader who has rated nothing: an unrated library
            // has no mean, and zero is not even on the scale.
            ratedCount == 0 ? null : (double)reception.Sum(r => (long)r.Rating * r.WorkCount) / ratedCount,
            Spread(byStatus, s => s.ToString()),
            [.. reception.Select(r => new RatingReceptionDto(
                r.Rating, r.WorkCount, r.AverageKudos, r.AverageWordCount))]);

        // The subscriptions decide which rows exist and the aggregate only fills them in, so a
        // followed ship the scraper has not reached yet is listed at zero rather than missing from
        // the page that was supposed to account for it.
        var byShip = shipStats.ToDictionary(s => s.ShipId);

        var ships = watchedShips.Select(w => byShip.TryGetValue(w.ShipId, out var s)
            ? new ShipStatsDto(
                w.ShipId, w.TagName, s.WorkCount, s.WordCount, s.MarkedCount, s.ReadCount, s.RatedCount)
            : new ShipStatsDto(w.ShipId, w.TagName, 0, 0, 0, 0, 0));

        return Ok(new StatsDto(shipId, [.. ships], corpus, reading));
    }

    /// <summary>
    /// An average, or null where there is nothing to average. Computed here from two totals the
    /// database already returned rather than as a sixth aggregate — the arithmetic is exact either
    /// way, and this is the form in which "no works" has an answer that is not a division by zero.
    /// </summary>
    private static double? Mean(long total, int count) => count == 0 ? null : (double)total / count;

    /// <summary>
    /// Counts across every value of a fixed vocabulary, in the enum's own order, including the ones
    /// no work landed on.
    /// </summary>
    /// <remarks>
    /// The zeros are the point. A grouped query returns only the values present, and a mix that
    /// silently omits "Dropped" reads as a reader who has never dropped anything being asked about
    /// a different set of statuses from one who has.
    /// </remarks>
    private static IReadOnlyList<LabelledCountDto> Spread<TEnum>(
        IReadOnlyDictionary<TEnum, int> counts, Func<TEnum, string> label)
        where TEnum : struct, Enum =>
        [.. Enum.GetValues<TEnum>().Select(v => new LabelledCountDto(label(v), counts.GetValueOrDefault(v)))];

    /// <inheritdoc cref="Spread"/>
    private static IReadOnlyList<BucketCountDto> Histogram(
        IReadOnlyList<StatsBucket> buckets, IReadOnlyList<BucketCountRow> rows)
    {
        var counts = rows.ToDictionary(r => r.Bucket, r => r.WorkCount);

        return [.. buckets.Select((b, i) =>
            new BucketCountDto(b.Label, b.Min, b.Max, counts.GetValueOrDefault(i)))];
    }
}
