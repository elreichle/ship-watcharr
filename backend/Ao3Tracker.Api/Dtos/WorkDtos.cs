using Ao3Tracker.Api.Models;

namespace Ao3Tracker.Api.Dtos;

/// <summary>One page of a larger result set, plus enough context to render a pager.</summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

/// <summary>
/// A work as it appears in the library list.
/// </summary>
/// <remarks>
/// Carries no summary. <c>Work.SummaryHtml</c> is raw archive HTML that has to be sanitized where
/// it is rendered, and shipping it to a client with no sanitizer is how it ends up injected — so
/// it stays out of the list DTO until the detail view brings one.
/// </remarks>
/// <param name="Rating">AO3's own label, e.g. "Teen And Up Audiences".</param>
/// <param name="Categories">Expanded from the flags column, e.g. ["F/F", "Gen"].</param>
/// <param name="Warnings">Expanded from the flags column, e.g. ["Major Character Death"].</param>
/// <param name="PlannedChapterCount">Null for an open-ended WIP — AO3's "?" — which is a
/// different thing from a work whose planned total equals its current count.</param>
/// <param name="UpdatedAtIsApproximate">True when only the day-granular date was available, so
/// the UI can avoid implying a precision it does not have.</param>
/// <param name="Ships">The watched tags this work turned up under and still appears in. Restricted
/// to the reader's own subscriptions: which other ships an instance tracks is not this user's
/// business.</param>
/// <param name="LeftShips">The watched tags whose listing has stopped carrying it, as concluded by
/// a completed full sweep. Normally empty: a row is on the list despite one of these only because
/// another watched tag still carries the work, or because the reader has marked it — see
/// <c>WorkQueries.Library</c> — and this field is what says so rather than leaving a work AO3 no
/// longer files under the tag looking as though it does.</param>
/// <param name="State">The caller's own reading status, rating, note and favorite mark — never another reader's,
/// and never absent: a work nobody has touched carries <see cref="WorkStateDto.Cleared"/>. It rides
/// on the row so a page of the feed costs one request rather than one per work.</param>
public record WorkListItemDto(
    long Id,
    string Title,
    IReadOnlyList<string> Authors,
    bool IsAnonymous,
    string Rating,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Fandoms,
    IReadOnlyList<string> Ships,
    IReadOnlyList<string> LeftShips,
    bool IsComplete,
    int WordCount,
    int ChapterCount,
    int? PlannedChapterCount,
    int Kudos,
    int Hits,
    int Bookmarks,
    int CommentCount,
    string? LanguageName,
    DateTime UpdatedAt,
    bool UpdatedAtIsApproximate,
    bool IsRestricted,
    WorkStateDto State);

/// <summary>
/// What one reader has made of one work. PER-USER, and written by nothing but that user.
/// </summary>
/// <remarks>
/// A stored row saying nothing and no stored row at all are the same state, and this API refuses to
/// let them be told apart — both render as <see cref="Cleared"/>. Storage picks the absent row as
/// the canonical form of that state; see the remarks on <c>WorksController.SetWorkState</c>.
/// </remarks>
/// <param name="Status">A <see cref="ReadingStatus"/> name — "None", "ToRead", "Reading", "Read"
/// or "Dropped". Enum names rather than numbers, as everywhere else on this API's wire.</param>
/// <param name="Rating">Half-stars, 1-10, so 7 is three and a half. Null means unrated, which is a
/// different fact from the lowest score and is never collapsed into it.</param>
/// <param name="FavoritedAt">When the reader marked the work a favorite, or null while it is not
/// one. <see cref="IsFavorite"/> is the same fact as a flag, for callers that only want to know
/// whether.</param>
public record WorkStateDto(string Status, int? Rating, string? Note, DateTime? FavoritedAt = null)
{
    /// <summary>What a reader who has said nothing about a work has said about it.</summary>
    public static readonly WorkStateDto Cleared = new(nameof(ReadingStatus.None), null, null);

