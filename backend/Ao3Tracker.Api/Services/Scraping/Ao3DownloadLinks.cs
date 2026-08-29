using AngleSharp.Html.Parser;
using Ao3Tracker.Api.Models;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Reads the download links off a work's own page.
///
/// A pure function of the markup, like <see cref="Ao3BlurbParser"/>, and for the same reason: what
/// AO3 offers is decided by captured HTML rather than by anything this codebase remembers.
///
/// It exists because the URLs cannot be constructed. AO3 addresses a download as
/// <c>/downloads/{workId}/{slug}.{ext}?updated_at={unix}</c>, and two of those parts are not
/// derivable from anything the library stores: the slug is an unstated truncation of the title
/// ("we chose to wait! marriage only after sex" becomes <c>we_chose_to_wait</c>), and
/// <c>updated_at</c> is a timestamp AO3 stamps rather than <see cref="Work.UpdatedAt"/>. So a
/// download costs two rate-gated requests — the page, then the file — and this is the seam between
/// them. Reconstructing the URL from a work id instead would be a guess dressed as an address.
/// </summary>
public static class Ao3DownloadLinks
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Every format the page offers, by the extension AO3 addresses it with.
    /// </summary>
    /// <param name="html">The work page's markup.</param>
    /// <param name="pageUrl">
    /// Where the page was fetched from — the base a relative href resolves against, and nothing
    /// else. It is not the authority on which origin a link may name: see <paramref name="archiveUrl"/>.
    /// </param>
    /// <param name="archiveUrl">
    /// The archive this deployment is configured for (<see cref="Ao3HttpClientOptions.BaseUrl"/>).
    /// It is the authority on which origin a link may name — see <see cref="Resolve"/>. Required,
    /// not optional: a link with nothing to check it against is not a link this app may fetch.
    /// </param>
    public static IReadOnlyDictionary<Ao3DownloadFormat, string> Parse(
        string? html, string? pageUrl, string archiveUrl)
    {
        var links = new Dictionary<Ao3DownloadFormat, string>();
        if (string.IsNullOrWhiteSpace(html)) return links;

        var document = Parser.ParseDocument(html);

        // No usable archive address means nothing below can be checked against anything, so nothing
        // is readable. Empty rather than "trust the markup".
        if (!Uri.TryCreate(archiveUrl, UriKind.Absolute, out var archiveUri)
            || !Ao3Origin.IsWeb(archiveUri))
        {
            return links;
        }

        // Best-effort, and deliberately only a base: a page fetched through a transport that follows
        // redirects was last served from wherever those redirects ended up, so a page that landed
        // off-origin resolves its own relative hrefs off-origin too — and Resolve then refuses them,
        // which is the point. Without one, only absolute hrefs are readable.
        var pageUri = Uri.TryCreate(pageUrl, UriKind.Absolute, out var parsed) && Ao3Origin.IsWeb(parsed)
            ? parsed
            : null;

        // Scoped to the menu rather than matched on extension across the page. A work's page links
        // to plenty this app must never fetch as "the download" — the series, related works, other
        // people's bookmarks — and one of those ending in .epub would otherwise be collected as if
        // it were this work.
        foreach (var anchor in document.QuerySelectorAll("li.download a[href]"))
        {
            var url = Resolve(anchor.GetAttribute("href"), pageUri, archiveUri);
            if (url is null) continue;

            // The extension, not the link's text. The text is a label AO3 is free to translate or
            // reword; the extension is part of the address it serves the file from.
            if (FormatOf(url) is not { } format) continue;

            // First wins. A second link for a format is not a second file — it is the same one
            // offered twice, and choosing between them by position at least chooses consistently.
            links.TryAdd(format, url);
        }

        return links;
    }

    /// <summary>
    /// An href as an absolute http(s) URL on the configured archive's own origin, or null where it
    /// is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The origin check is the load-bearing one. Whatever comes back from here is fetched with the
    /// instance's AO3 session cookie attached and written to the instance's disk, and a work page
    /// renders author-supplied HTML. One <c>&lt;a href="https://elsewhere.example/x.epub"&gt;</c>
    /// that survived AO3's sanitiser inside the download menu would otherwise hand this
    /// deployment's login to whoever wrote it.
    /// </para>
    /// <para>
    /// Measured against <paramref name="archiveUri"/> rather than against the page the link was read
    /// from, which is the same rule <see cref="Ao3SessionEstablisher"/> applies to the address it
    /// posts the login to. The work page is fetched with redirects followed, so where it was
    /// finally served from is something AO3's own responses decide: measured against *that*, a work
    /// page redirected off-origin would make every link on the substituted page same-origin, and
    /// the check would agree with whoever moved the page. The configured archive is the one address
    /// in this flow no page can influence.
    /// </para>
    /// <para>
    /// <see cref="Ao3Origin"/> holds the comparison itself, and why it is the whole origin rather
    /// than the host — a link that kept the name and dropped to <c>http</c> would send that same
    /// cookie unencrypted.
    /// </para>
    /// </remarks>
    private static string? Resolve(string? href, Uri? pageUri, Uri archiveUri)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;

        var resolved = Uri.TryCreate(href, UriKind.Absolute, out var absolute) && Ao3Origin.IsWeb(absolute)
            ? absolute
            : pageUri is not null && Uri.TryCreate(pageUri, href, out var relative) ? relative : null;

        if (resolved is null) return null;

        return Ao3Origin.IsTheConfiguredArchive(resolved, archiveUri) ? resolved.ToString() : null;
    }

    /// <summary>
    /// The format a download URL's path names, or null for one this library does not fetch.
    /// </summary>
    private static Ao3DownloadFormat? FormatOf(string url) =>
        Path.GetExtension(new Uri(url).AbsolutePath).TrimStart('.').ToLowerInvariant() switch
        {
            "epub" => Ao3DownloadFormat.Epub,
            "mobi" => Ao3DownloadFormat.Mobi,
            "pdf" => Ao3DownloadFormat.Pdf,
            "html" => Ao3DownloadFormat.Html,
            "azw3" => Ao3DownloadFormat.Azw3,
            _ => null,
        };
}
