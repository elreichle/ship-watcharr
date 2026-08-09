namespace Ao3Tracker.Api.Models;

/// <summary>Join between <see cref="Work"/> and <see cref="Tag"/>. Composite PK (WorkId, TagId).</summary>
public class WorkTag
{
    public long WorkId { get; set; }
    public Work Work { get; set; } = null!;

    public int TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}
