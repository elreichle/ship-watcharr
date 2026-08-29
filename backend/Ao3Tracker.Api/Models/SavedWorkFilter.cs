namespace Ao3Tracker.Api.Models;

/// <summary>
/// A named, reusable set of criteria for narrowing the library — AO3's filter sidebar, saved.
/// PER-USER.
///
/// Every criterion is its own typed column rather than a serialized blob, because each one maps
/// 1:1 onto a column of <see cref="Work"/> that the query is going to compare against. Columns let
/// the controller build the <c>Where</c> clauses straight from the entity, keep EF's own validation
/// and max-lengths in play, and mean a row written by one build can never fail to deserialize in
/// another. The cost is a migration per new criterion, which is the right trade while the
/// vocabulary being modelled is AO3's, and therefore fixed.
///
/// Null means "unconstrained" throughout — for the nullable ranges, for the flags masks, and for
/// <see cref="IsComplete"/>, where it is genuinely a third state rather than a default.
/// </summary>
public class SavedWorkFilter
{
    public int Id { get; set; }

    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    /// <summary>What the user calls this set. Unique per user, since it is how they pick it.</summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Applied when the works list is opened without naming a set. At most one per user; the
    /// invariant is enforced by <c>SavedFiltersController</c> in a transaction rather than by a
    /// filtered unique index, because <c>HasFilter</c> takes provider-specific SQL and this model
    /// is shared between SQLite and PostgreSQL.
    /// </summary>
    public bool IsDefault { get; set; }

    // ---- Criteria ----------------------------------------------------------------------

    /// <summary>
    /// Restrict to one ship. Not validated against the user's subscriptions when applied: the
    /// works query intersects it with the ships they currently watch, so a set naming a ship they
    /// have since unwatched returns nothing rather than reaching outside their library.
    /// </summary>
    public int? ShipId { get; set; }
    public Ship? Ship { get; set; }

    /// <summary>True for complete works only, false for WIPs only, null for both.</summary>
    public bool? IsComplete { get; set; }

    // Inclusive bounds. Null on either side leaves that side open.
    public int? MinWordCount { get; set; }
    public int? MaxWordCount { get; set; }
    public int? MinChapterCount { get; set; }
    public int? MaxChapterCount { get; set; }
    public int? MinKudos { get; set; }
    public int? MaxKudos { get; set; }
    public int? MinHits { get; set; }
    public int? MaxHits { get; set; }
    public int? MinComments { get; set; }
    public int? MaxComments { get; set; }
    public int? MinBookmarks { get; set; }
    public int? MaxBookmarks { get; set; }

    /// <summary>
    /// Inclusive rating band. <see cref="Ao3Rating"/> is ordered by ascending explicitness for
    /// exactly this — "Teen and below" is a <c>&lt;=</c> against an indexed column, not a set of
    /// six booleans.
    /// </summary>
    public Ao3Rating? MinRating { get; set; }
    public Ao3Rating? MaxRating { get; set; }

    // ---- the caller's own reading, not the archive's ------------------------------------------
    //
    // These two criteria are the only ones whose meaning depends on WHO applies the set: they are
    // matched against UserWorkState rows joined on the applying user's id, not on the filter's
    // owner. Only the owner can ever apply a set — every lookup in SavedFiltersController is scoped
    // by the caller's id — so the two are the same person and the filter's meaning is stable. Said
    // out loud because the columns themselves carry no user, and a future endpoint that let one
    // account apply another's set would silently change what a stored set matches.

    /// <summary>
    /// Restrict to works the reader has marked with this status. <see cref="ReadingStatus.None"/>
    /// is a real criterion here and means "unread" in the widest sense — no state row at all, or a
    /// row whose status was cleared while a rating or note stayed. Null is unconstrained.
    /// </summary>
    public ReadingStatus? ReadingStatus { get; set; }

