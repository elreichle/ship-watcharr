namespace Ao3Tracker.Api.Models;

/// <summary>
/// An AO3 tag — fandom, relationship, character, freeform or warning. GLOBAL, shared.
/// </summary>
public class Tag
{
    public int Id { get; set; }

    public Ao3TagType Type { get; set; }

    /// <summary>Display text exactly as AO3 renders it.</summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// <see cref="Name"/> uppercased with the invariant culture, and the column all lookups and
    /// searches go through.
    ///
    /// This is a provider-portability requirement, not an optimisation: SQLite's default LIKE
    /// and its NOCASE collation are ASCII-case-insensitive, while PostgreSQL's text comparison
    /// and LIKE are case-sensitive. Comparing against Name directly would silently return
    /// different results on the two providers for the same query.
    /// </summary>
    public string NameNormalized { get; set; } = null!;

    /// <summary>AO3's numeric tag id, when we've seen it (harvested from the tag page's RSS link).</summary>
    public long? Ao3TagId { get; set; }

    public DateTime FirstSeenAt { get; set; }

    public ICollection<WorkTag> Works { get; set; } = new List<WorkTag>();
}
