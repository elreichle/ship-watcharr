using System.Net;
using Ao3Tracker.Api.Services.Credentials;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Api.Services.Scraping;

/// <param name="Username">The account AO3 greeted, when the page said so.</param>
/// <param name="Error">Why the login did not happen. Null on success, and never carries a password.</param>
public sealed record Ao3LoginResult(bool Success, string? Username = null, string? Error = null);

/// <summary>
/// Logs the deployment in to AO3 and hands back a session. See <see cref="Ao3SessionEstablisher"/>.
/// </summary>
public interface IAo3SessionEstablisher
{
    Task<Ao3LoginResult> LogInAsync(CancellationToken ct = default);
}

/// <summary>
/// The AO3 login round trip: fetch the form, post the credential, keep what the response set.
///
/// Two rate-gated requests, which is why it is not done per scrape run. The session it produces is
/// cached in <see cref="Models.Ao3InstanceCredential"/> and reused until AO3 stops honouring it, so
/// the steady-state cost of being logged in is nothing at all.
///
/// The POST it makes is the <em>only</em> write this software performs against AO3. Kudos,
/// comments, bookmarks, subscriptions and posting are non-goals in the spec, not merely
/// unimplemented — the session exists to read pages that require being logged in, and for nothing
/// else.
///
/// Built as three pieces, like the index scraper: <see cref="Ao3LoginPage"/> is the pure parser,
/// this is the round trip, and <see cref="Ao3SessionProvider"/> is the caller that decides whether
/// one is needed. Everything goes through <see cref="IRateLimitedHttpClient"/>, so the login is
/// spaced and carries the instance's honest User-Agent like every other request.
/// </summary>
public sealed class Ao3SessionEstablisher : IAo3SessionEstablisher
{
    /// <summary>Where AO3 serves the login form. The one URL in this flow that is not read off a page.</summary>
    internal const string LoginPath = "/users/login";

    private readonly IRateLimitedHttpClient _http;
    private readonly IAo3InstanceCredentialStore _credentials;
    private readonly Ao3HttpClientOptions _options;
    private readonly ILogger<Ao3SessionEstablisher> _logger;
    private readonly TimeProvider _time;

