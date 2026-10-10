using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Ao3Tracker.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public AuthController(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [HttpPost("register")]
    public async Task<ActionResult<CurrentUserDto>> Register(RegisterRequest request)
    {
        var isFirstUser = !_userManager.Users.Any();

        var user = new ApplicationUser
        {
            UserName = request.Username,
            IsAdmin = isFirstUser, // first registered account becomes admin
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(error.Code, error.Description);
            return ValidationProblem(ModelState);
        }

        await _signInManager.SignInAsync(user, isPersistent: true);
        return Ok(new CurrentUserDto(user.Id, user.UserName!, user.Email, user.IsAdmin));
    }

    [HttpPost("login")]
    public async Task<ActionResult<CurrentUserDto>> Login(LoginRequest request)
    {
        // Accounts created before usernames existed were registered with the email as the username,
        // so this still finds them with what they have always typed here.
        var user = await _userManager.FindByNameAsync(request.Username);
        if (user is null)
            return Unauthorized(new { message = "Invalid username or password." });

        var result = await _signInManager.PasswordSignInAsync(user, request.Password, isPersistent: true, lockoutOnFailure: true);

        // Said plainly, because while locked even the right password fails, and "invalid" would
        // send someone hunting for a password they already had. This does tell a stranger the
        // username exists, but only one who has already spent five guesses on it.
        if (result.IsLockedOut)
            return Unauthorized(new { message = await LockedOutMessageAsync(user) });

        if (!result.Succeeded)
            return Unauthorized(new { message = "Invalid username or password." });

        return Ok(new CurrentUserDto(user.Id, user.UserName!, user.Email, user.IsAdmin));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<CurrentUserDto>> Me()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        return Ok(new CurrentUserDto(user.Id, user.UserName!, user.Email, user.IsAdmin));
    }

    private async Task<string> LockedOutMessageAsync(ApplicationUser user)
    {
        var end = await _userManager.GetLockoutEndDateAsync(user);
        var minutes = end is null ? 1 : Math.Max(1, (int)Math.Ceiling((end.Value - DateTimeOffset.UtcNow).TotalMinutes));
        return $"Too many failed sign-ins, so this account is locked for {minutes} more minute{(minutes == 1 ? "" : "s")}. "
            + "Until then even the right password will not work.";
    }
}
