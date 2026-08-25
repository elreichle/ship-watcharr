using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Credentials;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// The one AO3 login this deployment scrapes as, entered and cleared by an admin.
///
/// Instance-level rather than per-user because scraped data is shared — see
/// <see cref="Ao3InstanceCredential"/>. Sits under the same route prefix as
/// <see cref="AdminScrapingController"/> because it is the other half of "can this instance talk to
/// AO3 at all", but is a separate controller: identity is about how requests are attributed, this
/// is about what they are authorised as.
///
/// Nothing here ever reads a secret back out. The password and the session cookie go in and are
/// only ever used by the scraper; every response is status — who is configured, and whether a
/// session is currently cached — so a compromised dashboard session cannot exfiltrate the login.
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin/scraping/ao3-credential")]
public class AdminAo3CredentialController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IAo3InstanceCredentialStore _credentials;
    private readonly Ao3LoginBackoff _loginBackoff;
    private readonly ILogger<AdminAo3CredentialController> _logger;

    public AdminAo3CredentialController(
        UserManager<ApplicationUser> userManager,
        IAo3InstanceCredentialStore credentials,
        Ao3LoginBackoff loginBackoff,
        ILogger<AdminAo3CredentialController> logger)
    {
        _userManager = userManager;
        _credentials = credentials;
        _loginBackoff = loginBackoff;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<InstanceAo3CredentialDto>> GetStatus(CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync()) return Forbid();
        return Ok(await BuildStatusAsync(ct));
    }

    [HttpPut]
    public async Task<ActionResult<InstanceAo3CredentialDto>> SetCredential(
        SetInstanceAo3CredentialRequest request, CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync()) return Forbid();

        // Trimmed because it is typed by hand and a trailing space in an AO3 username is always a
        // typo; the password is passed through untouched, since whitespace in one is meaningful.
        await _credentials.SetCredentialAsync(request.Ao3Username.Trim(), request.Ao3Password, ct);

        // The credential just changed, so whatever the last login was backing off from is no longer
        // what would be tried. An operator correcting a password is owed an attempt on the next
        // poll, not a wait for a cooldown that was measuring the old one.
        _loginBackoff.Reset();

        _logger.LogInformation(
            "Instance AO3 credential saved by {UserId}; any cached session was discarded",
            _userManager.GetUserId(User));

        return Ok(await BuildStatusAsync(ct));
    }

    [HttpDelete]
    public async Task<ActionResult<InstanceAo3CredentialDto>> RemoveCredential(CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync()) return Forbid();

        // Removes the row rather than blanking it: a row with an empty password would still read as
        // "a login is configured" to the scraping gate, which is exactly the half-configured state
        // clearing it is meant to leave behind.
        await _credentials.RemoveCredentialAsync(ct);

        // Same reasoning as saving one: whatever the backoff was measuring is gone.
        _loginBackoff.Reset();

        _logger.LogInformation("Instance AO3 credential cleared by {UserId}", _userManager.GetUserId(User));

        return Ok(await BuildStatusAsync(ct));
    }

    private async Task<InstanceAo3CredentialDto> BuildStatusAsync(CancellationToken ct)
    {
        var username = await _credentials.GetUsernameAsync(ct);
        if (username is null)
            return new InstanceAo3CredentialDto(false, null, false, null, null);

        // Deliberately not GetDecryptedCredentialAsync: the username is all this needs, and
        // decrypting a password nobody is going to use is a risk taken for nothing.
        var session = await _credentials.GetSessionAsync(ct);

        return new InstanceAo3CredentialDto(
            HasCredential: true,
            Ao3Username: username,
            HasCachedSession: session is not null,
            SessionEstablishedAt: session?.EstablishedAt,
            SessionExpiresAt: session?.ExpiresAt);
    }

    private async Task<bool> IsCurrentUserAdminAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        return user?.IsAdmin == true;
    }
}
