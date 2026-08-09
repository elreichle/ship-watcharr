using System.ComponentModel.DataAnnotations;

namespace Ao3Tracker.Api.Dtos;

public record CreateScrapeJobRequest(
    [property: Required] string Name,
    [property: Required] string ScraperKey,
    [property: Range(1, int.MaxValue)] int IntervalMinutes);

public record ScrapeJobDto(
    int Id,
    string Name,
    string ScraperKey,
    int IntervalMinutes,
    bool IsEnabled,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt,
    string? LastRunStatus,
    string? LastRunError);

public record ScrapeRunDto(
    int Id,
    int ScrapeJobId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int ItemsScraped,
    string? ErrorMessage);

public record ScrapedItemDto(
    int Id,
    int ScrapeRunId,
    string SourceUrl,
    string? Title,
    string PayloadJson,
    DateTimeOffset ScrapedAt);
