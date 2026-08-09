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
}
