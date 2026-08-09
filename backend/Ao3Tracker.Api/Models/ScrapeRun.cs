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

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    public int ItemsScraped { get; set; }

    public ICollection<ScrapedItem> Items { get; set; } = new List<ScrapedItem>();
}
