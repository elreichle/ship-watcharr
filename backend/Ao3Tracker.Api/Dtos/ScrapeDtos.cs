namespace Ao3Tracker.Api.Dtos;

/// <summary>
/// A scrape schedule. Jobs belong to a ship rather than to a user, so this is exposed to a user
/// only via the ships they watch — there is no per-user job to create or delete directly.
/// </summary>
public record ScrapeJobDto(
    int Id,
    int ShipId,
    string ShipName,
    string ScraperKey,
    int IntervalMinutes,
    bool IsEnabled,
    DateTime? LastRunAt,
    DateTime? NextRunAt,
    string? LastRunStatus,
    string? LastRunError);

public record ScrapeRunDto(
    int Id,
    int ScrapeJobId,
    string Status,
    string Mode,
    DateTime StartedAt,
    DateTime? CompletedAt,
    int PagesFetched,
    int RequestsMade,
    int WorksSeen,
    int WorksAdded,
    int WorksUpdated,
    string? StopReason,
    string? ErrorMessage);
