using System.ComponentModel.DataAnnotations;

namespace Ao3Tracker.Api.Dtos;

public record RegisterRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password);

public record LoginRequest(
    [Required] string Email,
    [Required] string Password);

public record CurrentUserDto(string Id, string Email, bool IsAdmin);
