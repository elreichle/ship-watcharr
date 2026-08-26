using Ao3Tracker.Api.Models;

namespace Ao3Tracker.Api.Dtos;

/// <summary>
/// The body of <c>GET /api/stats</c>: two lenses over the same works.
/// </summary>
/// <remarks>
/// Nothing here is stored. Every number is an aggregate the database computed over the caller's
/// library at request time, which is what keeps statistics from being a third copy of the work
/// table that can go stale — see <c>StatsQueries</c>.
/// </remarks>
/// <param name="ShipId">The ship the whole body was narrowed to, or null for the caller's entire
/// library. Echoed back so a response is legible on its own.</param>
/// <param name="Ships">Where the two lenses meet: one row per watched ship carrying both the size
/// of its corpus and how much of it this reader has marked. One list rather than a corpus list and
/// a reading list, because a share needs its numerator and denominator in the same row.</param>
public record StatsDto(
    int? ShipId,
    IReadOnlyList<ShipStatsDto> Ships,
    CorpusStatsDto Corpus,
    ReadingStatsDto Reading);

/// <param name="WorkCount">Works in this ship's corpus, counted the way the library list counts
/// them. Zero for a followed ship nothing has been scraped into yet, which is a state the page
/// exists to explain rather than one to hide.</param>
/// <param name="MarkedCount">Works the reader has given any status other than
/// <see cref="ReadingStatus.None"/>.</param>
public record ShipStatsDto(
    int ShipId,
    string TagName,
    int WorkCount,
    long WordCount,
    int MarkedCount,
    int ReadCount,
    int RatedCount);

/// <summary>
/// The corpus as it stands, with nobody's reading in it.
/// </summary>
/// <param name="AverageKudos">Null for an empty library — the honest answer where the alternative
/// is a division by zero or a zero that reads as "nobody left kudos".</param>
/// <param name="WorksByUpdatedMonth">Works per calendar month of their last revision, ascending,
/// carrying only the months that have any. See <c>StatsQueries.WorksByUpdatedMonth</c> for why the
/// gaps are the caller's to fill.</param>
/// <param name="RatingMix">Every AO3 content rating, zero-count ones included, in AO3's own order
/// of explicitness.</param>
/// <param name="TopAuthors">The most prolific creators in the library. Anonymous works have no
/// creator and are absent rather than pooled.</param>
public record CorpusStatsDto(
    int WorkCount,
    int CompleteCount,
    long WordCount,
    long Kudos,
    double? AverageKudos,
    double? AverageWordCount,
    IReadOnlyList<MonthCountDto> WorksByUpdatedMonth,
    IReadOnlyList<LabelledCountDto> RatingMix,
    IReadOnlyList<BucketCountDto> KudosDistribution,
    IReadOnlyList<BucketCountDto> WordCountDistribution,
    IReadOnlyList<AuthorStatsDto> TopAuthors);

/// <summary>
/// The caller's own reading, laid over the corpus above. PER-USER, and written by nothing but them.
/// </summary>
/// <param name="StatusMix">Every <see cref="ReadingStatus"/>, zero-count ones included. These sum
/// to <c>Corpus.WorkCount</c>: a work nobody has touched counts under "None" rather than dropping
/// out, which is the same reading of "unread" the saved filters take.</param>
/// <param name="ReadWordCount">Words in the works marked Read — "how much of this have I actually
/// been through", which a work count alone does not answer.</param>
/// <param name="AverageRating">The caller's mean half-star score over the works they rated. Null
/// where they have rated nothing.</param>
/// <param name="RatingsAgainstReception">One row per half-star the caller has awarded, with what
/// the archive made of the works they put there. Unrated works are absent, not counted as zero.</param>
public record ReadingStatsDto(
    int MarkedCount,
    int ReadCount,
    long ReadWordCount,
    int RatedCount,
    double? AverageRating,
    IReadOnlyList<LabelledCountDto> StatusMix,
    IReadOnlyList<RatingReceptionDto> RatingsAgainstReception);

public record MonthCountDto(int Year, int Month, int WorkCount);

/// <param name="Label">For a reading status, the <see cref="ReadingStatus"/> name — enum names on
/// the wire, as everywhere else on this API. For a content rating, AO3's own wording.</param>
public record LabelledCountDto(string Label, int WorkCount);

/// <param name="Max">Null on the open-ended top bucket. The bounds ride along with the label so a
/// client can format its own axis without knowing this server's wording.</param>
public record BucketCountDto(string Label, int Min, int? Max, int WorkCount);

public record AuthorStatsDto(int PseudId, string Name, int WorkCount, long Kudos);

/// <param name="Rating">Half-stars, 1-10, so 7 is three and a half — the same scale
/// <c>WorkStateDto.Rating</c> uses.</param>
public record RatingReceptionDto(
    int Rating, int WorkCount, double AverageKudos, double AverageWordCount);
