namespace Ao3Tracker.Api.Models;

public enum ScrapeRunStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
}

/// <summary>One execution attempt of a ScrapeJob, with status/timing/error history.</summary>
public class ScrapeRun
{
    public int Id { get; set; }

    public int ScrapeJobId { get; set; }
    public ScrapeJob ScrapeJob { get; set; } = null!;

    public ScrapeRunStatus Status { get; set; } = ScrapeRunStatus.Pending;

    // DateTime (UTC), not DateTimeOffset: StartedAt is ordered by, and the SQLite
    // provider can't translate ordering/comparisons on DateTimeOffset — see README.
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    public int ItemsScraped { get; set; }

    public ICollection<ScrapedItem> Items { get; set; } = new List<ScrapedItem>();
}
