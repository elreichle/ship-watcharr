using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ao3Tracker.Api.Models;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Reads AO3's works-listing HTML into <see cref="Ao3WorkBlurb"/> records.
///
/// A pure function of the markup: no HTTP, no database, no clock. That is what lets the whole
/// vocabulary be tested against captured pages, and it means a page AO3 has changed the shape of
/// fails in one place instead of somewhere inside a half-finished transaction.
///
/// The governing rule throughout is that a blurb degrades rather than throws. AO3 is volunteer-run
/// and its markup moves; a scraper that threw on the first unfamiliar element would turn a cosmetic
/// change into a dead multi-hour backfill. So every field is independently optional, a blurb missing
/// one still yields the rest, and the shortfall is counted into
/// <see cref="Ao3ListingPage.ParseWarnings"/> where a run record can show it.
/// </summary>
public static class Ao3BlurbParser
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// AO3's visible date, e.g. "25 Dec 2023". Day-granular, which is why it is only the fallback
    /// for <see cref="Work.UpdatedAt"/> — and the whole of <see cref="Work.RevisedOn"/>.
    /// </summary>
    private static readonly string[] VisibleDateFormats = ["d MMM yyyy", "dd MMM yyyy"];

    public static Ao3ListingPage ParseListing(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new Ao3ListingPage([], TotalWorks: null, HasNextPage: false, ParseWarnings: 0, HasListing: false);

        var document = Parser.ParseDocument(html);
        var warnings = 0;
        var works = new List<Ao3WorkBlurb>();

        foreach (var element in SelectBlurbs(document))
        {
            var blurb = TryParseBlurb(element, ref warnings);
            if (blurb is not null) works.Add(blurb);
        }

        return new Ao3ListingPage(
            works, ParseTotalWorks(document), HasNextPage(document), warnings, FindListing(document) is not null);
    }

    /// <summary>
    /// The work blurbs on the page, and only those. Scoped to the listing rather than taken from the
    /// whole document because AO3 renders the same <c>li.blurb</c> shape in the sidebar and in
    /// related-works modules; a document-wide selector would ingest those as if they were results.
    /// </summary>
    private static IElement? FindListing(IDocument document) =>
        document.QuerySelector("ol.work.index.group") ?? document.QuerySelector("ol.index.group");

    private static IEnumerable<IElement> SelectBlurbs(IDocument document)
    {
        var scope = (IParentNode?)FindListing(document) ?? document;
        return scope.QuerySelectorAll("li.blurb").Where(li => li.Id?.StartsWith("work_", StringComparison.Ordinal) == true);
    }

    private static Ao3WorkBlurb? TryParseBlurb(IElement blurb, ref int warnings)
    {
        // The id is the one field with no fallback: everything downstream is keyed on it, and a
        // blurb we cannot name is a row we could only ever insert as a duplicate.
        var workId = ParseWorkId(blurb);
        if (workId is null)
        {
            warnings++;
            return null;
        }

        var heading = blurb.QuerySelector("h4.heading");
        var title = heading?.QuerySelector("a[href*='/works/']")?.TextContent.Trim();

        if (string.IsNullOrEmpty(title))
        {
            // Titled rather than skipped. A work with unreadable markup is still real, still in this
            // ship's index, and still worth recording as having been seen.
            title = $"Unknown work {workId}";
            warnings++;
        }

        var (chapters, plannedChapters) = ParseChapters(blurb);
        var revisedOn = ParseVisibleDate(blurb);
        var (updatedAt, isApproximate) = ParseUpdatedAt(blurb, revisedOn, ref warnings);
        var (authors, isAnonymous) = ParseByline(heading, ref warnings);

        return new Ao3WorkBlurb(
            WorkId: workId.Value,
            Title: title,
            SummaryHtml: blurb.QuerySelector("blockquote.summary")?.InnerHtml.Trim(),
            Rating: Ao3Labels.ParseRating(RequiredTagTitle(blurb, "rating")),
            Categories: Ao3Labels.ParseCategories(RequiredTagTitle(blurb, "category")),
            Warnings: Ao3Labels.ParseWarnings(RequiredTagTitle(blurb, "warnings")),

            // "Complete Work" is AO3's wording; anything else (including a missing marker) is a WIP,
            // which is the safer default — claiming a running story is finished is the worse error.
            IsComplete: string.Equals(RequiredTagTitle(blurb, "iswip"), "Complete Work", StringComparison.OrdinalIgnoreCase),

            WordCount: StatInt(blurb, "words") ?? 0,
            ChapterCount: chapters,
            PlannedChapterCount: plannedChapters,
            Hits: StatInt(blurb, "hits") ?? 0,
            Kudos: StatInt(blurb, "kudos") ?? 0,
            CommentCount: StatInt(blurb, "comments") ?? 0,
            Bookmarks: StatInt(blurb, "bookmarks") ?? 0,
            CollectionCount: StatInt(blurb, "collections") ?? 0,
            LanguageCode: blurb.QuerySelector("dd.language")?.GetAttribute("lang")?.Trim().NullIfEmpty(),
            LanguageName: blurb.QuerySelector("dd.language")?.TextContent.Trim().NullIfEmpty(),
            UpdatedAt: updatedAt,
            UpdatedAtIsApproximate: isApproximate,
            RevisedOn: revisedOn,

            IsAnonymous: isAnonymous,

            IsRestricted: IsRestricted(blurb),
            Tags: ParseTags(blurb),
            Authors: authors,
            Series: ParseSeries(blurb));
    }

    /// <summary>
    /// From the blurb's own <c>id</c> (<c>work_12345678</c>), falling back to its title link. The id
    /// attribute is preferred because a heading can link elsewhere — a series part, a translation —
    /// while the element id names the work the blurb is actually about.
    /// </summary>
    private static long? ParseWorkId(IElement blurb)
    {
        var fromId = blurb.Id?["work_".Length..];
        if (long.TryParse(fromId, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return id;

        var href = blurb.QuerySelector("h4.heading a[href*='/works/']")?.GetAttribute("href");
        return TryIdFromPath(href, "works");
    }

    /// <summary>
    /// Pulls the numeric id out of a <c>/{collection}/{id}</c> path. Tolerates absolute and relative
    /// URLs alike, and trailing segments such as <c>/works/123/chapters/456</c>.
    /// </summary>
    private static long? TryIdFromPath(string? href, string collection)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;

        var path = Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : href;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!parts[i].Equals(collection, StringComparison.OrdinalIgnoreCase)) continue;
            if (long.TryParse(parts[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return id;
        }

        return null;
    }

    /// <summary>
    /// The <c>title</c> of one of the four symbols in <c>ul.required-tags</c>. AO3 puts the human
    /// wording there and the machine-readable value only in the class name, so the title is what
    /// both this and <see cref="Ao3Labels"/> agree to speak in.
    /// </summary>
    private static string? RequiredTagTitle(IElement blurb, string kind) =>
        blurb.QuerySelector($"ul.required-tags span.{kind}")?.GetAttribute("title")?.Trim().NullIfEmpty();

    /// <summary>
    /// Posted and planned chapter counts from <c>dd.chapters</c>, which reads "3/10" or "3/?".
    /// </summary>
    private static (int Posted, int? Planned) ParseChapters(IElement blurb)
    {
        var text = blurb.QuerySelector("dd.chapters")?.TextContent.Trim();
        if (string.IsNullOrEmpty(text)) return (0, null);

        var parts = text.Split('/', 2);
        var posted = ParseInt(parts.ElementAtOrDefault(0)) ?? 0;

        // "?" is AO3's open-ended WIP, and is why the column is nullable rather than defaulting to
        // the posted count — "3 of 3 so far" and "3 of 3, finished" are different claims.
        var planned = ParseInt(parts.ElementAtOrDefault(1));

        return (posted, planned);
    }

    /// <summary>
    /// The revision time, exact if AO3 emitted its machine-readable form and day-granular if not.
    ///
    /// Both are read because they answer different questions: the epoch comment is exact to the
    /// second and so can safely gate "has this work changed since we last looked", while the visible
    /// date can only say "some time that day". The flag records which one a row actually got, so a
    /// later comparison knows how much precision it is working with.
    /// </summary>
    private static (DateTime UpdatedAt, bool IsApproximate) ParseUpdatedAt(
        IElement blurb, DateTime? visibleDate, ref int warnings)
    {
        if (TryParseEpochComment(blurb) is { } exact) return (exact, false);
        if (visibleDate is { } day) return (day, true);

        // Neither form was readable. DateTime.MinValue rather than "now": the watermark and the
        // backfill's monotonicity check both compare against this, and a work stamped with the
        // current time would look like the newest thing in the tag and truncate the next
        // incremental pass at it.
        warnings++;
        return (DateTime.MinValue, true);
    }

    /// <summary>
    /// The day <c>p.datetime</c> shows, at UTC midnight, or null when it holds nothing readable.
    /// Read on its own rather than only when the <c>updated_at</c> comment is missing, because the two
    /// are different clocks and <see cref="Ao3WorkBlurb.RevisedOn"/> needs this one: it is the date
    /// AO3's listing sorts and filters by, and the comment has been captured days ahead of it.
    /// </summary>
    private static DateTime? ParseVisibleDate(IElement blurb)
    {
        var visible = blurb.QuerySelector("p.datetime")?.TextContent.Trim();

        return !string.IsNullOrEmpty(visible)
            && DateTime.TryParseExact(
                visible, VisibleDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// AO3's <c>&lt;!-- updated_at=EPOCH --&gt;</c> marker. Read from the comment nodes rather than
    /// by regex over the whole blurb so that an epoch appearing in a summary cannot be mistaken for
    /// one.
    /// </summary>
    private static DateTime? TryParseEpochComment(IElement blurb)
    {
        foreach (var comment in blurb.Descendants<IComment>())
        {
            var text = comment.Data.Trim();
            if (!text.StartsWith("updated_at=", StringComparison.OrdinalIgnoreCase)) continue;

            var value = text["updated_at=".Length..].Trim();
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
                return DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        }

        return null;
    }

    /// <summary>
    /// Registered-users-only works carry a lock in the heading. They are invisible to a logged-out
    /// scrape entirely, so seeing one at all means this run was authenticated — which is what
    /// <see cref="Ship.LastKnownTotalWasAuthenticated"/> exists to remember.
    /// </summary>
    /// <remarks>
    /// The lock is a bare image: AO3's blurb template (otwarchive
    /// <c>app/views/works/_work_module.html.erb</c>, read 2026-09-10) renders
    /// <c>image_tag("lockblue.png", size: "15x15", alt: "(Restricted)", title: "Restricted")</c> with
    /// no class at all. The selector this replaced also required <c>class="symbol"</c>, which only the
    /// hand-written test markup ever carried, so not one of production's 228,073 works was ever
    /// flagged. Matched on the title, which is what tells it apart from the red lock beside it on a
    /// work an admin has hidden (<c>lockred.png</c>, titled "Hidden by Administrator").
    /// </remarks>
    private static bool IsRestricted(IElement blurb) =>
        blurb.QuerySelector("h4.heading img[title='Restricted']") is not null;

    /// <summary>
    /// Creators in byline order, and whether the work is anonymous — null when the byline could not
    /// be read at all. Only <c>rel="author"</c> anchors count as creators: AO3 renders gift
    /// recipients as links inside the very same heading, so a looser selector records them as
    /// co-authors. See <see cref="WorkAuthor"/>.
    ///
    /// The third state is the rule this method exists to state. "Nobody is credited" and "we could
    /// not read who is credited" reach the parser as the same markup — a heading with no author
    /// anchors — but they must not have the same effect: <c>WorkIngestor</c> reconciles authorship
    /// against what the blurb says, so an empty set deletes every creator the work already had, and
    /// a change to the heading's shape would rewrite a whole library as anonymous.
    ///
    /// AO3 does write the difference down: an anonymous work says "Anonymous" where the byline
    /// goes. So that word, and nothing else, is what may conclude a work has no creators. A byline
    /// crediting nobody and not saying it is unread — counted as a parse warning, and left for the
    /// ingestor to decline to act on.
    /// </summary>
    private static (IReadOnlyList<Ao3BlurbAuthor> Authors, bool? IsAnonymous) ParseByline(
        IElement? heading, ref int warnings)
    {
        var authors = new List<Ao3BlurbAuthor>();

        if (heading is not null)
        {
            var seen = new HashSet<(string, string)>();
            var unreadableAnchors = 0;

            foreach (var anchor in heading.QuerySelectorAll("a[rel~='author']"))
            {
                var display = anchor.TextContent.Trim();
                var (username, pseud) = ParsePseudPath(anchor.GetAttribute("href"), display);

                if (username is null || pseud is null)
                {
                    unreadableAnchors++;
                    continue;
                }

                // A creator listed twice in one byline would otherwise violate the (WorkId, PseudId)
                // primary key on WorkAuthor and fail the whole page's save.
                if (seen.Add((username, pseud)))
                    authors.Add(new Ao3BlurbAuthor(username, pseud, display.NullIfEmpty() ?? pseud));
            }

            // A byline that named somebody was read, even if one of its anchors was shaped in a way
            // this does not understand. Dropping that one creator is the smaller error; discarding
            // the ones that were read to protect it is the larger.
            if (authors.Count > 0)
            {
                if (unreadableAnchors > 0) warnings++;
                return (authors, false);
            }

            // Consulted only once nothing has been credited, which is why an unreadable anchor
            // reading "Anonymous" still counts: whether AO3 renders that word as plain text or as a
            // link, a heading crediting nobody and saying it is an anonymous work either way.
            if (SaysAnonymous(heading)) return (authors, true);
        }

        warnings++;
        return (authors, null);
    }

    /// <summary>
    /// Whether the heading says "Anonymous" where the creators would go — and only there. Everything
    /// this reads has to be the byline itself, because the word carries the authority to conclude a
    /// work has no creators, and the heading holds other people's names too — four of them, and a
    /// title that is nobody's name at all.
    /// </summary>
    private static bool SaysAnonymous(IElement heading)
    {
        foreach (var word in BylineWords(heading))
        {
            // AO3's gift clause — "… for someuser" — is not part of the byline, and a gift to
            // someone who asked not to be named renders exactly like an anonymous creator. So the
            // byline ends here, and nothing past it may speak for the work's authorship.
            if (word.Equals("for", StringComparison.OrdinalIgnoreCase)) return false;
            if (word.Equals("Anonymous", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// The byline's words in order: everything after the "by" that separates a heading's title from
    /// its creators, minus the text of any link that is not a creator — recipients, series,
    /// collections. Anchors carrying <c>rel="author"</c> are kept, so the word counts whether AO3
    /// renders it as plain text or as a link. Punctuation is trimmed so that "Anonymous," reads as
    /// the word it is.
    ///
    /// The separator is what makes the title somebody else's words (a work called "Anonymous" must
    /// not erase its own author), and it has to be the separator rather than the title's link:
    /// under exactly the reshaping this guards against, no anchor in the heading survives, and a
    /// title would then be read as the byline it sits next to. The <em>last</em> "by" is the
    /// separator, because the one word that can appear on both sides of it is "by" itself, and only
    /// a title can hold the spare one. A heading with no separator yields no byline words at all:
    /// nothing in it carries the authority to conclude a work has no creators.
    /// </summary>
    private static IEnumerable<string> BylineWords(IElement heading)
    {
        var words = heading.Descendants<IText>()
            .Where(IsBylineText)
            .SelectMany(text => text.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Select(word => word.Trim(BylinePunctuation))
            .ToList();

        var separator = words.FindLastIndex(word => word.Equals("by", StringComparison.OrdinalIgnoreCase));
        return separator < 0 ? [] : words.Skip(separator + 1);
    }

    private static bool IsBylineText(IText text)
    {
        var anchor = text.ParentElement?.Closest("a");
        return anchor is null || anchor.Matches("a[rel~='author']");
    }

    /// <summary>What separates the words of a byline from the words themselves.</summary>
    private static readonly char[] BylinePunctuation = [',', '.', ':', ';', '(', ')', '[', ']', '"', '\''];

    /// <summary>
    /// Splits <c>/users/{username}/pseuds/{pseud}</c>. A bare <c>/users/{username}</c> is the
    /// default pseud, where AO3 omits the redundant second segment.
    /// </summary>
    private static (string? Username, string? Pseud) ParsePseudPath(string? href, string display)
    {
        if (string.IsNullOrWhiteSpace(href)) return (null, null);

        var path = Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : href;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var usersAt = Array.FindIndex(parts, p => p.Equals("users", StringComparison.OrdinalIgnoreCase));
        if (usersAt < 0 || usersAt + 1 >= parts.Length) return (null, null);

        var username = Uri.UnescapeDataString(parts[usersAt + 1]);

        var pseudsAt = Array.FindIndex(parts, p => p.Equals("pseuds", StringComparison.OrdinalIgnoreCase));
        var pseud = pseudsAt >= 0 && pseudsAt + 1 < parts.Length
            ? Uri.UnescapeDataString(parts[pseudsAt + 1])
            : username;

        return (username.NullIfEmpty(), pseud.NullIfEmpty() ?? display.NullIfEmpty());
    }

    /// <summary>
    /// Fandoms from <c>h5.fandoms</c> and everything else from <c>ul.tags.commas</c>, per
    /// <see cref="Ao3TagType"/>. Categories are deliberately absent: AO3 does not list them as tags,
    /// and they live only as <see cref="Ao3Category"/> flags.
    /// </summary>
    private static IReadOnlyList<Ao3BlurbTag> ParseTags(IElement blurb)
    {
        var tags = new List<Ao3BlurbTag>();
        var seen = new HashSet<(Ao3TagType, string)>();

        void Add(Ao3TagType type, string? name)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return;

            // Same reason as the byline de-duplication: (WorkId, TagId) is a primary key, and AO3
            // does render the same tag twice — a warning appears in both the required-tags symbols
            // and the tag list.
            if (seen.Add((type, trimmed.ToUpperInvariant()))) tags.Add(new Ao3BlurbTag(type, trimmed));
        }

        foreach (var anchor in blurb.QuerySelectorAll("h5.fandoms a.tag"))
            Add(Ao3TagType.Fandom, anchor.TextContent);

        foreach (var item in blurb.QuerySelectorAll("ul.tags li"))
        {
            var type = TagTypeFromListItem(item);
            if (type is null) continue;

            foreach (var anchor in item.QuerySelectorAll("a.tag"))
                Add(type.Value, anchor.TextContent);
        }

        return tags;
    }

    /// <summary>
    /// AO3 marks each tag-list item with the plural of its kind. Anything else is left alone rather
    /// than guessed at — an unrecognised list is likelier to be a new AO3 concept than a new tag
    /// type, and inventing a type for it would put nonsense in the shared tag table.
    /// </summary>
    private static Ao3TagType? TagTypeFromListItem(IElement item)
    {
        foreach (var className in item.ClassList)
        {
            switch (className.ToLowerInvariant())
            {
                case "warnings": return Ao3TagType.Warning;
                case "relationships": return Ao3TagType.Relationship;
                case "characters": return Ao3TagType.Character;
                case "freeforms": return Ao3TagType.Freeform;
                case "fandoms": return Ao3TagType.Fandom;
            }
        }

        return null;
    }

    /// <summary>
    /// Series membership from <c>ul.series</c>, whose items read "Part <c>2</c> of <c>Series Name</c>".
    /// </summary>
    private static IReadOnlyList<Ao3BlurbSeries> ParseSeries(IElement blurb)
    {
        var series = new List<Ao3BlurbSeries>();
        var seen = new HashSet<long>();

        foreach (var item in blurb.QuerySelectorAll("ul.series li"))
        {
            var anchor = item.QuerySelector("a[href*='/series/']");
            if (anchor is null) continue;

            var id = TryIdFromPath(anchor.GetAttribute("href"), "series");
            if (id is null) continue;

            var title = anchor.TextContent.Trim();
            if (title.Length == 0) continue;

            if (seen.Add(id.Value))
                series.Add(new Ao3BlurbSeries(id.Value, title, ParseInt(item.QuerySelector("strong")?.TextContent)));
        }

        return series;
    }

    /// <summary>
    /// A number that is counting works, and nothing else: digits with AO3's thousands separators,
    /// immediately followed by the word it counts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Singular as well as plural. A tag with exactly one work is headed "1 Work in ...", and a
    /// pattern that only knew "Works" would fall through to whatever else the heading contained —
    /// which for a tag name carrying its own digits ("Star Wars: CT-7567", a disambiguating year)
    /// is the tag's *name* being read as the size of the ship.
    /// </para>
    /// <para>
    /// The word is what makes the number a total, so it is required rather than preferred. AO3
    /// serves soft errors as 200 with the status in the heading, and a page headed "Error 404"
    /// otherwise offers 404 as the tag's size — a number that then decides whether a full sweep
    /// believes works have left the tag.
    /// </para>
    /// </remarks>
    private static readonly Regex WorkCountInHeading =
        new(@"(?<count>\d[\d,.]*)\s+Works?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The "N Works in ..." total AO3 prints above a listing. Null when it is absent or unreadable;
    /// see <see cref="Ao3ListingPage.TotalWorks"/> for why that is not zero.
    /// </summary>
    private static int? ParseTotalWorks(IDocument document)
    {
        var heading = document.QuerySelector("#main h2.heading")?.TextContent
            ?? document.QuerySelector("h2.heading")?.TextContent;

        if (string.IsNullOrWhiteSpace(heading)) return null;

        // The first match, because the count comes before the tag: the heading is
        // "1 - 20 of 4,317 Works in Clarke Griffin/Lexa", a range whose total is the number beside
        // the word, and everything after "in" is a name someone else chose.
        var match = WorkCountInHeading.Match(heading);

        return match.Success ? ParseInt(match.Groups["count"].Value) : null;
    }

    /// <summary>
    /// Whether AO3 offered a next page. Taken from the pagination control rather than inferred from
    /// a full page of results: the last page of a tag whose count is an exact multiple of the page
    /// size is full and still last, and walking past it costs a pointless request every run.
    /// </summary>
    private static bool HasNextPage(IDocument document)
    {
        if (document.QuerySelector("ol.pagination a[rel~='next']") is not null) return true;

        // StartsWith, not equality: AO3 renders the label as "Next →", and the arrow is markup it
        // has changed before. Matching the whole string would silently turn every backfill into a
        // one-page run the next time it changes again.
        return document.QuerySelectorAll("ol.pagination li a")
            .Any(a => a.TextContent.Trim().StartsWith("Next", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An integer from a <c>dl.stats</c> entry, e.g. <c>dd.kudos</c>.</summary>
    private static int? StatInt(IElement blurb, string kind) =>
        ParseInt(blurb.QuerySelector($"dl.stats dd.{kind}")?.TextContent);

    /// <summary>
    /// A count as AO3 writes it, with thousands separators. Returns null rather than zero for
    /// anything unreadable, so callers can tell "AO3 said nothing" from "AO3 said none" — a missing
    /// <c>dd.kudos</c> means the work has no kudos yet, while a missing <c>dd.chapters</c> means the
    /// markup changed.
    /// </summary>
    private static int? ParseInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var cleaned = new string([.. text.Where(char.IsDigit)]);
        if (cleaned.Length == 0) return null;

        return int.TryParse(cleaned, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static string? NullIfEmpty(this string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
