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

public record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required, MinLength(8)] string NewPassword);

public record UpdateUsernameRequest(string Username);
