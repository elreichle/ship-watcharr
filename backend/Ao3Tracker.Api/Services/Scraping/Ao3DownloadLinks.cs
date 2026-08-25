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
    /// Where the page was fetched from, used to resolve a relative href. Null means only absolute
    /// links are readable — which is what the capture holds, but not something AO3 owes us.
    /// </param>
    public static IReadOnlyDictionary<Ao3DownloadFormat, string> Parse(string? html, string? pageUrl = null)
    {
        var links = new Dictionary<Ao3DownloadFormat, string>();
        if (string.IsNullOrWhiteSpace(html)) return links;

        var document = Parser.ParseDocument(html);
        Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri);

        // Scoped to the menu rather than matched on extension across the page. A work's page links
        // to plenty this app must never fetch as "the download" — the series, related works, other
        // people's bookmarks — and one of those ending in .epub would otherwise be collected as if
        // it were this work.
        foreach (var anchor in document.QuerySelectorAll("li.download a[href]"))
        {
            var url = Resolve(anchor.GetAttribute("href"), pageUri);
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
    /// An href as an absolute http(s) URL, or null where it is neither.
    /// </summary>
    /// <remarks>
    /// The scheme check is not ceremony. <c>Uri.TryCreate("/downloads/1/x.epub", UriKind.Absolute, …)</c>
    /// <em>succeeds</em> on Linux, yielding <c>file:///downloads/1/x.epub</c> — so a bare path read
    /// off a page would be treated as absolute and then resolved against local disk. The login
    /// establisher was found doing exactly that; this is the same trap on the same kind of input.
    /// </remarks>
    private static string? Resolve(string? href, Uri? pageUri)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;

        var resolved = Uri.TryCreate(href, UriKind.Absolute, out var absolute) && IsWeb(absolute)
            ? absolute
            : pageUri is not null && Uri.TryCreate(pageUri, href, out var relative) && IsWeb(relative)
                ? relative
                : null;

        return resolved?.ToString();
    }

    private static bool IsWeb(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;

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
