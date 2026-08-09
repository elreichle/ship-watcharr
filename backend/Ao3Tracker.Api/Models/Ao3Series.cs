namespace Ao3Tracker.Api.Models;

/// <summary>An AO3 series. GLOBAL, shared.</summary>
public class Ao3Series
{
    /// <summary>AO3's series id from <c>/series/{id}</c>. ValueGeneratedNever(), as for <see cref="Work.Id"/>.</summary>
    public long Id { get; set; }

    public string Title { get; set; } = null!;

    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    public ICollection<WorkSeries> Works { get; set; } = new List<WorkSeries>();
}
