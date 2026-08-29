using System.Net;
using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// The AO3 login round trip: fetch the form, post the credential, keep what came back.
///
/// The failure that matters most here is the quiet one. A rejected login is not an error status —
/// AO3 answers it with a 200 and the login page again — so a client that only checked the status
/// code would store a logged-out session, report success, and then scrape the anonymous half of the
/// archive for ever.
/// </summary>
public class Ao3LoginEstablisherTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    /// <summary>The token in the capture, which is what the POST has to carry.</summary>
    private const string CapturedToken =
        "FIXTURE-AUTHENTICITY-TOKEN-REDACTED";

    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- the happy path -----------------------------------------------------------------------

    [Fact]
    public async Task Reads_the_login_page_before_posting_to_it()
    {
        await _host.SaveAo3LoginAsync("shipwatcharr", Password);

        Assert.True((await _host.LogInToAo3Async()).Success);

        Assert.Equal("https://ao3.test/users/login", Assert.Single(_host.Http.LoginPagesRequested));
        Assert.Equal("https://ao3.test/users/login", Assert.Single(_host.Http.Posted).Url);
    }

    [Fact]
    public async Task Posts_the_stored_credential_under_the_forms_own_field_names()
    {
        await _host.SaveAo3LoginAsync("shipwatcharr", Password);

        await _host.LogInToAo3Async();

        var fields = Assert.Single(_host.Http.Posted).Fields;
        Assert.Equal("shipwatcharr", fields["user[login]"]);
        Assert.Equal(Password, fields["user[password]"]);
    }

    [Fact]
    public async Task Posts_the_token_the_login_form_carried()
    {
        // Without it Rails refuses the POST outright, and the refusal looks exactly like a wrong
        // password — so an omitted token would read as "the operator typed it wrong".
        await _host.SaveAo3LoginAsync();

        await _host.LogInToAo3Async();

        Assert.Equal(CapturedToken, Assert.Single(_host.Http.Posted).Fields["authenticity_token"]);
    }

    [Fact]
    public async Task Carries_the_cookies_the_login_page_set()
    {
        // Rails binds the authenticity token to the session that served it. The token alone is
        // rejected the same way no token at all is.
        await _host.SaveAo3LoginAsync();

        await _host.LogInToAo3Async();

        Assert.Equal("_otwarchive_session=before-login", Assert.Single(_host.Http.Posted).CookieHeader);
    }

    [Fact]
    public async Task Asks_to_be_remembered_so_it_has_to_log_in_less_often()
    {
        // Every login is two requests AO3 would rather not serve. A remembered session is the
        // politeness measure here, not a convenience.
        await _host.SaveAo3LoginAsync();

        await _host.LogInToAo3Async();

        Assert.Equal("1", Assert.Single(_host.Http.Posted).Fields["user[remember_me]"]);
    }

    [Fact]
    public async Task Stores_the_session_the_login_response_set()
    {
        await _host.SaveAo3LoginAsync();

        var result = await _host.LogInToAo3Async();

        Assert.True(result.Success);
        var session = await _host.WithCredentialStoreAsync(store => store.GetSessionAsync());
        Assert.NotNull(session);
        Assert.Equal("_otwarchive_session=logged-in", session.SessionCookie);
    }

    [Fact]
    public async Task Keeps_a_login_AO3_answers_with_a_greeting_rather_than_a_redirect()
    {
        // A 200 is not in itself a rejection. What settles it is whether the page greets an account.
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            Fixtures.Load(Fixtures.WorkPage), HttpStatusCode.OK, FromCache: false, FinalUrl: url,
            SetCookieHeaders: ["_otwarchive_session=logged-in; path=/"]);

        await _host.SaveAo3LoginAsync();

        var result = await _host.LogInToAo3Async();

        Assert.True(result.Success, result.Error);
        Assert.Equal("fixtureuser", result.Username);
    }

    [Fact]
    public async Task Reads_the_expiry_off_the_cookie_AO3_set()
    {
        _host.Clock.Now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            "", HttpStatusCode.Found, FromCache: false, FinalUrl: url,
            SetCookieHeaders: ["remember_user_token=abc; path=/; Max-Age=1209600"],
            Location: "https://ao3.test/users/shipwatcharr");

        await _host.SaveAo3LoginAsync();
        await _host.LogInToAo3Async();

        var session = await _host.WithCredentialStoreAsync(store => store.GetSessionAsync());
        Assert.Equal(_host.Clock.Now.UtcDateTime.AddDays(14), session!.ExpiresAt);
    }

    [Fact]
    public async Task Treats_a_cookie_with_no_stated_expiry_as_open_ended()
    {
        // Null means "usable until a page comes back logged out", which is the only honest reading
        // of a session cookie — inventing a lifetime would re-login on a timer for no reason.
        await _host.SaveAo3LoginAsync();
        await _host.LogInToAo3Async();

        var session = await _host.WithCredentialStoreAsync(store => store.GetSessionAsync());
        Assert.Null(session!.ExpiresAt);
    }

    // ---- refusals -----------------------------------------------------------------------------

    [Fact]
    public async Task Refuses_a_login_AO3_sends_straight_back_to_the_login_page()
    {
        // How AO3 turns away an account it will not sign in. The status is a 302 either way, so
        // only where it points tells the two apart.
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            "", HttpStatusCode.Found, FromCache: false, FinalUrl: url,
            SetCookieHeaders: ["_otwarchive_session=still-nobody; path=/"],
            Location: "https://ao3.test/users/login");

        await _host.SaveAo3LoginAsync();

        var result = await _host.LogInToAo3Async();

        Assert.False(result.Success);
        Assert.Null(await _host.WithCredentialStoreAsync(store => store.GetSessionAsync()));
    }

    [Fact]
    public async Task Refuses_a_login_answered_with_the_login_page_again()
    {
        // The wrong-password case, and the one a status-code check would sail straight past.
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            Fixtures.Load(Fixtures.LoginPage), HttpStatusCode.OK, FromCache: false, FinalUrl: url,
            SetCookieHeaders: ["_otwarchive_session=still-nobody; path=/"]);

        await _host.SaveAo3LoginAsync();

        var result = await _host.LogInToAo3Async();

        Assert.False(result.Success);
        Assert.Null(await _host.WithCredentialStoreAsync(store => store.GetSessionAsync()));
    }

    [Fact]
    public async Task Says_nothing_about_the_password_when_it_reports_a_refusal()
    {
        // The error goes into a log and onto an admin screen. Neither is a place for a password.
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            Fixtures.Load(Fixtures.LoginPage), HttpStatusCode.OK, FromCache: false, FinalUrl: url);

        await _host.SaveAo3LoginAsync("shipwatcharr", Password);

        var result = await _host.LogInToAo3Async();

        Assert.NotNull(result.Error);
        Assert.DoesNotContain(Password, result.Error);
    }

    [Fact]
    public async Task Refuses_a_login_that_set_no_cookies_of_its_own()
    {
        // The form fetch already put an anonymous `_otwarchive_session` in the jar, so "are we
        // holding any cookies" answers yes however little the POST did. Rails rotates the session on
        // sign-in; a login that sets nothing has signed nothing in, and storing the pre-login cookie
        // would leave every later run believing it was logged in until a page proved otherwise.
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            "", HttpStatusCode.Found, FromCache: false, FinalUrl: url,
            Location: "https://ao3.test/users/shipwatcharr");

        await _host.SaveAo3LoginAsync();

        var result = await _host.LogInToAo3Async();

        Assert.False(result.Success);
        Assert.Null(await _host.WithCredentialStoreAsync(store => store.GetSessionAsync()));
    }

    [Fact]
    public async Task Refuses_a_login_whose_only_cookie_was_one_it_deleted()
    {
        // A response that retires a cookie has established nothing, even though it carried a
        // Set-Cookie header. Counted the other way, the pre-login cookie would be stored again.
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            "", HttpStatusCode.Found, FromCache: false, FinalUrl: url,
            SetCookieHeaders: ["remember_user_token=; Max-Age=0"],
            Location: "https://ao3.test/users/shipwatcharr");

        await _host.SaveAo3LoginAsync();

        Assert.False((await _host.LogInToAo3Async()).Success);
        Assert.Null(await _host.WithCredentialStoreAsync(store => store.GetSessionAsync()));
    }

    [Fact]
    public async Task Keeps_the_login_pages_cookies_alongside_the_ones_the_login_added()
    {
        // The jar is cumulative: AO3 may rotate only the session cookie and leave the rest standing.
        _host.Http.RespondsToLoginPage = url => new ScrapeHttpResponse(
            Fixtures.Load(Fixtures.LoginPage), HttpStatusCode.OK, FromCache: false, FinalUrl: url,
            SetCookieHeaders: ["view_adult=true; path=/"]);
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            "", HttpStatusCode.Found, FromCache: false, FinalUrl: url,
            SetCookieHeaders: ["_otwarchive_session=logged-in; path=/"],
            Location: "https://ao3.test/users/shipwatcharr");

        await _host.SaveAo3LoginAsync();
        Assert.True((await _host.LogInToAo3Async()).Success);

        var session = await _host.WithCredentialStoreAsync(store => store.GetSessionAsync());
        Assert.Contains("view_adult=true", session!.SessionCookie);
        Assert.Contains("_otwarchive_session=logged-in", session.SessionCookie);
    }

    [Fact]
    public async Task Does_not_let_an_unrelated_short_lived_cookie_shorten_the_session()
    {
        // A banner or flash cookie with a few minutes on it must not cap the whole session's life —
        // that would turn a fortnight-long login into a two-request round trip every few minutes.
        // It expires out of the jar on its own; the session outlives it.
        _host.Clock.Now = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            "", HttpStatusCode.Found, FromCache: false, FinalUrl: url,
            SetCookieHeaders:
            [
                "_otwarchive_session=logged-in; path=/",
                "flash_notice=hello; Max-Age=0",
            ],
            Location: "https://ao3.test/users/shipwatcharr");

        await _host.SaveAo3LoginAsync();
        Assert.True((await _host.LogInToAo3Async()).Success);

        var session = await _host.WithCredentialStoreAsync(store => store.GetSessionAsync());
        Assert.Null(session!.ExpiresAt);
        Assert.DoesNotContain("flash_notice", session.SessionCookie);
    }

    // ---- giving up before posting anything ----------------------------------------------------

    [Fact]
    public async Task Sends_nothing_at_all_when_no_credential_is_stored()
    {
        var result = await _host.LogInToAo3Async();

        Assert.False(result.Success);
        Assert.Empty(_host.Http.LoginPagesRequested);
        Assert.Empty(_host.Http.Posted);
    }

    [Fact]
    public async Task Posts_nothing_when_the_login_page_carries_no_form()
    {
        // A markup change. Posting a guessed form to AO3 would be a request that cannot succeed.
        _host.Http.RespondsToLoginPage = url => new ScrapeHttpResponse(
            Fixtures.Load(Fixtures.WorkPage), HttpStatusCode.OK, FromCache: false, FinalUrl: url);

        await _host.SaveAo3LoginAsync();

        var result = await _host.LogInToAo3Async();

        Assert.False(result.Success);
        Assert.Empty(_host.Http.Posted);
        Assert.Contains("markup", result.Error);
    }

    [Fact]
    public async Task Posts_nothing_to_a_form_that_names_a_host_other_than_the_archive()
    {
        // The one request in this application that carries the operator's AO3 username and their
        // decrypted password. The action is read off a fetched page, so without this check a login
        // page whose form posted elsewhere would send both there — and the deployment would go on
        // reporting a failed login rather than a leaked credential.
        _host.Http.RespondsToLoginPage = url => new ScrapeHttpResponse(
            LoginFormPostingTo("https://elsewhere.example/collect"),
            HttpStatusCode.OK, FromCache: false, FinalUrl: url);

        await _host.SaveAo3LoginAsync();

        var result = await _host.LogInToAo3Async();

        Assert.False(result.Success);
        Assert.Empty(_host.Http.Posted);
        Assert.Contains("elsewhere.example", result.Error);
    }

    [Fact]
    public async Task Posts_to_an_absolute_action_the_archive_itself_names()
    {
        // Which archive it addresses is the check, not "is it relative". AO3 is free to render its
        // own form action absolutely, and a rule that refused that would refuse every login.
        _host.Http.RespondsToLoginPage = url => new ScrapeHttpResponse(
            LoginFormPostingTo("https://ao3.test/users/login"),
            HttpStatusCode.OK, FromCache: false, FinalUrl: url,
            SetCookieHeaders: ["_otwarchive_session=before-login; path=/; HttpOnly"]);

        await _host.SaveAo3LoginAsync();

        Assert.True((await _host.LogInToAo3Async()).Success);
        Assert.Equal("https://ao3.test/users/login", Assert.Single(_host.Http.Posted).Url);
    }

    [Fact]
    public async Task Posts_nothing_to_a_form_that_asks_for_the_password_unencrypted()
    {
        // Same host, one scheme down. The value being posted is a plaintext password, so what the
        // archive is measured by here is its whole origin and not only its name — a form action of
        // http:// on an https:// deployment puts the credential on the wire in the clear, and is
        // not something the real archive has ever asked for.
        _host.Http.RespondsToLoginPage = url => new ScrapeHttpResponse(
            LoginFormPostingTo("http://ao3.test/users/login"),
            HttpStatusCode.OK, FromCache: false, FinalUrl: url);

        await _host.SaveAo3LoginAsync();

        var result = await _host.LogInToAo3Async();

        Assert.False(result.Success);
        Assert.Empty(_host.Http.Posted);
    }

    [Fact]
    public async Task Posts_nothing_when_the_login_page_is_not_served()
    {
        _host.Http.RespondsToLoginPage = _ =>
            new ScrapeHttpResponse("", HttpStatusCode.ServiceUnavailable, FromCache: false);

        await _host.SaveAo3LoginAsync();

        var result = await _host.LogInToAo3Async();

        Assert.False(result.Success);
        Assert.Empty(_host.Http.Posted);
        Assert.Contains("503", result.Error);
    }

    [Fact]
    public async Task Reports_an_unreachable_archive_rather_than_throwing()
    {
        // A thrown login reaches the worker's outer handler as "unhandled error while polling",
        // which loses the one thing an operator needs: that the login is what did not happen. AO3
        // being down is a state this application expects, not an exceptional one.
        _host.Http.Fails = new HttpRequestException("Connection refused");

        await _host.SaveAo3LoginAsync();

        var result = await _host.LogInToAo3Async();

        Assert.False(result.Success);
        Assert.Contains("could not reach AO3", result.Error);
        Assert.Null(await _host.WithCredentialStoreAsync(store => store.GetSessionAsync()));
    }

    [Fact]
    public async Task Keeps_the_stored_password_when_a_login_is_refused()
    {
        // The credential is the durable thing and the session is the cache. A refused login must
        // never cost the operator the password they typed — even when the password is the problem.
        _host.Http.RespondsToPost = url => new ScrapeHttpResponse(
            Fixtures.Load(Fixtures.LoginPage), HttpStatusCode.OK, FromCache: false, FinalUrl: url);

        await _host.SaveAo3LoginAsync("shipwatcharr", Password);
        await _host.LogInToAo3Async();

        var credential = await _host.WithCredentialStoreAsync(store => store.GetDecryptedCredentialAsync());
        Assert.Equal(Password, credential!.Value.Ao3Password);
    }

    /// <summary>The captured login form, with its action replaced by <paramref name="action"/>.</summary>
    private static string LoginFormPostingTo(string action)
    {
        var page = Fixtures.Load(Fixtures.LoginPage);
        var form = Ao3LoginPage.ParseLoginForm(page);

        Assert.NotNull(form);
        Assert.NotEqual(action, form.Action);

        return page.Replace($"action=\"{form.Action}\"", $"action=\"{action}\"");
    }
}
