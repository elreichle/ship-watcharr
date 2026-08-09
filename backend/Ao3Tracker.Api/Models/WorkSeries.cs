namespace Ao3Tracker.Api.Models;

/// <summary>
/// Join between <see cref="Work"/> and <see cref="Ao3Series"/>. Composite PK (WorkId, SeriesId).
/// A work can belong to several series, and blurbs do list more than one.
/// </summary>
public class WorkSeries
{
    public long WorkId { get; set; }
    public Work Work { get; set; } = null!;

    public long SeriesId { get; set; }
    public Ao3Series Series { get; set; } = null!;

    /// <summary>Part number within the series. Nullable so unexpected markup degrades rather than throws.</summary>
    public int? Part { get; set; }
}
