using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ao3Tracker.Api.Models;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// A work's own page, parsed — the two things a listing blurb cannot carry.
/// </summary>
/// <param name="PublishedAt">
/// AO3's "Published:" date, day-granular because that is all the page prints, and UTC like every
/// other timestamp here. Null when it was absent or unreadable, which is a statement about this
/// parse rather than about the work: <see cref="Work.PublishedAt"/> must not be overwritten with
/// nothing on a page whose shape changed.
/// </param>
/// <param name="Tags">
/// Every tag the page carries, of the types <see cref="Ao3TagType"/> has a member for. This is the
/// complete list — the whole reason a work's page is worth a request, since a blurb prints an
/// abbreviated one — and <c>WorkIngestor.IngestDetailAsync</c> reconciles against it on that basis.
/// </param>
/// <param name="IsWorkPage">
/// Whether the document was a work's page at all: AO3's <c>dl.work.meta.group</c> was found. False
/// for anything else that can arrive at a work's address with a 200 — the adult-content
/// interstitial, a login page for a restricted work, a maintenance page, an empty body.
///
/// It is the guard the whole write hangs off, and the reason it is a field rather than an inference
/// from an empty tag list: the page is the authority that may delete a work's tags, and a document
/// that is not the page must never be read as one saying the work has none.
/// </param>
/// <param name="ParseWarnings">
/// Fields that were missing from a page that was otherwise read. Counted rather than thrown, the
/// same rule <see cref="Ao3BlurbParser"/> is written to: AO3's markup moves, and a parser that threw
/// on the first unfamiliar element would turn a cosmetic change into no detail fetches at all.
/// </param>
public sealed record Ao3WorkPage(
    DateTime? PublishedAt,
    IReadOnlyList<Ao3BlurbTag> Tags,
    bool IsWorkPage,
    int ParseWarnings)
{
    public static Ao3WorkPage NotAWorkPage { get; } = new(null, [], IsWorkPage: false, ParseWarnings: 0);
}

/// <summary>
/// Reads a work's own page into an <see cref="Ao3WorkPage"/>.
///
/// A pure function of the markup, like <see cref="Ao3BlurbParser"/> and
/// <see cref="Ao3DownloadLinks"/>, and for the same reason: what AO3 says is decided by captured
/// HTML — <c>ao3-work-page.html</c> — rather than by anything this codebase remembers. No HTTP, no
/// database, no clock.
///
/// It exists because a listing blurb is not a complete description of a work. AO3 prints no
/// publication date in a blurb at all, and prints a truncated tag list; both are on the work's own
/// page, which costs one request per work. See <see cref="Ao3WorkDetailScraper"/> for what spends
/// those requests and <c>WorkIngestor.IngestDetailAsync</c> for what is entitled to be written from
/// them.
/// </summary>
public static class Ao3WorkPageParser
{
    private static readonly HtmlParser Parser = new();

    /// <summary>AO3's machine-ish date on a work page, e.g. "2025-09-06".</summary>
    private const string PublishedDateFormat = "yyyy-MM-dd";

    /// <summary>
    /// The tag types AO3 prints on a work page, by the class it marks each <c>dd</c> with.
    ///
    /// Rating and category are deliberately absent. Both are printed in the identical
    /// <c>dd.{kind}.tags</c> shape, and neither is a tag this application stores:
    /// <see cref="Ao3TagType"/> has no member for either, and both are already columns on
    /// <see cref="Work"/> written from the blurb's required-tags symbols. Reading them here would
    /// mean inventing a type for rows no filter and no page could then use.
    /// </summary>
    private static readonly (string Class, Ao3TagType Type)[] TagSections =
    [
        ("warning", Ao3TagType.Warning),
        ("fandom", Ao3TagType.Fandom),
        ("relationship", Ao3TagType.Relationship),
        ("character", Ao3TagType.Character),
        ("freeform", Ao3TagType.Freeform),
    ];

    public static Ao3WorkPage Parse(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return Ao3WorkPage.NotAWorkPage;

        var document = Parser.ParseDocument(html);

        // Everything below is scoped to this element rather than taken from the document, because a
        // work's page links to plenty of other people's tags — the series it is part of, related
        // works, the sidebar — and a document-wide selector would record those as this work's.
        var meta = document.QuerySelector("dl.work.meta.group");
        if (meta is null) return Ao3WorkPage.NotAWorkPage;

        var warnings = 0;

        var published = ParsePublishedAt(meta);
        if (published is null) warnings++;

        var tags = ParseTags(meta);

        // A work with no tags is not a thing AO3 serves: a fandom is required of every work, and the
        // capture carries thirty-one. So an empty list here is a markup change, and the count is
        // what lets the ingestor decline to reconcile a work's tags against nothing.
        if (tags.Count == 0) warnings++;

        return new Ao3WorkPage(published, tags, IsWorkPage: true, warnings);
    }

    private static DateTime? ParsePublishedAt(IElement meta)
    {
        var text = meta.QuerySelector("dd.published")?.TextContent.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        return DateTime.TryParseExact(
            text, PublishedDateFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<Ao3BlurbTag> ParseTags(IElement meta)
    {
        var tags = new List<Ao3BlurbTag>();
        var seen = new HashSet<(Ao3TagType, string)>();

        foreach (var (className, type) in TagSections)
        {
            foreach (var anchor in meta.QuerySelectorAll($"dd.{className}.tags a.tag"))
            {
                var name = anchor.TextContent.Trim();
                if (name.Length == 0) continue;

                // (WorkId, TagId) is a primary key, so a tag printed twice would not be a cosmetic
                // problem but a save the database refuses for the whole work.
                if (seen.Add((type, name.ToUpperInvariant()))) tags.Add(new Ao3BlurbTag(type, name));
            }
        }

        return tags;
    }
}

/// <summary>
/// The address of a work's own page on the configured archive.
/// </summary>
/// <remarks>
/// Shared by the two things that fetch one — the detail pass and the download fetcher, which reads
/// the same page for its download links — so that the <c>view_adult</c> parameter is decided once.
/// Without it AO3 answers an explicitly rated work with its content interstitial: a 200 carrying no
/// work meta at all, which is a page neither caller can read and which no status code reports.
/// </remarks>
public static class Ao3WorkPageUrl
{
    public static string For(string baseUrl, long workId) =>
        $"{baseUrl.TrimEnd('/')}/works/{workId.ToString(CultureInfo.InvariantCulture)}?view_adult=true";
}
