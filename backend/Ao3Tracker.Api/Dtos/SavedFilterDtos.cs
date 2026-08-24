using System.ComponentModel.DataAnnotations;

namespace Ao3Tracker.Api.Dtos;

/// <summary>
/// A saved filter as the client sees it. Mirrors <c>SavedWorkFilter</c>'s criteria one for one,
/// with three deliberate differences.
///
/// Enum-valued criteria travel as their names — "TeenAndUpAudiences", ["FF", "Gen"] — never as the
/// numbers behind them. Nothing in this API serializes enums as ints (see <c>WatchedShipDto</c>),
/// and a flags column arriving as <c>17</c> would make the client decode a bitmask it has no
/// business knowing about. The display wording stays server-side too; the client asks
/// <c>/api/lookups/vocabulary</c> to turn a name into something to show.
///
/// Tags and authors arrive expanded, so the editor can render the chips it has to re-post without
/// a lookup per id.
/// </summary>
/// <param name="IsDefault">Applied when the works list is opened without naming a set. At most one
/// per user; setting it here clears whichever set held it before.</param>
/// <param name="ShipTagName">Null when the set is not restricted to one ship. Present even when the
/// user no longer watches that ship — the set stays valid and simply matches nothing.</param>
/// <param name="MatchingWorkCount">How many works in the reader's library this set currently
/// matches. The one number that says whether a set does what its author meant.</param>
/// <param name="ReadingStatus">"None", "ToRead", "Reading", "Read" or "Dropped" — the reader's own
/// mark, not anything AO3 knows. "None" means unread in the widest sense: never marked at all, or
/// marked and cleared. Null is unconstrained. Not in <c>/api/lookups/vocabulary</c>, which serves
/// AO3's wording only; these five are this app's own and the client names them itself.</param>
/// <param name="MinUserRating">Inclusive bounds on the reader's own rating in half-stars, 1-10, so
/// 7 is three and a half. Unrated works match neither bound.</param>
public record SavedFilterDto(
    int Id,
    string Name,
    bool IsDefault,
    int? ShipId,
    string? ShipTagName,
    bool? IsComplete,
    int? MinWordCount,
    int? MaxWordCount,
    int? MinChapterCount,
    int? MaxChapterCount,
    int? MinKudos,
    int? MaxKudos,
    int? MinHits,
    int? MaxHits,
    int? MinComments,
    int? MaxComments,
    int? MinBookmarks,
    int? MaxBookmarks,
    string? MinRating,
    string? MaxRating,
    string? ReadingStatus,
    int? MinUserRating,
    int? MaxUserRating,
    IReadOnlyList<string> IncludeCategories,
    IReadOnlyList<string> ExcludeCategories,
    IReadOnlyList<string> IncludeWarnings,
    IReadOnlyList<string> ExcludeWarnings,
    string? LanguageCode,
    DateTime? UpdatedAfter,
    DateTime? UpdatedBefore,
    string Sort,
    bool Ascending,
    IReadOnlyList<SavedFilterTagDto> IncludeTags,
    IReadOnlyList<SavedFilterTagDto> ExcludeTags,
    IReadOnlyList<SavedFilterAuthorDto> IncludeAuthors,
    IReadOnlyList<SavedFilterAuthorDto> ExcludeAuthors,
    int MatchingWorkCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <param name="Type">"Fandom", "Relationship", "Character", "Freeform" or "Warning" — the editor
/// groups by it, since "Fluff" the freeform and "Fluff" the character are different criteria.</param>
public record SavedFilterTagDto(int TagId, string Name, string Type);

public record SavedFilterAuthorDto(int PseudId, string DisplayName, string Username);

/// <summary>
/// A set to create or replace. PUT replaces the whole thing rather than patching it: every
/// criterion is nullable and null means "unconstrained", so a merge could never express "stop
/// filtering by word count" — the absent field and the cleared one look identical.
/// </summary>
/// <param name="ShipId">Checked against the caller's subscriptions when saved, so a typo'd id is a
/// 400 now rather than a set that silently matches nothing. Not rechecked when the set is applied;
/// unwatching the ship later leaves the set valid and empty.</param>
/// <param name="Sort">One of the keys the works list offers. Anything else is rejected — a set
/// whose saved sort is a typo would silently open in a different order.</param>
public record SaveFilterRequest(
    [Required(ErrorMessage = "Give this filter a name.")]
    [MaxLength(100, ErrorMessage = "That name is longer than 100 characters.")]
    string Name,
    bool IsDefault = false,
    int? ShipId = null,
    bool? IsComplete = null,
    int? MinWordCount = null,
    int? MaxWordCount = null,
    int? MinChapterCount = null,
    int? MaxChapterCount = null,
    int? MinKudos = null,
    int? MaxKudos = null,
    int? MinHits = null,
    int? MaxHits = null,
    int? MinComments = null,
    int? MaxComments = null,
    int? MinBookmarks = null,
    int? MaxBookmarks = null,
    string? MinRating = null,
    string? MaxRating = null,
    string? ReadingStatus = null,
    int? MinUserRating = null,
    int? MaxUserRating = null,
    IReadOnlyList<string>? IncludeCategories = null,
    IReadOnlyList<string>? ExcludeCategories = null,
    IReadOnlyList<string>? IncludeWarnings = null,
    IReadOnlyList<string>? ExcludeWarnings = null,
    string? LanguageCode = null,
    DateTime? UpdatedAfter = null,
    DateTime? UpdatedBefore = null,
    string Sort = "updated",
    bool Ascending = false,
    IReadOnlyList<int>? IncludeTagIds = null,
    IReadOnlyList<int>? ExcludeTagIds = null,
    IReadOnlyList<int>? IncludeAuthorIds = null,
    IReadOnlyList<int>? ExcludeAuthorIds = null);

/// <summary>Which set opens by default, toggled without re-posting the whole set.</summary>
public record SetDefaultFilterRequest(bool IsDefault);

/// <summary>
/// The controlled vocabulary a filter is built from, served rather than hard-coded in the client.
///
/// Same reason <c>Ao3Labels</c> lives server-side: this wording is AO3's, and the parser that reads
/// these words off a blurb is here too. A second copy in the client is a second thing to update
/// when AO3 renames something, and the two would drift.
/// </summary>
/// <param name="Languages">Codes actually present on works in this instance's library, with the
/// display name AO3 gave them. Empty until something has been scraped.</param>
public record FilterVocabularyDto(
    IReadOnlyList<VocabularyOptionDto> Ratings,
    IReadOnlyList<VocabularyOptionDto> Categories,
    IReadOnlyList<VocabularyOptionDto> Warnings,
    IReadOnlyList<VocabularyOptionDto> Sorts,
    IReadOnlyList<VocabularyOptionDto> Languages);

/// <param name="Value">The name the API takes and returns.</param>
/// <param name="Label">AO3's own wording, for display.</param>
public record VocabularyOptionDto(string Value, string Label);
