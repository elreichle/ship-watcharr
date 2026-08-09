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
    bool IsRestricted);
