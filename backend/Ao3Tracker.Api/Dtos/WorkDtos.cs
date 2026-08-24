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
/// <param name="Ships">The watched tags this work turned up under. Restricted to the reader's own
/// subscriptions: which other ships an instance tracks is not this user's business.</param>
/// <param name="State">The caller's own reading status, rating and note — never another reader's,
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
public record WorkStateDto(string Status, int? Rating, string? Note)
{
    /// <summary>What a reader who has said nothing about a work has said about it.</summary>
    public static readonly WorkStateDto Cleared = new(nameof(ReadingStatus.None), null, null);
}

/// <summary>
/// A replacement for the caller's state on one work. Not a patch: every field is optional and an
/// omitted one means "cleared", so a merge could not express taking a rating back off.
/// </summary>
public record SetWorkStateRequest(string? Status = null, int? Rating = null, string? Note = null);
