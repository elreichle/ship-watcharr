using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// Contact validation. The failure this guards against is subtle: a contact that cannot be
/// contacted is worse than none at all, because it looks cooperative to AO3 while being useless.
/// </summary>
public class OperatorContactValidationTests
{
    [Theory]
    [InlineData("emma@example.com")]
    [InlineData("emma+ao3@example.com")]
    [InlineData("first.last@sub.example.co.uk")]
    [InlineData("https://github.com/someone/ship-watcharr")]
    [InlineData("http://example.com/about")]
    public void Accepts_reachable_contacts(string contact)
    {
        Assert.True(Ao3UserAgentProvider.ValidateContact(contact, out var error), error);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_missing_contacts(string? contact)
    {
        Assert.False(Ao3UserAgentProvider.ValidateContact(contact, out var error));
        Assert.Contains("scraping is disabled", error);
    }

    [Theory]
    [InlineData("emma")]                 // a bare username is not reachable
    [InlineData("emma@localhost")]       // no dot in the domain
    [InlineData("@example.com")]         // no local part
    [InlineData("emma@")]                // no domain
    [InlineData("emma@example.")]        // trailing dot
    [InlineData("ftp://example.com")]    // not a scheme anyone can contact you through
    [InlineData("ShipWatcharr")]
    public void Rejects_unreachable_contacts(string contact)
    {
        Assert.False(Ao3UserAgentProvider.ValidateContact(contact, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("emma@example.com; evil/1.0")]   // would inject a second product token
    [InlineData("emma@example.com) (spoof")]     // would close the User-Agent comment early
    [InlineData("emma@example.com\nX-Evil: 1")]  // would split the header
    [InlineData("emma@example.com\r\nHost: x")]
    public void Rejects_contacts_that_would_corrupt_the_header(string contact)
    {
        Assert.False(Ao3UserAgentProvider.ValidateContact(contact, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Error_for_a_missing_contact_tells_the_operator_where_to_set_it()
    {
        Ao3UserAgentProvider.ValidateContact(null, out var error);

        // An error nobody can act on is the reason this whole path exists; keep it actionable.
        // The page moved from Settings to System when the sidebar gained a Settings/System split,
        // so this asserts the current location rather than just the word "Settings".
        Assert.Contains("admin account", error);
        Assert.Contains("System → Scraping", error);
    }
}
