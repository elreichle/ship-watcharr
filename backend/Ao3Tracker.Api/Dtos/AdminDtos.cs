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
/// <param name="ScrapingEnabled">
/// Whether scraping may run at all — every gate in <see cref="Services.Scraping.ScrapingGate"/>,
/// not just this instance's identity. <paramref name="Problem"/> lists every reason it may not.
/// </param>
/// <param name="Problem">Every blocker at once, or null when scraping is running.</param>
/// <param name="IdentityConfigured">
/// Whether an honest User-Agent could be built. Narrower than <paramref name="ScrapingEnabled"/>:
/// an instance can be perfectly identifiable and still be held for want of an AO3 login.
/// </param>
/// <param name="Ao3LoginConfigured">
/// Whether an AO3 login is stored for the deployment. The other gate — see
/// <see cref="InstanceAo3CredentialDto"/>.
/// </param>
/// <param name="IdentityProblem">
/// Why this instance can build no honest User-Agent, or null when it can. The identity gate's
/// reason alone, so a screen explaining the User-Agent can say what is wrong with it without
/// reprinting <paramref name="Problem"/>'s other blockers, which are about other things.
/// </param>
public record ScrapingIdentityDto(
    string? UserAgent,
    string? OperatorContact,
    string ContactSource,
    bool IsOverridden,
    string? DefaultContact,
    bool ScrapingEnabled,
    string? Problem,
    string ProductToken,
    string InstanceId,
    bool IdentityConfigured,
    bool Ao3LoginConfigured,
    string? IdentityProblem);

public record UpdateScrapingIdentityRequest(
    /* Null or blank clears the override and reverts to the admin account's address. */
    string? OperatorContact);

/// <summary>
/// The state of the one AO3 login this deployment scrapes as. Status only: neither the password
/// nor the session cookie is ever part of this, and there is no endpoint that reads either back.
/// </summary>
/// <param name="HasCredential">
/// Whether a login is stored at all — which is also "is a password stored", since the row cannot
/// exist without one. This is what gates scraping.
/// </param>
/// <param name="Ao3Username">The stored AO3 username, or null when no login is configured.</param>
/// <param name="HasCachedSession">
/// Whether a session cookie is currently cached. Purely informational: the session is a cache of
/// the password, so its absence means a login is due, never that the credential is gone.
/// </param>
/// <param name="SessionEstablishedAt">When the cached session was obtained, if there is one.</param>
/// <param name="SessionExpiresAt">When the cached session stops being usable, if AO3 said.</param>
public record InstanceAo3CredentialDto(
    bool HasCredential,
    string? Ao3Username,
    bool HasCachedSession,
    DateTime? SessionEstablishedAt,
    DateTime? SessionExpiresAt);

public record SetInstanceAo3CredentialRequest(
    [Required] string Ao3Username,
    [Required] string Ao3Password);
