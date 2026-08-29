using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// Reading AO3's login page.
///
/// The whole difficulty is that the page carries the login form twice — the header dropdown and the
/// real one — and in the capture both hidden tokens hold the same value. So a parser that grabs the
/// first <c>authenticity_token</c> in the document passes every naive test while reading the wrong
/// form, and the tests below have to construct the difference rather than wait for AO3 to.
/// </summary>
public class Ao3LoginPageTests
{
    private static readonly string Capture = Fixtures.Load(Fixtures.LoginPage);

    /// <summary>The token AO3 actually served, present in both of the page's forms.</summary>
    private const string CapturedToken =
        "FIXTURE-AUTHENTICITY-TOKEN-REDACTED";

    [Fact]
    public void Reads_the_authenticity_token_off_the_captured_page()
    {
        var form = Ao3LoginPage.ParseLoginForm(Capture);

        Assert.NotNull(form);
        Assert.Equal(CapturedToken, form.AuthenticityToken);
    }

    [Fact]
    public void Takes_the_login_forms_token_and_not_the_header_dropdowns_or_the_head_tags()
    {
        // The capture carries the same token in three places, so it cannot tell three different
        // parsers apart on its own: a <meta name="csrf-token"> in the head, the header dropdown's
        // hidden input, and the real form's. Giving the first two distinct values is what makes the
        // difference observable — everything else is the page exactly as AO3 served it, and the
        // ordering here is the document's own.
        var distinguishable = ReplaceNthToken(Capture, 0, "the-head-tags-token");
        distinguishable = ReplaceNthToken(distinguishable, 0, "the-header-dropdowns-token");

        var form = Ao3LoginPage.ParseLoginForm(distinguishable);

        Assert.NotNull(form);
        Assert.Equal(CapturedToken, form.AuthenticityToken);
    }

    /// <summary>
    /// Replaces the <paramref name="index"/>-th remaining copy of the captured token, so a test can
    /// name one of the three places it appears without rewriting the other two.
    /// </summary>
    private static string ReplaceNthToken(string html, int index, string replacement)
    {
        var at = -1;
        for (var found = 0; found <= index; found++)
        {
            at = html.IndexOf(CapturedToken, at + 1, StringComparison.Ordinal);
            Assert.True(at >= 0, $"The capture holds fewer than {index + 1} copies of the token.");
        }

        return string.Concat(
            html.AsSpan(0, at), replacement, html.AsSpan(at + CapturedToken.Length));
    }

    [Fact]
    public void The_capture_really_does_hold_the_token_three_times()
    {
        // The test above is only evidence while this is true. If AO3 stops repeating the token, that
        // test silently stops distinguishing anything and this is what says so.
        var occurrences = 0;
        for (var at = Capture.IndexOf(CapturedToken, StringComparison.Ordinal);
             at >= 0;
             at = Capture.IndexOf(CapturedToken, at + 1, StringComparison.Ordinal))
        {
            occurrences++;
        }

        Assert.Equal(3, occurrences);
    }

    [Fact]
    public void Reads_where_the_form_posts_rather_than_assuming_it()
    {
        Assert.Equal("/users/login", Ao3LoginPage.ParseLoginForm(Capture)!.Action);
    }

    [Fact]
    public void Reads_the_field_names_off_the_form_it_will_post_to()
    {
        var form = Ao3LoginPage.ParseLoginForm(Capture);

        Assert.NotNull(form);
        Assert.Equal("user[login]", form.LoginField);
        Assert.Equal("user[password]", form.PasswordField);
        Assert.Equal("user[remember_me]", form.RememberMeField);
    }

    [Fact]
    public void Follows_a_rename_of_the_fields_instead_of_posting_the_old_names()
    {
        // Reading the names is only worth doing if a change to them is actually followed; otherwise
        // the parser is a hardcoded list with extra steps.
        var renamed = Capture
            .Replace("name=\"user[login]\"", "name=\"account[handle]\"")
            .Replace("name=\"user[password]\"", "name=\"account[secret]\"");

        var form = Ao3LoginPage.ParseLoginForm(renamed);

        Assert.NotNull(form);
        Assert.Equal("account[handle]", form.LoginField);
        Assert.Equal("account[secret]", form.PasswordField);
    }

