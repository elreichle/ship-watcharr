using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// The reader's own preferences, at Settings → Downloads. Per-user and server-side, unlike the
/// theme and the reader's text size: those are about the screen in front of them, while a
/// preference here changes what the server does on their behalf, and it has to do the same thing
/// whichever device the click came from.
/// </summary>
[ApiController]
[Authorize]
[Route("api/account/preferences")]
public class AccountPreferencesController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;

    public AccountPreferencesController(UserManager<ApplicationUser> userManager)
    {
        _userManager = userManager;
    }

    [HttpGet]
    public async Task<ActionResult<AccountPreferencesDto>> Get()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        return Ok(Describe(user));
    }

    /// <summary>
    /// Replaces every preference with what the request carries. No sign-in refresh, unlike the
    /// email endpoint beside this one: nothing here rotates the security stamp.
    /// </summary>
    [HttpPut]
    public async Task<ActionResult<AccountPreferencesDto>> Update(UpdateAccountPreferencesRequest request)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        user.AutoDownloadFavorites = request.AutoDownloadFavorites;

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(error.Code, error.Description);
            return ValidationProblem(ModelState);
        }

        return Ok(Describe(user));
    }

    private static AccountPreferencesDto Describe(ApplicationUser user) => new(user.AutoDownloadFavorites);
}
