namespace Ao3Tracker.Api.Models;

/// <summary>
/// A fanwork on AO3. GLOBAL, shared data: one row per AO3 work id no matter how many users
/// watch a ship containing it. Per-user opinions live in <see cref="UserWorkState"/>.
///
/// Every field here is populated from a search-result blurb — AO3's listing pages carry the
/// full metadata set, so the normal scrape path never fetches a work's own page.
/// <see cref="PublishedAt"/> is the sole exception; see its remarks.
/// </summary>
public class Work
{
    /// <summary>
    /// The AO3 work id, used directly as the primary key. Configured ValueGeneratedNever():
    /// left to convention SQLite would make this AUTOINCREMENT and PostgreSQL an IDENTITY
    /// column, and inserting explicit ids would leave the Postgres sequence permanently behind
    /// real data — every later generated id would collide.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The title as AO3 rendered it. Setting it also sets <see cref="TitleNormalized"/>, so the
    /// two cannot drift apart — see the remarks there.
    /// </summary>
    public string Title
    {
        get => _title;
        set
        {
            _title = value;
            TitleNormalized = value.ToUpperInvariant();
        }
    }

    private string _title = null!;

    /// <summary>
    /// <see cref="Title"/> uppercased with the invariant culture, and the column a title search goes
    /// through. Same provider-portability requirement as <see cref="Tag.NameNormalized"/>: SQLite's
    /// LIKE folds case and PostgreSQL's does not, so a search through <see cref="Title"/> itself
    /// would answer differently on the two providers.
    ///
    /// Unlike <see cref="Ao3Pseud.DisplayNameNormalized"/>, this is not left to whoever writes the
    /// row: the <see cref="Title"/> setter maintains it, because a title is written from several
    /// places — the blurb ingest, every test fixture — and a forgotten write here does not fail,
    /// it makes one work unsearchable. EF reads through the backing fields, so materializing a row
    /// keeps whatever the column holds; the setter only runs when application code assigns a title.
    /// </summary>
    public string TitleNormalized { get; private set; } = null!;

    /// <summary>Raw HTML from the blurb's summary blockquote. Sanitize at render time, not here.</summary>
    public string? SummaryHtml { get; set; }

    public Ao3Rating Rating { get; set; }
    public Ao3Category Categories { get; set; }
    public Ao3Warning Warnings { get; set; }
    public bool IsComplete { get; set; }

    public int WordCount { get; set; }
    public int ChapterCount { get; set; }

    /// <summary>Null when AO3 shows "?" for the planned total (i.e. an open-ended WIP).</summary>
    public int? PlannedChapterCount { get; set; }

    public int Hits { get; set; }
    public int Kudos { get; set; }
    public int CommentCount { get; set; }
    public int Bookmarks { get; set; }
    public int CollectionCount { get; set; }

    /// <summary>BCP-47-ish code from the blurb's <c>dd.language[lang]</c> attribute.</summary>
    public string? LanguageCode { get; set; }
    public string? LanguageName { get; set; }

    /// <summary>
    /// AO3's revised/updated timestamp, taken from the <c>&lt;!-- updated_at=EPOCH --&gt;</c>
    /// comment in the blurb header (exact to the second, unlike the day-granular visible date).
    ///
    /// Note this does NOT change when kudos/hits/comments change — it tracks content revisions
    /// only. So it gates whether we need to *fetch* a work's page; it must never be used to
    /// decide whether to *write* the stat columns, or kudos counts would freeze permanently.
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>True when UpdatedAt fell back to the day-granular visible date.</summary>
    public bool UpdatedAtIsApproximate { get; set; }

    /// <summary>
    /// When this instance first saw the work standing at the <see cref="UpdatedAt"/> it holds now —
    /// our clock, unlike <see cref="UpdatedAt"/>, which is AO3's. Null for a row last written before
    /// this column existed, which is "no revision has been observed" and reads as no constraint.
    ///
    /// Distinct from <see cref="LastScrapedAt"/>, which every re-scrape moves whether or not
    /// anything changed. Only a <em>move</em> writes this, and that is what makes it usable as
    /// "anything read before this moment describes the previous version" — which is how a download
    /// knows a cached work page predates the version it is fetching for.
    /// </summary>
    public DateTime? UpdatedAtObservedAt { get; set; }

    /// <summary>
    /// The day the blurb's visible date shows, at UTC midnight: the revision date AO3 itself sorts a
    /// listing by and applies <c>date_from</c> to. <see cref="UpdatedAt"/> is not that clock — the
    /// <c>updated_at</c> comment it holds has been captured running up to eight days ahead — so
    /// anything comparing a work against a window AO3 drew reads this column instead.
    ///
    /// Null until a pass reads a blurb with a legible date, and never backfilled from
    /// <see cref="UpdatedAt"/>, for the same reason. An unreadable date leaves it alone. Which zone
    /// AO3 renders the day in is unverified (a logged-in page may use the account's), so compare it
    /// with a day's slack either side.
    /// </summary>
    public DateTime? RevisedOn { get; set; }

    /// <summary>
    /// Null until fetched. AO3 search blurbs do not carry the published date at all — only a work's
    /// own page has it — so no listing pass can populate this. <c>Ao3WorkDetailScraper</c> fills it
    /// in the background, one request per work, and until it has the detail page says so rather
    /// than showing a date it does not have.
    /// </summary>
    public DateTime? PublishedAt { get; set; }

    public bool IsAnonymous { get; set; }

    /// <summary>Registered-users-only. Such works are invisible to a logged-out scrape entirely.</summary>
    public bool IsRestricted { get; set; }

    public DateTime FirstSeenAt { get; set; }

    /// <summary>Last time this work appeared in any listing — proof it still exists.</summary>
    public DateTime LastSeenAt { get; set; }

    /// <summary>Last time these columns were written from a parsed blurb.</summary>
    public DateTime LastScrapedAt { get; set; }

    /// <summary>
    /// Last time the work's own page was fetched. Two things read it: <c>Ao3WorkDetailScraper</c>,
    /// which asks for a page only when this is null or <see cref="UpdatedAt"/> has moved past it,
    /// and <c>WorkIngestor.ApplyTags</c>, for which it decides whether a listing blurb's tag list is
    /// still the whole of what anyone has observed.
    /// </summary>
    public DateTime? DetailFetchedAt { get; set; }

    /// <summary>
    /// Set only on an authoritative 404 from a detail fetch. A work vanishing from a ship's
    /// listing is NOT this — it almost always means the author removed the relationship tag,
    /// which is recorded on <see cref="ShipWork.MissingSinceAt"/> instead.
    /// </summary>
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    public ICollection<WorkTag> Tags { get; set; } = new List<WorkTag>();
    public ICollection<WorkAuthor> Authors { get; set; } = new List<WorkAuthor>();
    public ICollection<WorkSeries> Series { get; set; } = new List<WorkSeries>();
    public ICollection<ShipWork> Ships { get; set; } = new List<ShipWork>();
}
