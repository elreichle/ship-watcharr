using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Ao3Tracker.Api.Services.Html;

/// <summary>
/// Turns one chapter of a downloaded EPUB into markup the in-app reader may render.
///
/// A chapter is the whole of an author's text as AO3 published it, and it arrives inside a file
/// this app fetched and stored rather than a column it scraped — but it is the same kind of thing
/// as a summary, author-supplied HTML, and it reaches a reader's browser the same way, inside a
/// logged-in session on this app's own origin. So it goes through the same walk, under a policy
/// that keeps what a chapter is made of and nothing a chapter can do without.
/// </summary>
/// <remarks>
/// Three attributes survive, each checked rather than trusted. A link keeps its <c>href</c> when it
/// is an absolute <c>http(s)</c> address — a relative one would resolve against this app, not the
/// archive — or a fragment pointing within the chapter, which is how footnotes are written; an
/// external link is also told to open elsewhere without handing the reader's page to it. An image
/// keeps an absolute <c>http(s)</c> <c>src</c> and its <c>alt</c>, and an image with no such source
/// is not an image and is unwrapped to nothing. An <c>id</c> is kept where it is a plain token, so
/// that a fragment link has something to point at. No <c>style</c>, no <c>class</c>, no handler:
/// the reader's own typography replaces the author's, which is what a reader chose an e-reader for.
/// </remarks>
public static partial class WorkChapterHtml
{
    /// <summary>
    /// The summary's formatting plus what a chapter is made of. This tracks AO3's own allowlist for
    /// work text, which is the markup a chapter can actually contain.
    /// </summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(
        WorkSummaryHtml.Allowed.Concat(
        [
            "h1", "h2", "h3", "h4", "h5", "h6",
            "div", "span", "a", "img",
            "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption",
            "figure", "figcaption",
            "center", "address",
            "abbr", "kbd", "samp", "var", "dfn", "big", "tt", "strike",
        ]),
        StringComparer.Ordinal);

    /// <summary>
    /// How long an id or an address may be. A fragment is a word, and an address longer than this
    /// is either a data URL in disguise or nothing a reader will ever follow.
    /// </summary>
    private const int MaxIdLength = 64;
    private const int MaxUrlLength = 2048;

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex IdToken();

    private sealed class Policy : HtmlAllowlistPolicy
    {
        public static readonly Policy Instance = new();

        public override IReadOnlySet<string> Allowed => WorkChapterHtml.Allowed;

        public override bool Keep(IElement element) =>
            element.LocalName != "img" || SafeAbsoluteUrl(element.GetAttribute("src")) is not null;

        public override bool IsContent(IElement element) => element.LocalName == "img";

        public override void AppendAttributes(IElement element, StringBuilder output)
        {
            var id = element.GetAttribute("id");
            if (id is not null && id.Length <= MaxIdLength && IdToken().IsMatch(id))
            {
                HtmlAllowlist.AppendAttribute(output, "id", id);
            }

            switch (element.LocalName)
            {
                case "a":
                    AppendLink(element.GetAttribute("href"), output);
                    break;

                case "img":
                    // Keep decided this one is present and safe; re-derived rather than remembered
                    // so the two cannot disagree about which value they looked at.
                    HtmlAllowlist.AppendAttribute(output, "src", SafeAbsoluteUrl(element.GetAttribute("src"))!);
                    HtmlAllowlist.AppendAttribute(output, "alt", element.GetAttribute("alt") ?? "");
                    break;
            }
        }

        private static void AppendLink(string? href, StringBuilder output)
        {
            if (href is null) return;

            var trimmed = href.Trim();

            // A fragment is a footnote: it stays within the chapter and needs no origin at all.
            if (trimmed.Length > 1 && trimmed[0] == '#')
            {
                var target = trimmed[1..];
                if (target.Length <= MaxIdLength && IdToken().IsMatch(target))
                {
                    HtmlAllowlist.AppendAttribute(output, "href", trimmed);
                }
                return;
            }

            var url = SafeAbsoluteUrl(trimmed);
            if (url is null) return;

            HtmlAllowlist.AppendAttribute(output, "href", url);
            // A new tab, and one that cannot reach back: the page it leaves is a logged-in session.
            HtmlAllowlist.AppendAttribute(output, "target", "_blank");
            HtmlAllowlist.AppendAttribute(output, "rel", "noopener noreferrer");
        }
    }

    /// <summary>
    /// The address as an absolute <c>http</c> or <c>https</c> URL, or null for anything else —
    /// a relative path, another scheme however it is spelled, or nothing at all. Judged by
    /// <see cref="Uri"/> rather than by prefix, so that a scheme written with stray whitespace or
    /// mixed case is judged by what it parses as.
    /// </summary>
    private static string? SafeAbsoluteUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        if (trimmed.Length > MaxUrlLength) return null;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>
    /// The chapter as markup the reader may render, or null where it holds nothing — no words and
    /// no image — which a chapter of a real work never is.
    /// </summary>
    public static string? Sanitize(string? html) => HtmlAllowlist.Sanitize(html, Policy.Instance);
}
