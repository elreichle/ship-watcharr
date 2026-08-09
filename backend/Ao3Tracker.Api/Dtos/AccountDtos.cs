using System.ComponentModel.DataAnnotations;

namespace Ao3Tracker.Api.Dtos;

/// <summary>
/// Optional contact address for this account. Null or blank clears it. An admin's address is what
/// the AO3 operator contact falls back to when nothing is set explicitly (see OperatorContactResolver).
/// </summary>
/// <remarks>
/// Deliberately not annotated with <c>[EmailAddress]</c>: that attribute rejects a whitespace-only
/// string, which would turn "clear my email" into a validation error. The controller collapses
/// blank to null first and validates the format itself.
/// </remarks>
public record UpdateAccountEmailRequest(string? Email);

public record AccountEmailDto(string? Email, bool IsUsedAsOperatorContact);

public record SetAo3CredentialRequest(
    [Required] string Ao3Username,
    [Required] string Ao3Password);

/// <summary>Never includes the password/session cookie — status only.</summary>
public record Ao3CredentialStatusDto(
    bool HasCredential,
    string? Ao3Username,
    bool HasActiveSession,
    DateTime? SessionExpiresAt);
