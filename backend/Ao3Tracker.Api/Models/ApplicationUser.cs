using Microsoft.AspNetCore.Identity;

namespace Ao3Tracker.Api.Models;

public class ApplicationUser : IdentityUser
{
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Whether marking a work a favorite also asks for its EPUB, the way pressing the EPUB button
    /// on the work's page would. Off by default: a download costs AO3 a page load and a file, and
    /// a reader who marks freely should have to say they want the shelf to fill itself. On the
    /// user row rather than in the browser because the mark and the queue both live here — the
    /// same favorite from a phone has to do the same thing.
    /// </summary>
    public bool AutoDownloadFavorites { get; set; }

    /// <summary>
    /// Ships this user follows. Note there is no ScrapeJobs navigation any more — jobs belong to
    /// a <see cref="Ship"/>, since scraped data is shared across everyone watching it.
    /// </summary>
    public ICollection<WatchedShip> WatchedShips { get; set; } = new List<WatchedShip>();

    public ICollection<UserWorkState> WorkStates { get; set; } = new List<UserWorkState>();

    public ICollection<Download> Downloads { get; set; } = new List<Download>();

    /// <summary>Unread and read alike — see <see cref="Notification"/> for what bounds them.</summary>
    public ICollection<Notification> Notifications { get; set; } = new List<Notification>();

    /// <summary>Named sets of library filter criteria, one of which may be the user's default.</summary>
    public ICollection<SavedWorkFilter> SavedWorkFilters { get; set; } = new List<SavedWorkFilter>();
}