    public Ao3SessionEstablisher(
        IRateLimitedHttpClient http,
        IAo3InstanceCredentialStore credentials,
        IOptions<Ao3HttpClientOptions> options,
        ILogger<Ao3SessionEstablisher> logger,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _credentials = credentials;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<Ao3LoginResult> LogInAsync(CancellationToken ct = default)
    {
        try
        {
            return await PerformLoginAsync(ct);
        }
        catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
        {
            // An archive that is down, or a request that hit the 30-second timeout. Both are states
            // this application expects and reports rather than exceptional ones: a thrown login would
            // reach the worker's outer handler as "unhandled error while polling", losing the one
            // thing an operator needs to know, which is that the *login* is what did not happen.
            // See ScrapeCancellation for why the filter is written this way and not as
            // `ex is not OperationCanceledException`.
            _logger.LogWarning(ex, "AO3 login could not be attempted");

            return new Ao3LoginResult(false, Error:
                $"This instance could not reach AO3 to log in: {ex.Message} Due jobs are held until "
                + "it can. Nothing has been recorded against them.");
        }
    }

    private async Task<Ao3LoginResult> PerformLoginAsync(CancellationToken ct)
    {
        var credential = await _credentials.GetDecryptedCredentialAsync(ct);
        if (credential is null)
        {
            return new Ao3LoginResult(false, Error: ScrapingGate.NoAo3LoginMessage);
        }

        var loginUrl = Absolute(LoginPath);

        // Logged out and uncached on purpose: an existing session is answered with a redirect
        // rather than a form, and a cached authenticity token is one Rails has already retired.
        var page = await _http.GetLoggedOutAsync(loginUrl, ct);
        if (page.StatusCode != HttpStatusCode.OK)
        {
            return Failed($"AO3 answered {(int)page.StatusCode} for the login page at {loginUrl}.");
        }

        var form = Ao3LoginPage.ParseLoginForm(page.Content);
        if (form is null)
        {
            return Failed(
                "The login form could not be read from AO3's login page. This normally means the "
                + "archive's markup has changed and the login parser needs updating.");
        }

        var now = _time.GetUtcNow().UtcDateTime;

        // Rails ties the authenticity token to the session that served it, so the POST has to carry
        // the cookies the form arrived with. Sending the token without them is the same rejection as
        // sending no token at all.
        var jar = Ao3Cookies.Apply(Ao3Cookies.Empty, page.SetCookies, now);

        var fields = new Dictionary<string, string>
        {
            ["authenticity_token"] = form.AuthenticityToken,
            [form.LoginField] = credential.Value.Ao3Username,
            [form.PasswordField] = credential.Value.Ao3Password,
        };

        // Asked for when the form offers it: a remembered login is one that has to be re-established
        // less often, and every login is two requests AO3 would rather not serve.
        if (form.RememberMeField is not null) fields[form.RememberMeField] = "1";

        var action = Absolute(form.Action);
        if (!IsTheConfiguredArchive(action, loginUrl))
        {
            return Failed(
                $"AO3's login page asked for the credential to be posted to {action}, which is not "
                + $"{_options.BaseUrl}. Nothing was sent. This instance only ever posts its AO3 "
                + "login to the archive it is configured for.");
        }

        var posted = await _http.PostFormAsync(
            action, fields, Ao3Cookies.ToHeader(jar), ct);

        // What the POST *itself* established, separately from what the form fetch already held.
        // Merging first and then asking "are there any cookies" answers yes on the strength of the
        // anonymous pre-login cookie, so a response that set nothing at all would be accepted and
        // that anonymous cookie stored as the instance's session.
        var established = Ao3Cookies.Apply(Ao3Cookies.Empty, posted.SetCookies, now);
        var afterLogin = Ao3Cookies.Apply(jar, posted.SetCookies, now);

        var rejection = RejectionReason(posted, established);
        if (rejection is not null) return Failed(rejection);

        var session = new Ao3Session(
            Ao3Cookies.ToHeader(afterLogin)!,
            EstablishedAt: now,

            // Taken over the jar the login left behind, not over the headers that built it: a header
            // can announce a *deletion*, whose date is in the past by construction, and a cookie AO3
            // retired on the way past must not date the session it is not part of. A null expiry is
            // a session cookie AO3 gave no end date for, usable until a page comes back logged out.
            ExpiresAt: Ao3Cookies.EarliestExpiry(afterLogin));

        // Written through the caller's scope, which is the worker's poll scope: it is entered before
        // any job runs and holds nothing but a projection of due job ids, so this SaveChanges commits
        // only itself. Unlike Ao3SessionCache — reached from inside a walk, and therefore owning the
        // scope it writes in — this one does not need its own.
        await _credentials.SetSessionAsync(session, ct);

        var username = Ao3LoginPage.ReadSessionState(posted.Content).Username ?? credential.Value.Ao3Username;
        _logger.LogInformation("Logged in to AO3 as {Ao3Username}; session cached until {ExpiresAt}",
            username, session.ExpiresAt?.ToString("u") ?? "AO3 says otherwise");

        return new Ao3LoginResult(true, username);
    }

    /// <summary>
    /// Why this response is not a logged-in session, or null when it is one.
    ///
    /// A rejected login is not an error status: Devise re-renders the login page with a flash and a
    /// 200, so "did it work" is a question about the body and the redirect, never about the code.
    /// Success is a redirect somewhere that is not the login page, or a page that greets an account.
    /// Anything else is refused — including a redirect straight back to <c>/users/login</c>, which
    /// is what a locked or unconfirmed account gets.
    /// </summary>
    /// <param name="established">
    /// The cookies this response set, and only those. Not the merged jar: the form fetch already put
    /// an anonymous cookie in that, so a merged jar is non-empty however little the POST did.
    /// </param>
    private string? RejectionReason(
        ScrapeHttpResponse posted, IReadOnlyDictionary<string, Ao3Cookie> established)
    {
        if (established.Count == 0)
        {
            // Rails rotates the session on sign-in, so a login that sets nothing has not signed
            // anything in — whatever the status says. Storing the pre-login cookie here would leave
            // every later run believing it was logged in until a page happened to prove otherwise.
            return "AO3 answered the login without setting a single cookie, so nothing was signed in "
                   + "and there is no session to keep.";
        }

        if (IsRedirect(posted.StatusCode))
        {
            // Location, not FinalUrl: this response was fetched by a transport that does not follow
            // redirects, so FinalUrl is where the POST was sent — always the login page, whichever
            // way it went.
            return posted.Location is null || posted.Location.Contains(LoginPath, StringComparison.Ordinal)
                ? "AO3 redirected the login back to the login page, which is how it refuses an account "
                  + "it will not sign in (a wrong password, or one that needs attention on the site itself)."
                : null;
        }

        var state = Ao3LoginPage.ReadSessionState(posted.Content).State;
        return state == Ao3SessionState.LoggedIn
            ? null
            : "AO3 did not sign this instance in. Check the username and password saved at "
              + "System → AO3 — a rejected login is answered with the login page again.";
    }

    /// <summary>
    /// Whether <paramref name="action"/> — the login form's own, read off a fetched page — addresses
    /// the archive this deployment is configured for, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the request that carries the deployment's AO3 username and its decrypted password,
    /// and its address comes out of markup, so an action of
    /// <c>https://elsewhere.example/collect</c> would send both there — and the instance would go on
    /// reporting a failed login rather than a leaked credential. The same check guards
    /// <see cref="Ao3DownloadLinks"/>, where a link read out of a work page is fetched with the
    /// session attached; this is the same rule on the input with the most to lose.
    /// </para>
    /// <para>
    /// Measured against <paramref name="configured"/> — <see cref="LoginPath"/> under the configured
    /// <see cref="Ao3HttpClientOptions.BaseUrl"/> — rather than against where the login page was
    /// finally served from. A relative action already resolves against that same root (see
    /// <see cref="Absolute"/>), so the configured archive is what the POST target is measured by
    /// either way, and it is the one address in this flow no page can influence.
    /// </para>
    /// <para>
    /// The whole origin and not merely the host: a password is what is being posted, so an action
    /// that kept the name and dropped to <c>http</c> would put it on the wire in the clear. AO3
    /// serves its login form over HTTPS and posts it back to the same place. That comparison lives
    /// in <see cref="Ao3Origin"/>, which is the same one the download links are measured by.
    /// </para>
    /// </remarks>
    private static bool IsTheConfiguredArchive(string action, string configured) =>
        Ao3Origin.IsTheConfiguredArchive(action, configured);

    private static bool IsRedirect(HttpStatusCode status) =>
        (int)status is >= 300 and < 400;

    private Ao3LoginResult Failed(string error)
    {
        _logger.LogWarning("AO3 login failed: {Error}", error);
        return new Ao3LoginResult(false, Error: error);
    }

    /// <summary>
    /// Resolves a path read off the page against the configured archive root. AO3's forms post to
    /// relative paths; a test's stub is somewhere else entirely, and both have to work.
    ///
    /// The scheme is checked rather than merely the "is this absolute" answer: on Linux
    /// <c>Uri.TryCreate("/users/login", UriKind.Absolute, …)</c> succeeds, producing
    /// <c>file:///users/login</c>. A form action would then be resolved against the local disk
    /// instead of against the archive.
    /// </summary>
    private string Absolute(string pathOrUrl) =>
        Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute)
        && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute.ToString()
            : $"{_options.BaseUrl.TrimEnd('/')}/{pathOrUrl.TrimStart('/')}";
}
