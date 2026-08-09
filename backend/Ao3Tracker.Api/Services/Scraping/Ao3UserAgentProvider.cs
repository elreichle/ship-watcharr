using Ao3Tracker.Api.Services.Storage;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Builds the User-Agent sent on every scrape request, in three parts:
///
///   ShipWatcharr/0.1 (+contact: you@example.com; instance/a3f9c2)
///   \_____________/    \______________________/  \_____________/
///     the software        who runs this copy      which copy
///
/// The split is the point. The product token is constant and public, so AO3 can recognise this
/// tool's traffic; the contact belongs to whoever installed this copy, so the tool's author is
/// never on the hook for a stranger's instance; the instance id distinguishes deployments
/// without identifying anybody.
///
/// Resolved per request rather than cached, so changing the contact in the settings UI takes
/// effect immediately instead of at the next restart. There is no fallback contact — an instance
/// that cannot say who to contact does not get to make requests.
/// </summary>
public sealed class Ao3UserAgentProvider
{
    private readonly Ao3HttpClientOptions _options;
    private readonly InstanceIdentity _instance;
    private readonly IOperatorContactResolver _contacts;

    public Ao3UserAgentProvider(
        IOptions<Ao3HttpClientOptions> options,
        InstanceIdentity instance,
        IOperatorContactResolver contacts)
    {
        _options = options.Value;
        _instance = instance;
        _contacts = contacts;
    }

    /// <summary>
    /// The User-Agent for this instance right now.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No usable operator contact. Thrown rather than substituted: the previous behaviour was a
    /// placeholder default reading "set this in config", which is the worst case — it identifies
    /// the tool to AO3 while giving them nothing they can act on.
    /// </exception>
    public async Task<string> GetUserAgentAsync(CancellationToken ct = default)
    {
        var resolution = await _contacts.ResolveAsync(ct);
        if (!TryBuild(resolution, out var userAgent, out var error))
            throw new InvalidOperationException(error);

        return userAgent!;
    }

    /// <summary>Non-throwing form, for callers that need to disable scraping rather than fail.</summary>
    public async Task<(bool Ok, string? UserAgent, string? Error)> TryGetUserAgentAsync(CancellationToken ct = default)
    {
        var resolution = await _contacts.ResolveAsync(ct);
        var ok = TryBuild(resolution, out var userAgent, out var error);
        return (ok, userAgent, error);
    }

    private bool TryBuild(OperatorContactResolution resolution, out string? userAgent, out string? error)
    {
        userAgent = null;

        if (!ValidateContact(resolution.Contact, out error)) return false;

        var product = string.IsNullOrWhiteSpace(_options.ProductToken)
            ? "ShipWatcharr/0.1"
            : _options.ProductToken.Trim();

        userAgent = $"{product} (+contact: {resolution.Contact!.Trim()}; instance/{_instance.Id})";
        error = null;
        return true;
    }

    /// <summary>
    /// Whether a contact is something AO3 could actually use. A contact that cannot be contacted
    /// is worse than none — it looks cooperative while being useless.
    /// </summary>
    public static bool ValidateContact(string? contact, out string? error)
    {
        if (string.IsNullOrWhiteSpace(contact))
        {
            error =
                "No operator contact is configured, so this instance cannot identify itself to AO3 " +
                "and scraping is disabled.\n\n" +
                "AO3 is volunteer-run infrastructure. Give them a way to reach you — an email or a " +
                "project URL — so they can ask you to slow down instead of blocking you.\n\n" +
                "This defaults to the admin account's email, but that is optional at registration. " +
                "Set it explicitly under Settings, or via Ao3HttpClient:OperatorContact in configuration.";
            return false;
        }

        var trimmed = contact.Trim();
        var looksReachable = IsEmailLike(trimmed) ||
                             trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!looksReachable)
        {
            error = $"Operator contact '{trimmed}' does not look like an email address or a URL. " +
                    "It must be something AO3 can actually use to reach you.";
            return false;
        }

        // Whitespace would split the User-Agent comment into nonsense and could inject a second
        // product token; a bare newline would let a value break the header entirely.
        if (trimmed.Any(char.IsControl) || trimmed.Contains(')') || trimmed.Contains(';'))
        {
            error = $"Operator contact '{trimmed}' contains characters that are not valid in a " +
                    "User-Agent header (control characters, ')' or ';').";
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsEmailLike(string value)
    {
        var at = value.IndexOf('@');
        if (at <= 0 || at == value.Length - 1) return false;

        var domain = value[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }
}
