using Ao3Tracker.Api.Services.Credentials;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Everything that has to be true before this instance may fetch a single page from AO3, answered
/// in one place so the worker that enforces it and the admin screen that explains it cannot drift
/// apart.
///
/// Two independent gates, and both must pass:
///
///   1. An honest User-Agent can be built — which needs an operator contact. See
///      <see cref="Ao3UserAgentProvider"/>.
///   2. An AO3 login is stored for the deployment. See <see cref="Models.Ao3InstanceCredential"/>.
///
/// Both are evaluated on every call, never short-circuited: a half-configured instance should be
/// told everything it is missing at once rather than one thing per fix. Nothing here is cached —
/// the gate is re-read every poll, so saving a contact or a login starts scraping on the next tick
/// rather than at the next restart.
/// </summary>
public sealed class ScrapingGate
{
    /// <summary>
    /// Said in terms of the login, never the session: the session cookie is a cache of the
    /// password, so its absence means a login is due, not that anything is missing.
    /// </summary>
    internal const string NoAo3LoginMessage =
        "No AO3 login is stored for this instance. The library is shared, so the deployment "
        + "signs in as one AO3 account; until an admin saves one at System → AO3, due jobs are "
        + "held unrun.";

    private readonly Ao3UserAgentProvider _userAgents;
    private readonly IAo3InstanceCredentialStore _credentials;

    public ScrapingGate(Ao3UserAgentProvider userAgents, IAo3InstanceCredentialStore credentials)
    {
        _userAgents = userAgents;
        _credentials = credentials;
    }

    public async Task<ScrapingGateState> EvaluateAsync(CancellationToken ct = default)
    {
        var (identityOk, userAgent, identityError) = await _userAgents.TryGetUserAgentAsync(ct);
        var hasLogin = await _credentials.HasCredentialAsync(ct);

        // Kept as its own value as well as being folded into the list: the admin screen has a
        // section about the User-Agent and nothing else, and printing every blocker there says
        // "no AO3 login is stored" inside a block that is not about the login.
        var identityProblem = identityOk
            ? null
            : identityError ?? "This instance cannot identify itself to AO3.";

        var blockers = new List<string>();
        if (identityProblem is not null) blockers.Add(identityProblem);
        if (!hasLogin) blockers.Add(NoAo3LoginMessage);

        return new ScrapingGateState(
            CanScrape: blockers.Count == 0,
            UserAgent: identityOk ? userAgent : null,
            IdentityConfigured: identityOk,
            Ao3LoginConfigured: hasLogin,
            IdentityProblem: identityProblem,
            Blockers: blockers);
    }
}

/// <param name="CanScrape">True only when every gate below passes.</param>
/// <param name="UserAgent">The header this instance would send, or null if it cannot build one.</param>
/// <param name="IdentityConfigured">Whether an honest User-Agent could be built.</param>
/// <param name="Ao3LoginConfigured">Whether an instance AO3 login is stored.</param>
/// <param name="IdentityProblem">
/// Why no honest User-Agent could be built, or null when one could. The identity gate's reason on
/// its own, for the one caller that is explaining the User-Agent rather than listing every reason
/// scraping is held.
/// </param>
/// <param name="Blockers">One reason per failing gate, in the order above. Empty when scraping may run.</param>
public sealed record ScrapingGateState(
    bool CanScrape,
    string? UserAgent,
    bool IdentityConfigured,
    bool Ao3LoginConfigured,
    string? IdentityProblem,
    IReadOnlyList<string> Blockers)
{
    /// <summary>Every blocker as one block of text, or null when there are none.</summary>
    public string? Problem => Blockers.Count == 0 ? null : string.Join("\n\n", Blockers);
}
