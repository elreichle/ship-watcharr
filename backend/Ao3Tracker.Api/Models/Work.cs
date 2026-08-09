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

    public string Title { get; set; } = null!;

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
    /// Null until fetched. AO3 search blurbs do not carry the published date at all — only a
    /// work's own page has it — so scrapes never populate this. It is filled lazily, once, the
    /// first time a user opens the work's detail view.
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
    /// Last time the work's own page was fetched. Guards the lazy published-date fetch:
    /// refetch only when null, or when <see cref="UpdatedAt"/> has moved past it.
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
