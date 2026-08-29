using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// What a work's own page says about downloading it.
///
/// The whole reason this seam exists is that the address cannot be computed: the slug is a
/// truncation of the title whose rule AO3 states nowhere, and <c>updated_at</c> is AO3's own
/// timestamp rather than the one the library holds. So every assertion here reads a href out of
/// the captured page rather than comparing against a pattern this test built.
/// </summary>
public class Ao3DownloadLinksTests
{
    /// <summary>The archive this deployment is configured for — what every link is measured against.</summary>
    private const string ArchiveUrl = "https://archiveofourown.org";

    /// <summary>
    /// Where the page was served from. The base a relative href resolves against, and nothing more:
    /// the work page is fetched with redirects followed, so this is AO3's to decide.
    /// </summary>
    private const string WorkPageUrl = "https://archiveofourown.org/works/70441196";

    /// <summary>
    /// One case per member of <see cref="Ao3DownloadFormat"/>, each address copied out of
    /// <c>ao3-work-page.html</c> rather than built from the shape the others share. Written this way
    /// on purpose: a table whose expectations were generated from a pattern would agree with a
    /// parser that generated the same pattern, and agree with it while it was wrong.
    /// </summary>
    [Theory]
    [InlineData(Ao3DownloadFormat.Epub,
        "https://archiveofourown.org/downloads/70441196/we_chose_to_wait.epub?updated_at=1767140797")]
    [InlineData(Ao3DownloadFormat.Mobi,
        "https://archiveofourown.org/downloads/70441196/we_chose_to_wait.mobi?updated_at=1767140797")]
    [InlineData(Ao3DownloadFormat.Pdf,
        "https://archiveofourown.org/downloads/70441196/we_chose_to_wait.pdf?updated_at=1767140797")]
    [InlineData(Ao3DownloadFormat.Html,
        "https://archiveofourown.org/downloads/70441196/we_chose_to_wait.html?updated_at=1767140797")]
    [InlineData(Ao3DownloadFormat.Azw3,
        "https://archiveofourown.org/downloads/70441196/we_chose_to_wait.azw3?updated_at=1767140797")]
    public void Reads_the_address_the_captured_page_offers_for(Ao3DownloadFormat format, string url)
    {
        var links = Ao3DownloadLinks.Parse(Fixtures.Load(Fixtures.WorkPage), WorkPageUrl, ArchiveUrl);

        Assert.Equal(url, links[format]);
    }

    [Fact]
    public void Reads_every_format_this_library_fetches_and_nothing_else()
    {
        // The capture offers all five, so "every format" is a claim about the archive and not only
        // about the parser. It is also the guard on the enum: a sixth member added without a
        // re-captured page fails here rather than silently becoming a format no work ever offers.
        var links = Ao3DownloadLinks.Parse(Fixtures.Load(Fixtures.WorkPage), WorkPageUrl, ArchiveUrl);

        Assert.Equal(Enum.GetValues<Ao3DownloadFormat>().Order(), links.Keys.Order());
    }

    [Fact]
    public void Reads_an_address_no_work_id_could_have_produced()
    {
        // The point of the seam, stated as an assertion: neither half of the path after the id is
        // derivable from anything the library stores. A parser that quietly fell back to building
        // "/downloads/70441196/70441196.epub" would pass the test above's shape and fail here.
        var links = Ao3DownloadLinks.Parse(Fixtures.Load(Fixtures.WorkPage), WorkPageUrl, ArchiveUrl);

        Assert.Contains("we_chose_to_wait", links[Ao3DownloadFormat.Epub]);
        Assert.Contains("updated_at=1767140797", links[Ao3DownloadFormat.Epub]);
    }

