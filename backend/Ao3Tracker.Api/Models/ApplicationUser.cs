using Microsoft.AspNetCore.Identity;

namespace Ao3Tracker.Api.Models;

public class ApplicationUser : IdentityUser
{
    public bool IsAdmin { get; set; }

    public Ao3Credential? Ao3Credential { get; set; }

    /// <summary>
    /// Ships this user follows. Note there is no ScrapeJobs navigation any more — jobs belong to
    /// a <see cref="Ship"/>, since scraped data is shared across everyone watching it.
    /// </summary>
    public ICollection<WatchedShip> WatchedShips { get; set; } = new List<WatchedShip>();

    public ICollection<UserWorkState> WorkStates { get; set; } = new List<UserWorkState>();

    public ICollection<Download> Downloads { get; set; } = new List<Download>();

    /// <summary>Named sets of library filter criteria, one of which may be the user's default.</summary>
    public ICollection<SavedWorkFilter> SavedWorkFilters { get; set; } = new List<SavedWorkFilter>();
}
