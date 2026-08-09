using System.ComponentModel.DataAnnotations;

namespace Ao3Tracker.Api.Dtos;

public record SetAo3CredentialRequest(
    [property: Required] string Ao3Username,
    [property: Required] string Ao3Password);

/// <summary>Never includes the password/session cookie — status only.</summary>
public record Ao3CredentialStatusDto(
    bool HasCredential,
    string? Ao3Username,
    bool HasActiveSession,
    DateTimeOffset? SessionExpiresAt);
