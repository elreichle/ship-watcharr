using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// Turning a response's <c>Set-Cookie</c> headers into the next request's <c>Cookie</c> header.
///
/// Small, and worth pinning because the whole session is one string built here: a header assembled
/// wrongly is a session that silently is not one, which AO3 answers with the anonymous view rather
/// than with an error.
/// </summary>
public class Ao3LoginCookieTests
{
    private static readonly DateTime Now = new(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyDictionary<string, Ao3Cookie> Apply(params string[] headers) =>
        Ao3Cookies.Apply(Ao3Cookies.Empty, headers, Now);

    private static DateTime? ExpiryOf(params string[] headers) =>
        Ao3Cookies.EarliestExpiry(Apply(headers));

    [Fact]
    public void Keeps_the_name_and_value_of_a_cookie_and_drops_its_attributes()
    {
        var jar = Apply("_otwarchive_session=abc123; path=/; HttpOnly; SameSite=Lax");

        Assert.Equal("_otwarchive_session=abc123", Ao3Cookies.ToHeader(jar));
    }

    [Fact]
    public void Carries_every_cookie_the_response_set()
    {
        var jar = Apply("_otwarchive_session=abc; path=/", "remember_user_token=xyz; path=/");

        var header = Ao3Cookies.ToHeader(jar);
        Assert.Contains("_otwarchive_session=abc", header);
        Assert.Contains("remember_user_token=xyz", header);
    }

    [Fact]
    public void Lets_a_later_response_replace_a_cookie_it_already_held()
    {
        // Rails rotates the session id on login. Holding both values would send two copies of one
        // cookie, and the old one first.
        var before = Apply("_otwarchive_session=before; path=/");
        var after = Ao3Cookies.Apply(before, ["_otwarchive_session=after; path=/"], Now);

        Assert.Equal("_otwarchive_session=after", Ao3Cookies.ToHeader(after));
    }

    [Fact]
    public void Forgets_a_cookie_the_server_blanked()
    {
        var before = Apply("remember_user_token=xyz; path=/");
        var after = Ao3Cookies.Apply(before, ["remember_user_token=; path=/"], Now);

        Assert.Null(Ao3Cookies.ToHeader(after));
    }

    [Fact]
    public void Forgets_a_cookie_the_server_expired()
    {
        // The other half of how a server retires one: a value with a date already gone by.
        var before = Apply("remember_user_token=xyz; path=/");
        var after = Ao3Cookies.Apply(
            before, ["remember_user_token=xyz; path=/; expires=Thu, 01 Jan 1970 00:00:00 GMT"], Now);

        Assert.Null(Ao3Cookies.ToHeader(after));
    }

    [Fact]
    public void Has_no_header_to_send_when_nothing_was_set()
    {
        Assert.Null(Ao3Cookies.ToHeader(Apply()));
    }

    [Fact]
    public void Ignores_a_header_it_cannot_read_rather_than_throwing()
    {
        // AO3's markup moves and so do its headers. One unparseable cookie must not cost the login.
        var jar = Apply("this is not a cookie", "_otwarchive_session=abc; path=/");

        Assert.Equal("_otwarchive_session=abc", Ao3Cookies.ToHeader(jar));
    }

    // ---- expiry --------------------------------------------------------------------------------

    [Fact]
    public void Reads_an_expiry_from_max_age()
    {
        var expiry = ExpiryOf("remember_user_token=x; Max-Age=1209600");

        Assert.Equal(Now.AddDays(14), expiry);
    }

    [Fact]
    public void Reads_an_expiry_from_an_expires_date()
    {
        var expiry = ExpiryOf("remember_user_token=x; expires=Tue, 08 Sep 2026 12:00:00 GMT");

        Assert.Equal(new DateTime(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc), expiry);
    }

    [Fact]
    public void Prefers_max_age_over_expires_where_a_cookie_carries_both()
    {
        // What the cookie spec requires, and it matters here: the two disagree whenever a clock is
        // off, and Max-Age is the one that does not depend on ours matching AO3's.
        var expiry = ExpiryOf("t=x; expires=Tue, 08 Sep 2026 12:00:00 GMT; Max-Age=3600");

        Assert.Equal(Now.AddHours(1), expiry);
    }

    [Fact]
    public void Takes_the_soonest_expiry_across_the_cookies_it_kept()
    {
        // The session is only whole while all of its parts are.
        var expiry = ExpiryOf("a=1; Max-Age=1209600", "b=2; Max-Age=3600", "c=3; Max-Age=86400");

        Assert.Equal(Now.AddHours(1), expiry);
    }

    [Fact]
    public void Has_no_expiry_when_every_cookie_is_a_session_cookie()
    {
        // Null is "usable until a page comes back logged out", not "usable forever".
        Assert.Null(ExpiryOf("_otwarchive_session=abc; path=/; HttpOnly"));
    }

    [Fact]
    public void Ignores_the_expiry_of_a_cookie_that_is_being_deleted()
    {
        // A blanked cookie's date is in the past by construction. Counting it would date the whole
        // session to 1970 and force a login on every single request.
        var expiry = ExpiryOf("remember_user_token=; expires=Thu, 01 Jan 1970 00:00:00 GMT", "s=abc; Max-Age=3600");

        Assert.Equal(Now.AddHours(1), expiry);
    }

    [Fact]
    public void Ignores_a_deletion_that_kept_its_value_and_zeroed_its_max_age()
    {
        // The other spelling of a deletion, and the dangerous one: the value is still there, so a
        // check that only skipped blank cookies would count `Max-Age=0` as "expires now" — dating
        // the session to this instant and sending the instance back to log in on every poll for
        // ever. This is why the expiry is read off the jar rather than off the headers.
        var expiry = ExpiryOf("banner_seen=1; Max-Age=0", "_otwarchive_session=abc; Max-Age=3600");

        Assert.Equal(Now.AddHours(1), expiry);
    }

    [Fact]
    public void Drops_a_cookie_whose_max_age_is_zero()
    {
        Assert.Equal("_otwarchive_session=abc", Ao3Cookies.ToHeader(
            Apply("banner_seen=1; Max-Age=0", "_otwarchive_session=abc; path=/")));
    }
}
