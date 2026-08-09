using System.ComponentModel.DataAnnotations;

namespace Ao3Tracker.Api.Dtos;

public record DatabaseStatusDto(
    string Provider,
    string SqliteDbPath,
    bool PostgresConfigured,
    string? PostgresConnectionSummary);

public record UpdateDatabaseSettingsRequest(
    [Required] string Provider,
    string? PostgresConnectionString);
