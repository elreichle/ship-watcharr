using System.ComponentModel.DataAnnotations;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// Registration only asks for a username and password. An email is optional and lives here, so the
/// one reason to give one — an admin becoming their instance's AO3 operator contact — is presented
/// where it can be explained, rather than as an unexplained field on the signup form.
/// </summary>
[ApiController]
[Authorize]
[Route("api/account/email")]
public class AccountEmailController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IOperatorContactResolver _contactResolver;

    public AccountEmailController(
        UserManager<ApplicationUser> userManager,
        IOperatorContactResolver contactResolver)
    {
        _userManager = userManager;
        _contactResolver = contactResolver;
    }

    [HttpGet]
    public async Task<ActionResult<AccountEmailDto>> Get(CancellationToken ct)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        return Ok(await DescribeAsync(user, ct));
    }

    [HttpPut]
    public async Task<ActionResult<AccountEmailDto>> Update(UpdateAccountEmailRequest request, CancellationToken ct)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Unauthorized();

        // Blank collapses to null so "no email" is one value everywhere, rather than empty strings
        // that would read as a configured operator contact. Blank means "clear it", not "invalid",
        // so this has to happen before the format check.
        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();

        if (email is not null && !new EmailAddressAttribute().IsValid(email))
        {
            ModelState.AddModelError(nameof(request.Email), "The Email field is not a valid e-mail address.");
            return ValidationProblem(ModelState);
        }

        var result = await _userManager.SetEmailAsync(user, email);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
                ModelState.AddModelError(error.Code, error.Description);
            return ValidationProblem(ModelState);
        }

        return Ok(await DescribeAsync(user, ct));
    }

    private async Task<AccountEmailDto> DescribeAsync(ApplicationUser user, CancellationToken ct)
    {
        var resolved = await _contactResolver.ResolveAsync(ct);

        // Only true when this address is what AO3 actually sees right now: the resolver fell back to
        // an admin account, and that account is this one.
        var isContact = resolved.Source == OperatorContactSource.AdminAccount
            && !string.IsNullOrWhiteSpace(user.Email)
            && string.Equals(resolved.Contact, user.Email.Trim(), StringComparison.OrdinalIgnoreCase);

        return new AccountEmailDto(user.Email, isContact);
    }
}
