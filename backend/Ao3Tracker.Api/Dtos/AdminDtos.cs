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

/// <summary>
/// The identity this instance presents to AO3 on every scrape request.
/// </summary>
/// <param name="UserAgent">The exact header value being sent, or null if scraping is disabled.</param>
/// <param name="OperatorContact">The effective contact.</param>
/// <param name="ContactSource">Where it came from — "AdminSetting", "Configuration", "AdminAccount" or "None".</param>
/// <param name="IsOverridden">Whether an explicit value is saved, as opposed to a resolved default.</param>
/// <param name="DefaultContact">What it would fall back to if the override were cleared.</param>
/// <param name="ScrapingEnabled">False when no usable contact exists; <paramref name="Problem"/> says why.</param>
public record ScrapingIdentityDto(
    string? UserAgent,
    string? OperatorContact,
    string ContactSource,
    bool IsOverridden,
    string? DefaultContact,
    bool ScrapingEnabled,
    string? Problem,
    string ProductToken,
    string InstanceId);

public record UpdateScrapingIdentityRequest(
    /* Null or blank clears the override and reverts to the admin account's address. */
    string? OperatorContact);
