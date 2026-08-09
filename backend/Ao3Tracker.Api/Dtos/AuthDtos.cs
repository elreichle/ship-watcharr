using System.ComponentModel.DataAnnotations;

namespace Ao3Tracker.Api.Dtos;

public record RegisterRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required, MinLength(8)] string Password);

public record LoginRequest(
    [property: Required] string Email,
    [property: Required] string Password);

public record CurrentUserDto(string Id, string Email, bool IsAdmin);