    /// <summary>
    /// Inclusive bounds on the reader's own rating, in half-stars 1-10 — the same scale as
    /// <see cref="UserWorkState.Rating"/>, where 7 is three and a half stars.
    ///
    /// An unrated work matches neither bound: null is not a score, and a set asking for "3 stars or
    /// better" that quietly kept everything unrated would be useless. Range-checked in
    /// <c>SavedFiltersController</c> rather than by a check constraint, unlike the column these are
    /// compared against — that constraint could be declared on a brand-new table, while adding one
    /// here would force SQLite to rebuild an existing one.
    /// </summary>
    public int? MinUserRating { get; set; }
    public int? MaxUserRating { get; set; }

    /// <summary>Work must carry at least one of these categories. Null or None: unconstrained.</summary>
    public Ao3Category? IncludeCategories { get; set; }

    /// <summary>Work must carry none of these categories.</summary>
    public Ao3Category? ExcludeCategories { get; set; }

    /// <summary>Work must carry at least one of these archive warnings.</summary>
    public Ao3Warning? IncludeWarnings { get; set; }

    /// <summary>
    /// Work must carry none of these archive warnings. This is the one people actually reach for,
    /// and the reason warnings are filtered from the flags column rather than the tag rows: the
    /// flags are set even when AO3 renders the warning as a tag we failed to recognise.
    /// </summary>
    public Ao3Warning? ExcludeWarnings { get; set; }

    /// <summary>Matched against <see cref="Work.LanguageCode"/>, not the display name.</summary>
    public string? LanguageCode { get; set; }

    /// <summary>
    /// Inclusive bounds on <see cref="Work.UpdatedAt"/>. <c>DateTime</c> rather than
    /// <c>DateTimeOffset</c>: SQLite cannot translate range comparisons on the latter, and
    /// <c>AppDbContext</c> already forces every DateTime through <c>UtcDateTimeConverter</c>.
    /// </summary>
    public DateTime? UpdatedAfter { get; set; }
    public DateTime? UpdatedBefore { get; set; }

    /// <summary>
    /// The sort this set opens with — one of the keys <c>WorksController</c> offers. Carried on the
    /// set because "longest first" is part of what a saved view means, not a separate preference.
    /// An explicit sort in the request still wins, so the works page's own dropdown keeps working.
    /// </summary>
    public string Sort { get; set; } = "updated";

    public bool Ascending { get; set; }

    public ICollection<SavedWorkFilterTag> Tags { get; set; } = new List<SavedWorkFilterTag>();

    public ICollection<SavedWorkFilterAuthor> Authors { get; set; } = new List<SavedWorkFilterAuthor>();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One tag criterion on a saved filter.
///
/// Following AO3's sidebar: included tags are AND'ed — the work must carry every one of them —
/// while excluded tags are NOT'ed, so carrying any one of them removes the work. Two included tags
/// therefore narrow, which is what people expect from ticking two boxes, and is not what a naive
/// <c>Any(t => included.Contains(t))</c> would do.
/// </summary>
public class SavedWorkFilterTag
{
    public int SavedWorkFilterId { get; set; }
    public SavedWorkFilter SavedWorkFilter { get; set; } = null!;

    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;

    public bool Exclude { get; set; }
}

/// <summary>
/// One author criterion on a saved filter. Keyed by pseud rather than by account, because
/// <see cref="WorkAuthor"/> points at a pseud — filtering by account would need a second lookup
/// and would silently widen a filter aimed at one of an author's several pseuds.
///
/// Include/exclude read as for <see cref="SavedWorkFilterTag"/>, except that included authors are
/// OR'ed: a work has one byline per creator, so requiring two at once would only ever match
/// co-authored works, which is not what picking two authors means.
/// </summary>
public class SavedWorkFilterAuthor
{
    public int SavedWorkFilterId { get; set; }
    public SavedWorkFilter SavedWorkFilter { get; set; } = null!;

    public int PseudId { get; set; }
    public Ao3Pseud Pseud { get; set; } = null!;

    public bool Exclude { get; set; }
}
