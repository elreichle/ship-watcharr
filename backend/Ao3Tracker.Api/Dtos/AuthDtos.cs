using System.ComponentModel.DataAnnotations;

namespace Ao3Tracker.Api.Dtos;

/// <summary>
/// A username and password are all that is needed to sign up. Email is not collected here at all —
/// it is optional and set afterwards at Settings → Account (see <see cref="UpdateAccountEmailRequest"/>).
/// </summary>
public record RegisterRequest(
    [Required, MinLength(3), MaxLength(64)] string Username,
    [Required, MinLength(8)] string Password);

public record LoginRequest(
    [Required] string Username,
    [Required] string Password);

public record CurrentUserDto(string Id, string Username, string? Email, bool IsAdmin);
