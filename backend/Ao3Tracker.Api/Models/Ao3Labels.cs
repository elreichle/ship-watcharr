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
}
