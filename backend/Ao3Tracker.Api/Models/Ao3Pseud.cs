namespace Ao3Tracker.Api.Models;

/// <summary>
/// An AO3 creator identity — a pseud belonging to a username. GLOBAL, shared.
///
/// AO3 models users and pseuds as separate entities, but collapsing them into one table with an
/// indexed <see cref="Username"/> already answers "everything by this author" without the extra
/// join. A separate Ao3User table buys nothing until we scrape user profiles, so it is deferred.
/// </summary>
public class Ao3Pseud
{
    public int Id { get; set; }

    /// <summary>The account name from <c>/users/{username}/pseuds/{pseud}</c>.</summary>
    public string Username { get; set; } = null!;

    /// <summary>The pseud name. Equal to <see cref="Username"/> for the default pseud.</summary>
    public string PseudName { get; set; } = null!;

    /// <summary>The byline text AO3 actually rendered.</summary>
    public string DisplayName { get; set; } = null!;

    public DateTime FirstSeenAt { get; set; }

    public ICollection<WorkAuthor> Works { get; set; } = new List<WorkAuthor>();
}
