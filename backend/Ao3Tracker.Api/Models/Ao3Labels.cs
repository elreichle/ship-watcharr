namespace Ao3Tracker.Api.Models;

/// <summary>
/// AO3's own wording for the values in <see cref="Ao3Rating"/>, <see cref="Ao3Category"/> and
/// <see cref="Ao3Warning"/>.
///
/// Server-side rather than a lookup table in the client, for one reason: this vocabulary is AO3's,
/// and the parser that reads these strings off a blurb lives here too. Two copies would let the
/// reader and the writer of the same words drift apart.
/// </summary>
public static class Ao3Labels
{
    /// <summary>Ordered as AO3 lists them, so a work's categories always read in the same order.</summary>
    private static readonly (Ao3Category Flag, string Label)[] CategoryLabels =
    [
        (Ao3Category.FF, "F/F"),
        (Ao3Category.FM, "F/M"),
        (Ao3Category.Gen, "Gen"),
        (Ao3Category.MM, "M/M"),
        (Ao3Category.Multi, "Multi"),
        (Ao3Category.Other, "Other"),
        (Ao3Category.NoCategory, "No category"),
        (Ao3Category.Unknown, "Unrecognised category"),
    ];

    private static readonly (Ao3Warning Flag, string Label)[] WarningLabels =
    [
        (Ao3Warning.NoArchiveWarningsApply, "No Archive Warnings Apply"),
        (Ao3Warning.ChooseNotToUseArchiveWarnings, "Creator Chose Not To Use Archive Warnings"),
        (Ao3Warning.GraphicDepictionsOfViolence, "Graphic Depictions Of Violence"),
        (Ao3Warning.MajorCharacterDeath, "Major Character Death"),
        (Ao3Warning.RapeNonCon, "Rape/Non-Con"),
        (Ao3Warning.Underage, "Underage"),
        (Ao3Warning.Unknown, "Unrecognised warning"),
    ];

    public static string Describe(Ao3Rating rating) => rating switch
    {
        Ao3Rating.NotRated => "Not Rated",
        Ao3Rating.GeneralAudiences => "General Audiences",
        Ao3Rating.TeenAndUpAudiences => "Teen And Up Audiences",
        Ao3Rating.Mature => "Mature",
        Ao3Rating.Explicit => "Explicit",
        _ => "Unknown",
    };

    /// <summary>
    /// Empty when nothing is set. That is the honest answer for a work scraped before the parser
    /// understood categories — "No category" is AO3's explicit choice and means something else.
    /// </summary>
    public static IReadOnlyList<string> Describe(Ao3Category categories) =>
        [.. CategoryLabels.Where(c => categories.HasFlag(c.Flag)).Select(c => c.Label)];

    public static IReadOnlyList<string> Describe(Ao3Warning warnings) =>
        [.. WarningLabels.Where(w => warnings.HasFlag(w.Flag)).Select(w => w.Label)];

    // ---- the vocabulary, enumerated -----------------------------------------------------------
    //
    // Served to the filter editor so it can offer these choices without keeping its own copy of
    // AO3's wording. Values are the enum names, which is what the filter API takes and returns.

    /// <summary>
    /// Ratings in AO3's order, least explicit first, so a "between" control reads correctly.
    /// <see cref="Ao3Rating.Unknown"/> is left out: it means the scraper never read a rating, so
    /// offering it as a band bound would ask the user to filter on our own shortfall.
    /// </summary>
    public static IReadOnlyList<(string Value, string Label)> Ratings { get; } =
    [
        .. new[]
        {
            Ao3Rating.NotRated,
            Ao3Rating.GeneralAudiences,
            Ao3Rating.TeenAndUpAudiences,
            Ao3Rating.Mature,
            Ao3Rating.Explicit,
        }.Select(r => (r.ToString(), Describe(r))),
    ];

    /// <summary>
    /// Every category including <see cref="Ao3Category.Unknown"/> — unlike a rating, an
    /// unrecognised category is a real thing a work carries, and excluding it is a filter someone
    /// might reasonably want while AO3's vocabulary is drifting.
    /// </summary>
    public static IReadOnlyList<(string Value, string Label)> Categories { get; } =
        [.. CategoryLabels.Select(c => (c.Flag.ToString(), c.Label))];

    public static IReadOnlyList<(string Value, string Label)> Warnings { get; } =
        [.. WarningLabels.Select(w => (w.Flag.ToString(), w.Label))];

