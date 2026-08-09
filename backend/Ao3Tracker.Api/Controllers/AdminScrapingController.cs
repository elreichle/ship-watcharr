using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Settings;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// Lets an admin see and change how this instance identifies itself to AO3.
///
/// Only the operator contact is editable. The product token and instance id are shown but fixed:
/// the token is what lets AO3 recognise this tool's traffic as a known client, and the instance id
/// distinguishes deployments without identifying anyone — letting either be edited would turn an
/// honest identifier into a disguise, and the politeness guarantees depend on it staying honest.
///
/// Unlike database settings, changes here apply immediately — the User-Agent is resolved per
/// request, so there is nothing to restart.
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin/scraping")]
public class AdminScrapingController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPersistedSettingsStore _settingsStore;
    private readonly IOperatorContactResolver _contacts;
    private readonly Ao3UserAgentProvider _userAgents;
    private readonly InstanceIdentity _instance;
    private readonly Ao3HttpClientOptions _options;
    private readonly ILogger<AdminScrapingController> _logger;

    public AdminScrapingController(
        UserManager<ApplicationUser> userManager,
        IPersistedSettingsStore settingsStore,
        IOperatorContactResolver contacts,
        Ao3UserAgentProvider userAgents,
        InstanceIdentity instance,
        IOptions<Ao3HttpClientOptions> options,
        ILogger<AdminScrapingController> logger)
    {
        _userManager = userManager;
        _settingsStore = settingsStore;
        _contacts = contacts;
        _userAgents = userAgents;
        _instance = instance;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet("identity")]
    public async Task<ActionResult<ScrapingIdentityDto>> GetIdentity(CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync()) return Forbid();
        return Ok(await BuildIdentityAsync(ct));
    }

    [HttpPut("identity")]
    public async Task<ActionResult<ScrapingIdentityDto>> UpdateIdentity(
        UpdateScrapingIdentityRequest request, CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync()) return Forbid();

        var contact = request.OperatorContact?.Trim();

        // Blank means "clear the override", which is always allowed — the resolver falls back to
        // the admin account's address. Only a non-blank value has to be reachable.
        if (!string.IsNullOrWhiteSpace(contact) && !Ao3UserAgentProvider.ValidateContact(contact, out var error))
            return BadRequest(new { message = error });

        await _settingsStore.SaveOperatorContactAsync(contact, ct);

        _logger.LogInformation(
            "AO3 operator contact {Action} by {UserId}",
            string.IsNullOrWhiteSpace(contact) ? "cleared" : "updated",
            _userManager.GetUserId(User));

        return Ok(await BuildIdentityAsync(ct));
    }

    private async Task<ScrapingIdentityDto> BuildIdentityAsync(CancellationToken ct)
    {
        var resolution = await _contacts.ResolveAsync(ct);
        var (ok, userAgent, problem) = await _userAgents.TryGetUserAgentAsync(ct);

        var saved = await _settingsStore.ReadOperatorContactAsync(ct);
        var isOverridden = !string.IsNullOrWhiteSpace(saved);

        // Read-only: asks the resolver what it would fall back to. An earlier version of this
        // computed it by clearing the saved value, resolving, then writing it back — which would
        // have raced any concurrent save and could have lost the operator's contact outright.
        var defaultContact = (await _contacts.ResolveDefaultAsync(ct)).Contact;

        return new ScrapingIdentityDto(
            UserAgent: ok ? userAgent : null,
            OperatorContact: resolution.Contact,
            ContactSource: resolution.Source.ToString(),
            IsOverridden: isOverridden,
            DefaultContact: defaultContact,
            ScrapingEnabled: ok,
            Problem: ok ? null : problem,
            ProductToken: _options.ProductToken,
            InstanceId: _instance.Id);
    }

    private async Task<bool> IsCurrentUserAdminAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        return user?.IsAdmin == true;
    }
}