    [Fact]
    public void Finds_no_form_on_a_page_that_is_not_the_login_page()
    {
        // A markup change or a redirect elsewhere must read as "no form here", not as a token of
        // whatever the page happened to contain.
        Assert.Null(Ao3LoginPage.ParseLoginForm(Fixtures.Load(Fixtures.WorkPage)));
    }

    [Fact]
    public void Finds_no_form_when_the_form_carries_no_token()
    {
        // Posting without one is a guaranteed rejection, so a tokenless form is not a form.
        var stripped = Capture.Replace($"value=\"{CapturedToken}\"", "value=\"\"");

        Assert.Null(Ao3LoginPage.ParseLoginForm(stripped));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<html><body>nothing here</body></html>")]
    public void Finds_no_form_in_markup_that_has_none(string? html)
    {
        Assert.Null(Ao3LoginPage.ParseLoginForm(html));
    }
}

/// <summary>
/// Telling a page served to our session from the same page served to nobody.
///
/// AO3 answers an expired session with a 200 and the logged-out view, never a 401, so this reading
/// is the only thing that can notice a cached cookie has stopped working.
/// </summary>
public class Ao3LoginSessionStateTests
{
    [Fact]
    public void Reads_a_page_captured_while_logged_in_as_logged_in()
    {
        var reading = Ao3LoginPage.ReadSessionState(Fixtures.Load(Fixtures.WorkPage));

        Assert.Equal(Ao3SessionState.LoggedIn, reading.State);
        Assert.Equal("fixtureuser", reading.Username);
    }

    [Fact]
    public void Reads_a_second_logged_in_capture_the_same_way()
    {
        // A different page shape entirely — a works index rather than a work — so the marker being
        // read is the header every page shares, not something incidental to one capture.
        var reading = Ao3LoginPage.ReadSessionState(Fixtures.Load(Fixtures.EmptyListing));

        Assert.Equal(Ao3SessionState.LoggedIn, reading.State);
        Assert.Equal("fixtureuser", reading.Username);
    }

    [Fact]
    public void Reads_the_login_page_as_logged_out()
    {
        var reading = Ao3LoginPage.ReadSessionState(Fixtures.Load(Fixtures.LoginPage));

        Assert.Equal(Ao3SessionState.LoggedOut, reading.State);
        Assert.Null(reading.Username);
    }

    [Fact]
    public void Reads_a_logged_in_capture_stripped_of_its_greeting_as_logged_out()
    {
        // The real regression this guards: an expired cookie turns a page we have seen logged in
        // into the anonymous view of itself, header and all.
        var loggedOut = Fixtures.Load(Fixtures.WorkPage)
            .Replace("<nav id=\"greeting\"", "<nav id=\"was-greeting\"")
            + "<form id=\"new_user_session_small\"></form>";

        Assert.Equal(Ao3SessionState.LoggedOut, Ao3LoginPage.ReadSessionState(loggedOut).State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html><body>404 Not Found</body></html>")]
    public void Reads_a_page_carrying_neither_marker_as_unknown(string? html)
    {
        // Never LoggedOut. A 404, an error page or a file body proves nothing about the session,
        // and treating "no evidence" as "logged out" would discard a working session over one.
        Assert.Equal(Ao3SessionState.Unknown, Ao3LoginPage.ReadSessionState(html).State);
    }

    [Fact]
    public void Still_reads_a_greeting_it_cannot_name_as_logged_in()
    {
        // The username is a nicety for the log; losing it must not lose the state.
        var noLink = "<html><body><nav id=\"greeting\"><p>Hi!</p></nav></body></html>";

        var reading = Ao3LoginPage.ReadSessionState(noLink);

        Assert.Equal(Ao3SessionState.LoggedIn, reading.State);
        Assert.Null(reading.Username);
    }
}
