using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Ao3Tracker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/account")]
public class AccountCredentialsController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;

    public AccountCredentialsController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [HttpPut("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(error.Code, error.Description);
            return ValidationProblem(ModelState);
        }

        // ChangePasswordAsync rotates the security stamp, and AddIdentityCookies() puts
        // SecurityStampValidator on the auth cookie -- so without re-issuing it here, changing your
        // own password signs you out at the next validation interval. Same pairing as
        // AccountEmailController.Update.
        await _signInManager.RefreshSignInAsync(user);

        return NoContent();
    }

    [HttpPut("username")]
    public async Task<ActionResult<CurrentUserDto>> ChangeUsername(UpdateUsernameRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        var username = request.Username.Trim();
        if (username.Length < 3 || username.Length > 64)
        {
            ModelState.AddModelError(nameof(request.Username),
                "The Username field must be between 3 and 64 characters.");
            return ValidationProblem(ModelState);
        }

        var result = await _userManager.SetUserNameAsync(user, username);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(error.Code, error.Description);
            return ValidationProblem(ModelState);
        }

        // SetUserNameAsync rotates the security stamp, same reasoning as ChangePassword above.
        await _signInManager.RefreshSignInAsync(user);

        return Ok(new CurrentUserDto(user.Id, user.UserName!, user.Email, user.IsAdmin));
    }
}