    /// <summary>Whether the work is one of this reader's favorites — <see cref="FavoritedAt"/> as a flag.</summary>
    public bool IsFavorite => FavoritedAt is not null;
}

/// <summary>
/// A replacement for the caller's state on one work. Not a patch: every field is optional and an
/// omitted one means "cleared", so a merge could not express taking a rating back off.
/// </summary>
/// <param name="IsFavorite">Whether the work is to be one of the caller's favorites. A flag on the
/// way in even though storage keeps a date: when the mark went on is the server's to remember, and
/// a client re-sending the whole state must not be able to move it.</param>
public record SetWorkStateRequest(
    string? Status = null,
    int? Rating = null,
    string? Note = null,
    bool IsFavorite = false);

/// <summary>
/// Everything this instance holds about one work — a strict superset of <see cref="WorkListItemDto"/>,
/// and the body of <c>GET /api/works/{id}</c>.
/// </summary>
/// <remarks>
/// Nothing here is fetched on demand: every field was written by a listing scrape, so a detail page
/// costs AO3 nothing. The two fields a work's own page carries and a blurb does not —
/// <paramref name="PublishedAt"/> and the complete tag list — are reported as unfetched rather than
/// as absent, so the page can say which it is.
/// </remarks>
/// <param name="SummarySafeHtml">The summary, sanitized for rendering — see
/// <c>WorkSummaryHtml</c>. Named for what it is because the raw column is not this: a client that
/// renders this field as HTML is doing the intended thing, which is only true of the sanitized
/// form. Null where the scrape read no summary, or where the summary was markup and nothing else.</param>
/// <param name="Tags">Every tag on the work with its kind, in AO3's own order — fandoms,
/// relationships, characters, freeforms — rather than split into a field per kind, so a tag type
/// this app learns about later needs no new field to be shown.</param>
/// <param name="DetailFetchedAt">When the work's own page was last read, or null while everything
/// here came from listings alone. What makes a null <paramref name="PublishedAt"/> legible as "not
/// fetched yet" rather than "AO3 has no date for this".</param>
public record WorkDetailDto(
    long Id,
    string Title,
    IReadOnlyList<string> Authors,
    bool IsAnonymous,
    string? SummarySafeHtml,
    string Rating,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<WorkTagDto> Tags,
    IReadOnlyList<WorkSeriesDto> Series,
    IReadOnlyList<WorkShipDto> Ships,
    bool IsComplete,
    int WordCount,
    int ChapterCount,
    int? PlannedChapterCount,
    int Kudos,
    int Hits,
    int Bookmarks,
    int CommentCount,
    int CollectionCount,
    string? LanguageName,
    string? LanguageCode,
    DateTime UpdatedAt,
    bool UpdatedAtIsApproximate,
    DateTime? PublishedAt,
    DateTime? DetailFetchedAt,
    bool IsRestricted,
    DateTime FirstSeenAt,
    DateTime LastSeenAt,
    WorkStateDto State);

/// <param name="Type">An <see cref="Ao3TagType"/> name — enum names on the wire, as everywhere else
/// on this API.</param>
public record WorkTagDto(string Type, string Name);

/// <param name="Part">Position within the series, or null where the blurb's wording could not be
/// read as a number — a work in a series at an unknown position, not a work outside it.</param>
public record WorkSeriesDto(long Id, string Title, int? Part);

/// <summary>
/// A watched ship this work turned up under. The reader's own subscriptions only, and the id rides
/// along so the page can link back into the feed narrowed to that ship.
/// </summary>
/// <param name="MissingSinceAt">When a completed full sweep of that tag last failed to find this
/// work, or null while it is still listed there. A work can be opened, marked and downloaded from
/// a tag it has left — see <c>WorkQueries.Reachable</c> — so the page has to be able to say that
/// the archive no longer files it under this ship.</param>
public record WorkShipDto(int ShipId, string TagName, DateTime? MissingSinceAt);
