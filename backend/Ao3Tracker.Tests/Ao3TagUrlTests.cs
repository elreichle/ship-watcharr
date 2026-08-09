using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// The tag-to-URL transform. Worth its own tests because it is the one piece of AO3's addressing
/// scheme the app has to reproduce exactly: percent-encoding alone produces a URL AO3 404s on, and
/// the failure appears as a scrape that finds no works rather than as an error.
/// </summary>
public class Ao3TagUrlTests
{
    [Fact]
    public void Escapes_the_slash_in_a_romantic_pairing()
    {
        // The single most common shape of tag this app takes, and the reason a plain
        // Uri.EscapeDataString is not enough: %2F would be read as a path separator.
        Assert.Equal("Clarke%20Griffin*s*Lexa", Ao3TagUrl.ToUrlSegment("Clarke Griffin/Lexa"));
    }

    [Fact]
    public void Escapes_the_ampersand_in_a_platonic_pairing()
    {
        // AO3 writes these with spaces either side of the ampersand, and they survive as %20 —
        // only the ampersand itself is substituted.
        Assert.Equal(
            "Sam%20Winchester%20*a*%20Dean%20Winchester",
            Ao3TagUrl.ToUrlSegment("Sam Winchester & Dean Winchester"));
    }

    [Theory]
    [InlineData("a/b", "a*s*b")]
    [InlineData("a&b", "a*a*b")]
    [InlineData("a.b", "a*d*b")]
    [InlineData("a?b", "a*q*b")]
    [InlineData("a#b", "a*h*b")]
    public void Substitutes_every_character_AO3_cannot_route(string tagName, string expected)
    {
        Assert.Equal(expected, Ao3TagUrl.ToUrlSegment(tagName));
    }

    [Fact]
    public void Escapes_several_slashes_in_a_poly_ship()
    {
        Assert.Equal("Kirk*s*Spock*s*McCoy", Ao3TagUrl.ToUrlSegment("Kirk/Spock/McCoy"));
    }

    [Fact]
    public void Leaves_the_digraph_delimiters_unencoded()
    {
        // The whole transform collapses if the '*' it just wrote comes back as %2A, which is
        // exactly what .NET's escaper does to it by default.
        Assert.DoesNotContain("%2A", Ao3TagUrl.ToUrlSegment("a/b&c.d"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Percent_encodes_everything_else()
    {
        Assert.Equal("Steve%20Rogers%20%7C%20Nomad", Ao3TagUrl.ToUrlSegment("Steve Rogers | Nomad"));
    }

    [Fact]
    public void Keeps_a_non_ascii_name_intact()
    {
        // Encoded whole-string rather than per character. A per-character loop splits a surrogate
        // pair and produces two invalid UTF-8 sequences, which no round trip would recover.
        Assert.Equal("%F0%9F%92%96", Ao3TagUrl.ToUrlSegment("\U0001F496"));
    }

    // ---- reading a tag back out of a URL -------------------------------------------------------

    [Theory]
    [InlineData("Clarke Griffin/Lexa")]
    [InlineData("Sam Winchester & Dean Winchester")]
    [InlineData("Kirk/Spock/McCoy")]
    [InlineData("Dr. Strange")]
    [InlineData("Steve Rogers | Nomad")]
    [InlineData("\U0001F496")]
    public void Round_trips_a_tag_through_its_url_segment(string tagName)
    {
        // The property that matters for synonym resolution: a canonical tag read back out of a
        // redirect URL has to be the string AO3 would have rendered, or the ship gets renamed to
        // something subtly wrong.
        Assert.Equal(tagName, Ao3TagUrl.FromUrlSegment(Ao3TagUrl.ToUrlSegment(tagName)));
    }

    [Fact]
    public void Finds_the_tag_in_a_works_index_url()
    {
        Assert.Equal(
            "Clarke%20Griffin*s*Lexa",
            Ao3TagUrl.TryGetTagSegment("https://archiveofourown.org/tags/Clarke%20Griffin*s*Lexa/works"));
    }

    [Theory]
    [InlineData("https://archiveofourown.org/users/login")]
    [InlineData("https://archiveofourown.org/tags/Clarke*s*Lexa")]
    [InlineData("https://archiveofourown.org/tags/Clarke*s*Lexa/bookmarks")]
    [InlineData("https://archiveofourown.org/works/12345")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void Refuses_to_read_a_tag_out_of_anything_else(string? url)
    {
        // Null here is what stops a redirect to a login or error page renaming someone's ship to
        // "login". Every one of these must be an inconclusive check, not an answer.
        Assert.Null(Ao3TagUrl.TryGetTagSegment(url));
    }

    [Fact]
    public void Substitutes_a_dot_before_it_can_be_mistaken_for_a_file_extension()
    {
        // Rails routes treat a trailing .format as a response format, which is why AO3 escapes the
        // dot at all rather than leaving a legal path character alone.
        Assert.Equal("Dr*d*%20Strange", Ao3TagUrl.ToUrlSegment("Dr. Strange"));
    }
}
