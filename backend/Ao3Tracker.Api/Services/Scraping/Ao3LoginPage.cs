using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// What AO3's login form is called and where it posts, read off the page that serves it rather than
/// remembered. Field names travel with the form for the same reason the token does: a Rails app is
/// free to rename them, and a client that hardcodes them posts a form nobody is listening for.
/// </summary>
/// <param name="Action">Where the form posts. Relative on AO3 ("/users/login").</param>
/// <param name="AuthenticityToken">
/// Rails' CSRF token. The POST is rejected without it, and it is bound to the session cookie that
/// served this page — the two have to travel together.
/// </param>
public sealed record Ao3LoginForm(
    string Action,
    string AuthenticityToken,
    string LoginField,
    string PasswordField,
    string? RememberMeField);

/// <summary>Whether a page AO3 served was rendered for a logged-in reader.</summary>
public enum Ao3SessionState
{
    /// <summary>The page carries neither marker — a 404, a file download, something unfamiliar.</summary>
    Unknown,

    /// <summary>The page offers a login, so whoever asked for it was nobody.</summary>
    LoggedOut,

    /// <summary>The page greets an account by name.</summary>
    LoggedIn,
}

/// <param name="Username">The account AO3 greeted, when the greeting could be read.</param>
public sealed record Ao3SessionReading(Ao3SessionState State, string? Username = null);

/// <summary>
/// Reads AO3's login page, and reads any AO3 page for whether it was served to a logged-in session.
///
/// A pure function of the markup — no HTTP, no database, no clock — built against the captures in
/// <c>Fixtures/</c> rather than against remembered selectors.
///
/// Nothing here logs in. It parses; <see cref="Ao3SessionEstablisher"/> is what performs the round
/// trip, and the split is what lets the markup half be tested against a saved page.
/// </summary>
public static class Ao3LoginPage
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// The real login form, which is <c>form#new_user</c> and never merely the first form on the
    /// page carrying an authenticity token.
    ///
    /// AO3 renders a second login form in the header dropdown (<c>form#new_user_session_small</c>),
    /// it also posts to <c>/users/login</c>, and it comes <em>first</em> in the document. "The first
    /// authenticity_token on the page" therefore finds the header's, and on a capture where both
    /// tokens happen to be equal such a parser passes for the wrong reason.
    /// </summary>
    public static Ao3LoginForm? ParseLoginForm(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var form = Parser.ParseDocument(html).QuerySelector("form#new_user");
        if (form is null) return null;

        var token = form.QuerySelector("input[name='authenticity_token']")?.GetAttribute("value");
        var action = form.GetAttribute("action");
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(action)) return null;

        // By input type rather than by name, so the names themselves can be read off the markup
        // instead of asserted against it.
        var login = FieldName(form, "input[type='text']");
        var password = FieldName(form, "input[type='password']");
        if (login is null || password is null) return null;

        return new Ao3LoginForm(action, token, login, password, FieldName(form, "input[type='checkbox']"));
    }

    private static string? FieldName(IElement form, string selector)
    {
        var name = form.QuerySelector(selector)?.GetAttribute("name");
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Whether the page in front of us was served to our session, which is the only way to find out
    /// that a cached cookie has stopped working: AO3 answers an expired session with the logged-out
    /// view of the page and a 200, not with a 401.
    ///
    /// Both markers are structural parts of AO3's header, present on every rendered page:
    /// <c>nav#greeting</c> holds "Hi, {user}!" and a logout link for a signed-in reader, and
    /// <c>#new_user_session_small</c> is the login dropdown offered to everyone else. A page
    /// carrying neither is <see cref="Ao3SessionState.Unknown"/> and proves nothing — which is the
    /// answer that must never be mistaken for "logged out", because acting on it would throw away a
    /// working session over a 404.
    /// </summary>
    public static Ao3SessionReading ReadSessionState(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return new Ao3SessionReading(Ao3SessionState.Unknown);

        var document = Parser.ParseDocument(html);

        var greeting = document.QuerySelector("nav#greeting");
        if (greeting is not null)
            return new Ao3SessionReading(Ao3SessionState.LoggedIn, GreetedUsername(greeting));

        if (document.QuerySelector("form#new_user_session_small") is not null ||
            document.QuerySelector("form#new_user") is not null)
        {
            return new Ao3SessionReading(Ao3SessionState.LoggedOut);
        }

        return new Ao3SessionReading(Ao3SessionState.Unknown);
    }

    /// <summary>
    /// The account name out of the greeting's dashboard link (<c>/users/{name}</c>), which is the
    /// machine-readable half of "Hi, {name}!". Null rather than a guess if the link has moved —
    /// the name is a nicety for the log, never the thing the session check turns on.
    /// </summary>
    private static string? GreetedUsername(IElement greeting)
    {
        var href = greeting.QuerySelector("a[href*='/users/']")?.GetAttribute("href");
        if (string.IsNullOrWhiteSpace(href)) return null;

        var segments = href.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(segments, "users");
        if (index < 0 || index + 1 >= segments.Length) return null;

        var name = segments[index + 1].Split('?')[0];
        return string.IsNullOrWhiteSpace(name) ? null : Uri.UnescapeDataString(name);
    }
}