    [Fact]
    public void Reads_nothing_from_a_page_that_offers_no_downloads()
    {
        // A restricted work, an error page, or markup AO3 has moved on from. Empty rather than a
        // throw: the caller turns "no link" into one failed request with a message, and a throw
        // would turn it into an unhandled fetch instead.
        Assert.Empty(Ao3DownloadLinks.Parse(
            "<html><body><p>Nothing here.</p></body></html>", WorkPageUrl, ArchiveUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Reads_nothing_from_an_absent_page(string? html) =>
        Assert.Empty(Ao3DownloadLinks.Parse(html, WorkPageUrl, ArchiveUrl));

    [Fact]
    public void Resolves_a_relative_href_against_the_page_it_was_read_from()
    {
        // The capture holds absolute links, which AO3 does not owe us — it serves relative hrefs
        // elsewhere on the same page, and a parser that only understood absolute ones would start
        // reporting "this work offers no EPUB" the day that changed.
        var links = Ao3DownloadLinks.Parse(
            Menu("<li><a href=\"/downloads/70441196/we_chose_to_wait.epub?updated_at=1767140797\">EPUB</a></li>"),
            WorkPageUrl, ArchiveUrl);

        Assert.Equal(
            "https://archiveofourown.org/downloads/70441196/we_chose_to_wait.epub?updated_at=1767140797",
            links[Ao3DownloadFormat.Epub]);
    }

    [Fact]
    public void Ignores_a_link_in_a_format_this_library_does_not_fetch()
    {
        var links = Ao3DownloadLinks.Parse(
            Menu("<li><a href=\"https://archiveofourown.org/downloads/1/x.zip\">ZIP</a></li>"
                 + "<li><a href=\"https://archiveofourown.org/downloads/1/x.pdf\">PDF</a></li>"),
            WorkPageUrl, ArchiveUrl);

        Assert.Equal(Ao3DownloadFormat.Pdf, Assert.Single(links).Key);
    }

    [Fact]
    public void Ignores_a_link_that_does_not_address_the_web()
    {
        // Uri.TryCreate(..., UriKind.Absolute) succeeds on Linux for a bare path, yielding
        // file:///downloads/… — so "is this absolute" is not the question. The scheme is.
        var links = Ao3DownloadLinks.Parse(
            Menu("<li><a href=\"javascript:alert('epub')\">EPUB</a></li>"), WorkPageUrl, ArchiveUrl);

        Assert.Empty(links);
    }

    [Fact]
    public void Ignores_a_link_pointing_at_another_host()
    {
        // Whatever comes out of here is fetched with the instance's AO3 session cookie attached and
        // written to the instance's disk, and a work page renders author-supplied HTML. One href
        // that survived AO3's sanitiser inside the download menu would otherwise hand this
        // deployment's login to whoever wrote it.
        var links = Ao3DownloadLinks.Parse(
            Menu("<li><a href=\"https://elsewhere.example/downloads/1/x.epub\">EPUB</a></li>"),
            WorkPageUrl, ArchiveUrl);

        Assert.Empty(links);
    }

    [Fact]
    public void Ignores_a_link_on_the_origin_a_redirected_page_landed_on()
    {
        // The origin check's whole point, and the one measurement that cannot be the page's own.
        // The work page is fetched with redirects followed, so where it was finally served from is
        // decided by AO3's responses — and measured against *that*, a page redirected off-origin
        // makes every link on the substituted page same-origin. The link would then be fetched with
        // this deployment's AO3 session cookie attached. The configured archive is what decides it.
        var links = Ao3DownloadLinks.Parse(
            Menu("<li><a href=\"https://elsewhere.example/downloads/1/x.epub\">EPUB</a></li>"),
            pageUrl: "https://elsewhere.example/works/70441196",
            ArchiveUrl);

        Assert.Empty(links);
    }

    [Fact]
    public void Ignores_a_relative_link_on_a_page_that_landed_off_the_archive()
    {
        // The same substitution one step subtler: the href names no host at all, so it inherits the
        // origin the page landed on. Resolving it against the page is still right — that is what a
        // browser would do — but what comes out is off-archive, and it is refused for being so.
        var links = Ao3DownloadLinks.Parse(
            Menu("<li><a href=\"/downloads/1/x.epub\">EPUB</a></li>"),
            pageUrl: "https://elsewhere.example/works/70441196",
            ArchiveUrl);

        Assert.Empty(links);
    }

    [Fact]
    public void Reads_an_absolute_archive_link_off_a_page_that_landed_elsewhere()
    {
        // The other half of the rule, and the reason this seam is not the whole defence: the page's
        // address is not evidence for or against a link, so a link that does address the configured
        // archive reads no matter where the page carrying it was served from. What a substituted
        // page must not get to do is *choose* the file, and that is the caller's to refuse — see
        // DownloadWorkerTests.Fetches_nothing_off_a_redirected_page_even_where_its_links_address_the_archive.
        var links = Ao3DownloadLinks.Parse(
            Menu("<li><a href=\"https://archiveofourown.org/downloads/1/x.epub\">EPUB</a></li>"),
            pageUrl: "https://elsewhere.example/works/70441196",
            ArchiveUrl);

        Assert.Equal("https://archiveofourown.org/downloads/1/x.epub", links[Ao3DownloadFormat.Epub]);
    }

    [Fact]
    public void Ignores_a_link_that_keeps_the_host_and_drops_the_encryption()
    {
        // The same attack one scheme along: the session cookie this link is fetched with would go
        // over the wire in the clear, and a host comparison alone says yes to that. AO3 serves its
        // work pages over HTTPS and its downloads from the same place.
        var links = Ao3DownloadLinks.Parse(
            Menu("<li><a href=\"http://archiveofourown.org/downloads/1/x.epub\">EPUB</a></li>"),
            WorkPageUrl, ArchiveUrl);

        Assert.Empty(links);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("file:///archive")]
    public void Reads_nothing_when_there_is_no_archive_address_to_check_a_link_against(string? archiveUrl)
    {
        // A link with nothing to compare its origin to is not a link this app may fetch. Empty even
        // though the capture's own links are the real archive's: a misconfigured instance gets no
        // downloads rather than downloads nothing checked.
        Assert.Empty(Ao3DownloadLinks.Parse(Fixtures.Load(Fixtures.WorkPage), WorkPageUrl, archiveUrl!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a url")]
    public void Reads_an_absolute_link_even_with_no_page_address_to_resolve_against(string? pageUrl)
    {
        // The page address only resolves relative hrefs now, so losing it costs those and nothing
        // else: an href that already names the configured archive is still measurable against it.
        var links = Ao3DownloadLinks.Parse(Fixtures.Load(Fixtures.WorkPage), pageUrl, ArchiveUrl);

        Assert.Equal(Enum.GetValues<Ao3DownloadFormat>().Order(), links.Keys.Order());
    }

    [Fact]
    public void Reads_nothing_relative_when_there_is_no_page_address_to_resolve_against()
    {
        var links = Ao3DownloadLinks.Parse(
            Menu("<li><a href=\"/downloads/1/x.epub\">EPUB</a></li>"), pageUrl: null, ArchiveUrl);

        Assert.Empty(links);
    }

    [Fact]
    public void Reads_only_the_download_menu()
    {
        // A work's page links to plenty that is not a download of it — related works, the series,
        // other people's bookmarks. Anything matching on extension alone would collect those too.
        var html = "<html><body>"
            + "<a href=\"https://archiveofourown.org/downloads/999/someone_else.epub?updated_at=1\">EPUB</a>"
            + "</body></html>";

        Assert.Empty(Ao3DownloadLinks.Parse(html, WorkPageUrl, ArchiveUrl));
    }

    [Fact]
    public void Keeps_the_first_link_a_format_is_offered_under()
    {
        var links = Ao3DownloadLinks.Parse(
            Menu("<li><a href=\"https://archiveofourown.org/downloads/1/first.epub\">EPUB</a></li>"
                 + "<li><a href=\"https://archiveofourown.org/downloads/1/second.epub\">EPUB</a></li>"),
            WorkPageUrl, ArchiveUrl);

        Assert.Equal("https://archiveofourown.org/downloads/1/first.epub", links[Ao3DownloadFormat.Epub]);
    }

    /// <summary>The capture's download menu, with <paramref name="items"/> in place of its links.</summary>
    private static string Menu(string items) =>
        "<html><body><ul class=\"work navigation actions\"><li class=\"download\">"
        + "<button class=\"collapsed\">Download</button>"
        + $"<ul class=\"expandable secondary hidden\">{items}</ul>"
        + "</li></ul></body></html>";
}
