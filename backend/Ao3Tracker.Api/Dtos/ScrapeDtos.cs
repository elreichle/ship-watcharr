using System.ComponentModel.DataAnnotations;

namespace Ao3Tracker.Api.Dtos;

public record CreateScrapeJobRequest(
    [Required] string Name,
    [Required] string ScraperKey,
    [Range(1, int.MaxValue)] int IntervalMinutes);

public record ScrapeJobDto(
    int Id,
    string Name,
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
    DateTime StartedAt,
    DateTime? CompletedAt,
    int ItemsScraped,
    string? ErrorMessage);

public record ScrapedItemDto(
    int Id,
    int ScrapeRunId,
    string SourceUrl,
    string? Title,
    string PayloadJson,
    DateTime ScrapedAt);
