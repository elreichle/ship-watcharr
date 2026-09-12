using Ao3Tracker.Api.Models;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// One tag read off a blurb, before it has been resolved to a <see cref="Tag"/> row.
/// </summary>
public sealed record Ao3BlurbTag(Ao3TagType Type, string Name);

/// <param name="Username">The account, from <c>/users/{username}/pseuds/{pseud}</c>.</param>
/// <param name="PseudName">The pseud. Equal to <paramref name="Username"/> for the default pseud.</param>
/// <param name="DisplayName">The byline text AO3 actually rendered, e.g. "somepseud (someuser)".</param>
public sealed record Ao3BlurbAuthor(string Username, string PseudName, string DisplayName);

/// <param name="Part">Null when the markup did not state one — see <see cref="WorkSeries.Part"/>.</param>
public sealed record Ao3BlurbSeries(long Id, string Title, int? Part);

/// <summary>
/// A work as a listing page describes it. Deliberately a plain record with no entity references:
/// parsing is a pure function of the HTML, so it can be tested against captured markup without a
/// database, and a parse failure can never leave half-written rows behind.
///
/// The field set mirrors <see cref="Work"/> because AO3's blurbs really do carry the whole metadata
/// set — see the remarks there. <see cref="Work.PublishedAt"/> has no counterpart here precisely
/// because blurbs are the one place it is missing.
/// </summary>
/// <param name="IsAnonymous">
/// True for a work AO3 credits to nobody, false for one whose byline named someone, and null when
/// the byline could not be read — which is a statement about this parse, not about the work, and
/// the reason <see cref="Authors"/> being empty may not be taken for authorship having been
/// withdrawn. See <c>Ao3BlurbParser.ParseByline</c>.
/// </param>
/// <param name="Authors">
/// The creators this blurb named. Empty whenever <see cref="IsAnonymous"/> is not false, and only
/// authoritative when it is: an empty list beside a null flag means nothing was read.
/// </param>
/// <param name="RevisedOn">
/// The day the blurb's visible date shows, at UTC midnight, and null when it could not be read —
/// whether or not <see cref="UpdatedAt"/> was. A different clock from that one: this is the date AO3
/// orders a listing by revision and applies <c>date_from</c> to, and the <c>updated_at</c> comment
/// has been captured running days ahead of it. See <see cref="Work.RevisedOn"/>.
/// </param>
public sealed record Ao3WorkBlurb(
    long WorkId,
    string Title,
    string? SummaryHtml,
    Ao3Rating Rating,
    Ao3Category Categories,
    Ao3Warning Warnings,
    bool IsComplete,
    int WordCount,
    int ChapterCount,
    int? PlannedChapterCount,
    int Hits,
    int Kudos,
    int CommentCount,
    int Bookmarks,
    int CollectionCount,
    string? LanguageCode,
    string? LanguageName,
    DateTime UpdatedAt,
    bool UpdatedAtIsApproximate,
    DateTime? RevisedOn,
    bool? IsAnonymous,
    bool IsRestricted,
    IReadOnlyList<Ao3BlurbTag> Tags,
    IReadOnlyList<Ao3BlurbAuthor> Authors,
    IReadOnlyList<Ao3BlurbSeries> Series);

/// <summary>
/// One listing page, parsed.
/// </summary>
/// <param name="TotalWorks">
/// The "N Works in ..." count AO3 prints above the listing, when it prints one. Null rather than
/// zero when absent: an unknown total and a genuinely empty tag are different facts, and
/// <see cref="Ship.LastKnownTotalWorks"/> must not be overwritten by the former.
/// </param>
/// <param name="HasNextPage">Whether AO3 offered a "Next" link. The authority on where a walk ends.</param>
/// <param name="ParseWarnings">
/// Blurbs that could not be read, plus fields that were missing from ones that could. Surfaced on
/// the run rather than thrown: one malformed blurb must not discard the other nineteen on the page.
/// </param>
/// <param name="HasListing">
/// Whether the document contained AO3's listing container at all — the <c>ol.work.index.group</c>
/// the blurbs hang off. False means what was served is not a results page: an empty body, a static
/// maintenance page, anything a proxy substituted. That is different from an empty listing, which
/// has the container and nothing in it, and the difference is what lets a walk tell "this tag has
/// no works" apart from "this is not the tag". Without it the two are indistinguishable and the
/// stronger conclusion wins by default.
/// </param>
public sealed record Ao3ListingPage(
    IReadOnlyList<Ao3WorkBlurb> Works,
    int? TotalWorks,
    bool HasNextPage,
    int ParseWarnings,
    bool HasListing);
