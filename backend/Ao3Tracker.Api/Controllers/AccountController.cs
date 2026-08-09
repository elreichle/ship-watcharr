using System.Security.Claims;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Services.Credentials;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ao3Tracker.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/account/ao3-credential")]
public class AccountController : ControllerBase
{
    private readonly IAo3CredentialStore _credentialStore;

    public AccountController(IAo3CredentialStore credentialStore)
    {
        _credentialStore = credentialStore;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request missing user id claim.");

    [HttpGet]
    public async Task<ActionResult<Ao3CredentialStatusDto>> GetStatus(CancellationToken ct)
    {
        var hasCredential = await _credentialStore.HasCredentialAsync(CurrentUserId, ct);
        if (!hasCredential)
            return Ok(new Ao3CredentialStatusDto(false, null, false, null));

        var decrypted = await _credentialStore.GetDecryptedCredentialAsync(CurrentUserId, ct);
        var session = await _credentialStore.GetSessionAsync(CurrentUserId, ct);

        return Ok(new Ao3CredentialStatusDto(
            HasCredential: true,
            Ao3Username: decrypted?.Ao3Username,
            HasActiveSession: session is not null,
            SessionExpiresAt: session?.ExpiresAt));
    }

    [HttpPut]
    public async Task<IActionResult> SetCredential(SetAo3CredentialRequest request, CancellationToken ct)
    {
        await _credentialStore.SetCredentialAsync(CurrentUserId, request.Ao3Username, request.Ao3Password, ct);
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> RemoveCredential(CancellationToken ct)
    {
        await _credentialStore.RemoveCredentialAsync(CurrentUserId, ct);
        return NoContent();
    }
}
