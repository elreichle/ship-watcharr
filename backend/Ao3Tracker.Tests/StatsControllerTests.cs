using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace Ao3Tracker.Tests;

/// <summary>
/// The statistics endpoint, over a real SQLite database rather than a mocked context — every figure
/// here is decided by a GROUP BY, so a mock would only ever agree with the test's own arithmetic.
///
/// Three things carry the risk and take most of the coverage: whose library is being measured, what
/// an empty one answers, and whether a bar's label still describes the comparison that filled it.
/// </summary>
public class StatsControllerTests : IDisposable
{
    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- whose library ---------------------------------------------------------------------------

    [Fact]
    public async Task Counts_only_the_works_of_ships_you_follow()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1, 2);
        await SeedWorksAsync(await SeedShipAsync("Kirk/Spock", sam), 3);

        var stats = await StatsAsync(emma);

        Assert.Equal(2, stats.Corpus.WorkCount);
        Assert.Equal(["Clarke Griffin/Lexa"], stats.Ships.Select(s => s.TagName));
    }

    [Fact]
    public async Task Excludes_works_AO3_has_deleted()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, 1);
        await SeedWorksAsync(lexa, w => w.IsDeleted = true, 2);

        var stats = await StatsAsync(emma);

        Assert.Equal(1, stats.Corpus.WorkCount);
        Assert.Equal(1, stats.Ships.Single().WorkCount);
    }

    [Fact]
    public async Task Counts_a_crossover_once_overall_and_under_both_of_its_ships()
    {
        // The corpus is a set of works; the per-ship rows are a set per ship. A work carrying two
        // watched relationship tags belongs to both, so the rows deliberately do not sum to the
        // total — and the total is what must not double-count.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        var kirk = await SeedShipAsync("Kirk/Spock", emma);
        await SeedWorksAsync(lexa, w => w.WordCount = 100, 1);
        await LinkAsync(kirk, 1);

        var stats = await StatsAsync(emma);

        Assert.Equal(1, stats.Corpus.WorkCount);
        Assert.Equal(100, stats.Corpus.WordCount);
        Assert.Equal([1, 1], stats.Ships.Select(s => s.WorkCount));
    }

    [Fact]
    public async Task Reads_only_your_own_reading()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await WatchAsync("Clarke Griffin/Lexa", sam);
        await SeedWorksAsync(lexa, 1);
        await MarkAsync(sam, 1, status: nameof(ReadingStatus.Read), rating: 10);

        var stats = await StatsAsync(emma);

        Assert.Equal(0, stats.Reading.ReadCount);
        Assert.Equal(0, stats.Reading.RatedCount);
        Assert.Null(stats.Reading.AverageRating);
        Assert.Empty(stats.Reading.RatingsAgainstReception);
        Assert.Equal(0, stats.Ships.Single().ReadCount);
    }

    // ---- an empty library ------------------------------------------------------------------------

    [Fact]
    public async Task Answers_a_user_who_follows_nothing_without_dividing_by_zero()
    {
        var emma = _host.SeedUser();

        var stats = await StatsAsync(emma);

        Assert.Equal(0, stats.Corpus.WorkCount);
        Assert.Equal(0, stats.Corpus.WordCount);
        Assert.Equal(0, stats.Corpus.Kudos);
        Assert.Null(stats.Corpus.AverageKudos);
        Assert.Null(stats.Corpus.AverageWordCount);
        Assert.Null(stats.Reading.AverageRating);
        Assert.Empty(stats.Ships);
        Assert.Empty(stats.Corpus.WorksByUpdatedMonth);
        Assert.Empty(stats.Corpus.TopAuthors);
    }

    [Fact]
    public async Task Still_names_every_bucket_and_status_for_an_empty_library()
    {
        // The vocabularies are fixed and the zeros are information. A page handed nothing at all
        // cannot tell "no works are Explicit" from "this build forgot about Explicit".
        var emma = _host.SeedUser();

        var stats = await StatsAsync(emma);

        Assert.Equal(
            StatsQueries.KudosBuckets.Select(b => b.Label),
            stats.Corpus.KudosDistribution.Select(b => b.Label));
        Assert.All(stats.Corpus.KudosDistribution, b => Assert.Equal(0, b.WorkCount));
        Assert.Equal(
            StatsQueries.WordCountBuckets.Select(b => b.Label),
            stats.Corpus.WordCountDistribution.Select(b => b.Label));
        Assert.Equal(
            ["None", "ToRead", "Reading", "Read", "Dropped"],
            stats.Reading.StatusMix.Select(s => s.Label));
        Assert.Equal(
            Enum.GetValues<Ao3Rating>().Select(Ao3Labels.Describe),
            stats.Corpus.RatingMix.Select(r => r.Label));
    }

    [Fact]
    public async Task Names_a_followed_ship_with_no_works_at_zero()
    {
        // The state a fresh install sits in while the AO3-login gate holds every job. A ship that
        // disappeared from this page for having nothing in it would take its own explanation with it.
        var emma = _host.SeedUser();
        await SeedShipAsync("Clarke Griffin/Lexa", emma);

        var stats = await StatsAsync(emma);

        var ship = Assert.Single(stats.Ships);
        Assert.Equal("Clarke Griffin/Lexa", ship.TagName);
        Assert.Equal(0, ship.WorkCount);
        Assert.Equal(0, ship.WordCount);
    }

    // ---- narrowing to one ship -------------------------------------------------------------------

    [Fact]
    public async Task Narrows_every_figure_to_one_ship()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        var kirk = await SeedShipAsync("Kirk/Spock", emma);
        await SeedWorksAsync(lexa, w => w.WordCount = 100, 1, 2);
        await SeedWorksAsync(kirk, w => w.WordCount = 500, 3);

        var stats = await StatsAsync(emma, kirk);

        Assert.Equal(kirk, stats.ShipId);
        Assert.Equal(1, stats.Corpus.WorkCount);
        Assert.Equal(500, stats.Corpus.WordCount);
        Assert.Equal("Kirk/Spock", Assert.Single(stats.Ships).TagName);
    }

    [Fact]
    public async Task Refuses_a_ship_you_do_not_follow()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var kirk = await SeedShipAsync("Kirk/Spock", sam);

        var result = await _host.Stats(emma).GetStats(kirk, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- the corpus ------------------------------------------------------------------------------

    [Fact]
    public async Task Sums_the_corpus_and_averages_it()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => { w.WordCount = 1000; w.Kudos = 30; w.IsComplete = true; }, 1);
        await SeedWorksAsync(lexa, w => { w.WordCount = 3000; w.Kudos = 70; }, 2);

        var stats = await StatsAsync(emma);

        Assert.Equal(4000, stats.Corpus.WordCount);
        Assert.Equal(100, stats.Corpus.Kudos);
        Assert.Equal(1, stats.Corpus.CompleteCount);
        Assert.Equal(50d, stats.Corpus.AverageKudos);
        Assert.Equal(2000d, stats.Corpus.AverageWordCount);
    }

    [Fact]
    public async Task Groups_works_by_the_month_they_were_last_revised()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.UpdatedAt = Utc(2025, 11), 1, 2);
        await SeedWorksAsync(lexa, w => w.UpdatedAt = Utc(2026, 3), 3);

        var stats = await StatsAsync(emma);

        Assert.Equal(
            [(2025, 11, 2), (2026, 3, 1)],
            stats.Corpus.WorksByUpdatedMonth.Select(m => (m.Year, m.Month, m.WorkCount)));
    }

    [Fact]
    public async Task Counts_every_content_rating_it_has_and_zero_for_the_rest()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.Rating = Ao3Rating.Mature, 1, 2);
        await SeedWorksAsync(lexa, w => w.Rating = Ao3Rating.Explicit, 3);

        var mix = (await StatsAsync(emma)).Corpus.RatingMix
            .ToDictionary(r => r.Label, r => r.WorkCount);

        Assert.Equal(2, mix["Mature"]);
        Assert.Equal(1, mix["Explicit"]);
        Assert.Equal(0, mix["General Audiences"]);
    }

    [Theory]
    [InlineData(0, "0-9")]
    [InlineData(9, "0-9")]
    [InlineData(10, "10-49")]
    [InlineData(49, "10-49")]
    [InlineData(50, "50-99")]
    [InlineData(100, "100-499")]
    [InlineData(500, "500-999")]
    [InlineData(999, "500-999")]
    [InlineData(1000, "1,000-4,999")]
    [InlineData(4999, "1,000-4,999")]
    [InlineData(5000, "5,000+")]
    [InlineData(int.MaxValue, "5,000+")]
    public async Task Puts_a_kudos_count_in_the_bucket_its_label_claims(int kudos, string label)
    {
        // The bar's label and the SQL CASE that fills it are generated from one array. This is what
        // says the generation is right, and what would catch a boundary drifting by one.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), w => w.Kudos = kudos, 1);

        var buckets = (await StatsAsync(emma)).Corpus.KudosDistribution;

        Assert.Equal(label, Assert.Single(buckets, b => b.WorkCount == 1).Label);
    }

    [Theory]
    [InlineData(0, "under 1,000")]
    [InlineData(999, "under 1,000")]
    [InlineData(1000, "1,000-4,999")]
    [InlineData(9999, "5,000-9,999")]
    [InlineData(10000, "10,000-29,999")]
    [InlineData(49999, "30,000-49,999")]
    [InlineData(50000, "50,000-99,999")]
    [InlineData(100000, "100,000+")]
    public async Task Puts_a_word_count_in_the_bucket_its_label_claims(int words, string label)
    {
        var emma = _host.SeedUser();
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), w => w.WordCount = words, 1);

        var buckets = (await StatsAsync(emma)).Corpus.WordCountDistribution;

        Assert.Equal(label, Assert.Single(buckets, b => b.WorkCount == 1).Label);
    }

    [Fact]
    public async Task Reports_the_bounds_of_the_bucket_beside_its_label()
    {
        var emma = _host.SeedUser();

        var buckets = (await StatsAsync(emma)).Corpus.KudosDistribution;

        Assert.Equal((0, 9), (buckets[0].Min, buckets[0].Max));
        Assert.Equal((5000, null), (buckets[^1].Min, buckets[^1].Max));
    }

    [Fact]
    public async Task Ranks_the_most_prolific_creators_first()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.Kudos = 5, 1, 2, 3);
        await SeedWorksAsync(lexa, w => w.Kudos = 900, 4);
        var prolific = await SeedPseudAsync("prolific");
        var occasional = await SeedPseudAsync("occasional");
        await CreditAsync(prolific, 1, 2, 3);
        await CreditAsync(occasional, 4);

        var authors = (await StatsAsync(emma)).Corpus.TopAuthors;

        Assert.Equal(["prolific", "occasional"], authors.Select(a => a.Name));
        Assert.Equal([3, 1], authors.Select(a => a.WorkCount));
        Assert.Equal([15L, 900L], authors.Select(a => a.Kudos));
    }

    [Fact]
    public async Task Names_no_more_creators_than_the_list_is_for()
    {
        // "Most prolific" is a top ten, not the byline of every author in the library — which on a
        // real instance is thousands of rows nobody asked for riding on every page load.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        var ids = Enumerable.Range(1, StatsQueries.TopAuthorCount + 1).Select(i => (long)i).ToArray();
        await SeedWorksAsync(lexa, ids);
        foreach (var id in ids) await CreditAsync(await SeedPseudAsync($"author{id}"), id);

        var authors = (await StatsAsync(emma)).Corpus.TopAuthors;

        Assert.Equal(StatsQueries.TopAuthorCount, authors.Count);
    }

    [Fact]
    public async Task Leaves_an_anonymous_work_out_of_the_creator_ranking()
    {
        // An anonymous blurb names nobody, so WorkIngestor writes no author row at all. Nothing
        // here may invent one — a creator called "Anonymous" would top the list on every instance.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.IsAnonymous = true, 1);

        var stats = await StatsAsync(emma);

        Assert.Equal(1, stats.Corpus.WorkCount);
        Assert.Empty(stats.Corpus.TopAuthors);
    }

    // ---- your reading over it --------------------------------------------------------------------

    [Fact]
    public async Task Counts_a_work_nobody_has_touched_as_unmarked_rather_than_dropping_it()
    {
        // The status mix has to sum to the corpus, which is only true if the absence of a state row
        // reads as None. It is the same reading of "unread" the saved filters take.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, 1, 2, 3);
        await MarkAsync(emma, 1, status: nameof(ReadingStatus.Read));

        var stats = await StatsAsync(emma);

        Assert.Equal(stats.Corpus.WorkCount, stats.Reading.StatusMix.Sum(s => s.WorkCount));
        Assert.Equal(2, stats.Reading.StatusMix.Single(s => s.Label == "None").WorkCount);
        Assert.Equal(1, stats.Reading.MarkedCount);
        Assert.Equal(1, stats.Reading.ReadCount);
    }

    [Fact]
    public async Task Counts_a_status_cleared_beside_a_kept_rating_as_unmarked()
    {
        // Clearing a status while a rating stands leaves the row behind saying None. Both shapes of
        // "not marked" have to land in the same place, or the mix stops summing to the corpus.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, 1);
        await MarkAsync(emma, 1, rating: 8);

        var stats = await StatsAsync(emma);

        Assert.Equal(1, stats.Reading.StatusMix.Single(s => s.Label == "None").WorkCount);
        Assert.Equal(0, stats.Reading.MarkedCount);
        Assert.Equal(1, stats.Reading.RatedCount);
    }

    [Fact]
    public async Task Sums_the_words_of_what_you_have_marked_read()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.WordCount = 1200, 1);
        await SeedWorksAsync(lexa, w => w.WordCount = 8000, 2);
        await MarkAsync(emma, 1, status: nameof(ReadingStatus.Read));
        await MarkAsync(emma, 2, status: nameof(ReadingStatus.Reading));

        var stats = await StatsAsync(emma);

        Assert.Equal(1200, stats.Reading.ReadWordCount);
    }

    [Fact]
    public async Task Lays_your_half_stars_against_what_the_archive_made_of_them()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => { w.Kudos = 10; w.WordCount = 100; }, 1);
        await SeedWorksAsync(lexa, w => { w.Kudos = 30; w.WordCount = 300; }, 2);
        await SeedWorksAsync(lexa, w => { w.Kudos = 900; w.WordCount = 900; }, 3);
        await MarkAsync(emma, 1, rating: 4);
        await MarkAsync(emma, 2, rating: 4);
        await MarkAsync(emma, 3, rating: 10);

        var rows = (await StatsAsync(emma)).Reading.RatingsAgainstReception;

        Assert.Equal([4, 10], rows.Select(r => r.Rating));
        Assert.Equal([2, 1], rows.Select(r => r.WorkCount));
        Assert.Equal([20d, 900d], rows.Select(r => r.AverageKudos));
        Assert.Equal([200d, 900d], rows.Select(r => r.AverageWordCount));
    }

    [Fact]
    public async Task Leaves_an_unrated_work_out_of_the_rating_lens_rather_than_scoring_it_zero()
    {
        // Null means "no opinion", and averaging it in as a zero would drag every figure toward the
        // bottom of a scale that starts at one — off the scale, in fact.
        //
        // Work 3 is the case that matters and the one an absent row does not cover: marking a work
        // read without scoring it leaves a state row behind whose Rating is null. A lens reading
        // that as a zero would answer 2 here, and would do it to every reader who marks more than
        // they rates.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, 1, 2, 3);
        await MarkAsync(emma, 1, rating: 6);
        await MarkAsync(emma, 3, status: nameof(ReadingStatus.Read));

        var stats = await StatsAsync(emma);

        Assert.Equal(1, stats.Reading.RatedCount);
        Assert.Equal(6d, stats.Reading.AverageRating);
        Assert.Equal(6, Assert.Single(stats.Reading.RatingsAgainstReception).Rating);
    }

    [Fact]
    public async Task Weights_your_average_rating_by_how_many_works_sit_at_each_half_star()
    {
        // The lens groups by score, so a row is a bucket rather than a work. An unweighted mean over
        // those rows would answer 6 here instead of 4.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, 1, 2, 3);
        await MarkAsync(emma, 1, rating: 2);
        await MarkAsync(emma, 2, rating: 2);
        await MarkAsync(emma, 3, rating: 8);

        var stats = await StatsAsync(emma);

        Assert.Equal(4d, stats.Reading.AverageRating);
    }

    [Fact]
    public async Task Counts_a_ships_read_share_against_that_ships_own_total()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        var kirk = await SeedShipAsync("Kirk/Spock", emma);
        await SeedWorksAsync(lexa, 1, 2, 3, 4);
        await SeedWorksAsync(kirk, 5, 6);
        await MarkAsync(emma, 1, status: nameof(ReadingStatus.Read));
        await MarkAsync(emma, 2, status: nameof(ReadingStatus.Read));
        await MarkAsync(emma, 5, status: nameof(ReadingStatus.Dropped), rating: 3);

        var ships = (await StatsAsync(emma)).Ships.ToDictionary(s => s.TagName);

        Assert.Equal((4, 2, 2, 0), Shape(ships["Clarke Griffin/Lexa"]));
        Assert.Equal((2, 0, 1, 1), Shape(ships["Kirk/Spock"]));

        static (int, int, int, int) Shape(ShipStatsDto s) =>
            (s.WorkCount, s.ReadCount, s.MarkedCount, s.RatedCount);
    }

    [Fact]
    public async Task Orders_the_ships_the_same_way_whatever_case_their_tags_are_in()
    {
        // Ordering on the rendered tag name would put these in one order on SQLite and another on
        // PostgreSQL — the reason nothing in this app sorts by display text. See WorkQueries.Order.
        // Names chosen so the two orderings disagree: SQLite's default collation is binary, which
        // puts every uppercase letter before every lowercase one, so "Beta/Gamma" would come first.
        var emma = _host.SeedUser();
        await SeedShipAsync("Beta/Gamma", emma);
        await SeedShipAsync("alpha/delta", emma);

        var stats = await StatsAsync(emma);

        Assert.Equal(["alpha/delta", "Beta/Gamma"], stats.Ships.Select(s => s.TagName));
    }

    // ---- fixture ---------------------------------------------------------------------------------

    private static DateTime Utc(int year, int month) => new(year, month, 15, 0, 0, 0, DateTimeKind.Utc);

    private async Task<StatsDto> StatsAsync(ApplicationUser user, int? shipId = null) =>
        Assert.IsType<StatsDto>(
            Assert.IsType<OkObjectResult>((await _host.Stats(user).GetStats(shipId, default)).Result).Value);

    /// <summary>Creates a ship through the real endpoint, so its schedule is wired up too.</summary>
    private async Task<int> SeedShipAsync(string tagName, ApplicationUser watcher)
    {
        var result = await _host.Ships(watcher).WatchShip(new(tagName), default);
        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    /// <summary>A second reader following a ship that already exists.</summary>
    private Task WatchAsync(string tagName, ApplicationUser watcher) =>
        _host.Ships(watcher).WatchShip(new(tagName), default);

    private Task SeedWorksAsync(int shipId, params long[] ids) => SeedWorksAsync(shipId, _ => { }, ids);

    private async Task SeedWorksAsync(int shipId, Action<Work> customize, params long[] ids)
    {
        await using var db = _host.NewContext();

        foreach (var id in ids)
        {
            var work = new Work { Id = id, Title = $"Work {id}", UpdatedAt = Utc(2026, 1) };
            customize(work);
            db.Works.Add(work);
            db.ShipWorks.Add(new ShipWork { ShipId = shipId, WorkId = id });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Records an existing work as also appearing under a second ship's index.</summary>
    private async Task LinkAsync(int shipId, long workId)
    {
        await using var db = _host.NewContext();
        db.ShipWorks.Add(new ShipWork { ShipId = shipId, WorkId = workId });
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedPseudAsync(string name)
    {
        await using var db = _host.NewContext();

        var pseud = new Ao3Pseud
        {
            Username = name,
            PseudName = name,
            UsernameNormalized = name.ToUpperInvariant(),
            PseudNameNormalized = name.ToUpperInvariant(),
            DisplayName = name,
            DisplayNameNormalized = name.ToUpperInvariant(),
        };
        db.Ao3Pseuds.Add(pseud);
        await db.SaveChangesAsync();

        return pseud.Id;
    }

    private async Task CreditAsync(int pseudId, params long[] workIds)
    {
        await using var db = _host.NewContext();
        foreach (var workId in workIds)
            db.WorkAuthors.Add(new WorkAuthor { PseudId = pseudId, WorkId = workId });
        await db.SaveChangesAsync();
    }

    /// <summary>Writes reading state through the endpoint that owns it, not straight into the table.</summary>
    private async Task MarkAsync(ApplicationUser user, long workId, string? status = null, int? rating = null)
    {
        var result = await _host.NewWorksRequest(user).SetWorkState(workId, new(status, rating), default);
        Assert.IsType<OkObjectResult>(result.Result);
    }
}
