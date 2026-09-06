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

/// <summary>
/// The reader's own preferences: how the app behaves for them, as opposed to who they are. One
/// record for all of them, replaced whole, so a page that offers several never has to know which
/// one changed.
/// </summary>
/// <param name="AutoDownloadFavorites">Whether marking a work a favorite also asks for its EPUB —
/// see <c>ApplicationUser.AutoDownloadFavorites</c>.</param>
public record AccountPreferencesDto(bool AutoDownloadFavorites);

/// <summary>Every preference, sent whole: the endpoint replaces rather than patches.</summary>
public record UpdateAccountPreferencesRequest(bool AutoDownloadFavorites = false);
