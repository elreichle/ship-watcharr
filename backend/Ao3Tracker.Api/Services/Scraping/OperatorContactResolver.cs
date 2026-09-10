using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Services.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>Where an operator contact came from. Shown in the settings UI so the value is never mysterious.</summary>
public enum OperatorContactSource
{
    /// <summary>Nothing configured and no admin account yet — scraping is disabled.</summary>
    None = 0,

    /// <summary>Explicitly saved through the admin settings UI. Beats everything else.</summary>
    AdminSetting = 1,

    /// <summary>From user secrets, environment, or appsettings — the deployment's own config.</summary>
    Configuration = 2,

    /// <summary>Fell back to the first admin's optional account email.</summary>
    AdminAccount = 3,
}

public sealed record OperatorContactResolution(string? Contact, OperatorContactSource Source)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Contact);
}

public interface IOperatorContactResolver
{
    Task<OperatorContactResolution> ResolveAsync(CancellationToken ct = default);

    /// <summary>
    /// What the contact would be if the admin's saved override were cleared. Lets the settings UI
    /// show "clearing this reverts to X" without having to actually clear it to find out.
    /// </summary>
    Task<OperatorContactResolution> ResolveDefaultAsync(CancellationToken ct = default);
}

/// <summary>
/// Works out how AO3 should reach whoever runs this instance, in precedence order:
///
///   1. The admin setting saved through the app's settings UI.
///   2. Ao3HttpClient:OperatorContact from configuration (user secrets locally, env under Docker).
///   3. The first admin account's email address, if they gave one.
///
/// Rule 3 is what makes this work out of the box, so whoever installs this becomes the contact for
/// their own instance automatically and the project's author never is. Registration only requires a
/// username, though, so an admin who skipped the optional email leaves this unresolved — scraping
/// then stays disabled until a contact is saved at System → AO3 (rule 1).
///
/// Rule 1 beating rule 2 matches how database settings already behave: a deliberate choice made
/// in the UI is the source of truth until it is changed there again (see Program.cs).
/// </summary>
public sealed class OperatorContactResolver : IOperatorContactResolver
{
    private readonly IPersistedSettingsStore _settings;
    private readonly Ao3HttpClientOptions _options;
    private readonly AppDbContext _db;

    public OperatorContactResolver(
        IPersistedSettingsStore settings,
        IOptions<Ao3HttpClientOptions> options,
        AppDbContext db)
    {
        _settings = settings;
        _options = options.Value;
        _db = db;
    }

    public async Task<OperatorContactResolution> ResolveAsync(CancellationToken ct = default)
    {
        var saved = await _settings.ReadOperatorContactAsync(ct);
        if (!string.IsNullOrWhiteSpace(saved))
            return new OperatorContactResolution(saved.Trim(), OperatorContactSource.AdminSetting);

        return await ResolveDefaultAsync(ct);
    }

    public async Task<OperatorContactResolution> ResolveDefaultAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_options.OperatorContact))
            return new OperatorContactResolution(_options.OperatorContact.Trim(), OperatorContactSource.Configuration);

        // Oldest admin, not just any admin: on a multi-admin instance this has to be stable, or
        // the User-Agent would change identity depending on who registered most recently.
        var adminEmail = await _db.Users
            .Where(u => u.IsAdmin && u.Email != null && u.Email != "")
            .OrderBy(u => u.Id)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(adminEmail))
            return new OperatorContactResolution(adminEmail.Trim(), OperatorContactSource.AdminAccount);

        return new OperatorContactResolution(null, OperatorContactSource.None);
    }
}