    // ---- reading AO3's wording back off a blurb ------------------------------------------------
    //
    // The inverse of Describe, built from the very same tables, which is the whole reason this
    // lives here rather than in the parser: a label AO3 renames has to move in one place, or the
    // reader and the writer of the same words drift apart.
    //
    // AO3 has renamed items in this vocabulary before and will again, so every alias it has used is
    // accepted rather than only the current spelling. Historic pages stay parseable that way, and a
    // rename costs one line here instead of a silently mis-parsed backfill.

    private static readonly Dictionary<string, Ao3Rating> RatingsByLabel =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Not Rated"] = Ao3Rating.NotRated,
            ["General Audiences"] = Ao3Rating.GeneralAudiences,
            ["Teen And Up Audiences"] = Ao3Rating.TeenAndUpAudiences,
            ["Mature"] = Ao3Rating.Mature,
            ["Explicit"] = Ao3Rating.Explicit,
        };

    private static readonly Dictionary<string, Ao3Category> CategoriesByLabel =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["F/F"] = Ao3Category.FF,
            ["F/M"] = Ao3Category.FM,
            ["Gen"] = Ao3Category.Gen,
            ["M/M"] = Ao3Category.MM,
            ["Multi"] = Ao3Category.Multi,
            ["Other"] = Ao3Category.Other,
            ["No category"] = Ao3Category.NoCategory,
        };

    private static readonly Dictionary<string, Ao3Warning> WarningsByLabel =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["No Archive Warnings Apply"] = Ao3Warning.NoArchiveWarningsApply,
            ["Creator Chose Not To Use Archive Warnings"] = Ao3Warning.ChooseNotToUseArchiveWarnings,

            // AO3's own older wording, still rendered on some pages.
            ["Choose Not To Use Archive Warnings"] = Ao3Warning.ChooseNotToUseArchiveWarnings,

            ["Graphic Depictions Of Violence"] = Ao3Warning.GraphicDepictionsOfViolence,
            ["Major Character Death"] = Ao3Warning.MajorCharacterDeath,
            ["Rape/Non-Con"] = Ao3Warning.RapeNonCon,
            ["Underage"] = Ao3Warning.Underage,

            // Renamed in 2024; both spellings mean the same warning.
            ["Underage Sex"] = Ao3Warning.Underage,
        };

    /// <summary>
    /// Reads a rating from AO3's wording. <see cref="Ao3Rating.Unknown"/> for anything unrecognised
    /// — a rating is a single value with no spare bit to flag, and Unknown already means "the
    /// scraper never read one", which is exactly true here.
    /// </summary>
    public static Ao3Rating ParseRating(string? label) =>
        label is not null && RatingsByLabel.TryGetValue(label.Trim(), out var rating)
            ? rating
            : Ao3Rating.Unknown;

    /// <summary>
    /// Reads the comma-separated category list AO3 puts in a blurb's category <c>title</c>.
    /// Unrecognised tokens set <see cref="Ao3Category.Unknown"/> rather than throwing; see the
    /// remarks on that member.
    /// </summary>
    public static Ao3Category ParseCategories(string? title) =>
        ParseFlags(title, CategoriesByLabel, Ao3Category.None, Ao3Category.Unknown);

    /// <summary>The same, for the warning <c>title</c>. See <see cref="ParseCategories"/>.</summary>
    public static Ao3Warning ParseWarnings(string? title) =>
        ParseFlags(title, WarningsByLabel, Ao3Warning.None, Ao3Warning.Unknown);

    private static TFlags ParseFlags<TFlags>(
        string? title,
        Dictionary<string, TFlags> byLabel,
        TFlags none,
        TFlags unknown)
        where TFlags : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(title)) return none;

        var result = Convert.ToInt64(none);

        // Split on commas only. No AO3 label in either vocabulary contains one, while several
        // contain characters a more eager split would break on — "Rape/Non-Con" and "F/M" both
        // carry a slash, and the rating labels contain spaces.
        const StringSplitOptions splitOptions =
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;

        foreach (var token in title.Split(',', splitOptions))
        {
            result |= byLabel.TryGetValue(token, out var flag)
                ? Convert.ToInt64(flag)
                : Convert.ToInt64(unknown);
        }

        return (TFlags)Enum.ToObject(typeof(TFlags), result);
    }
}
