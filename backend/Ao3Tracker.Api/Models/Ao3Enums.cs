namespace Ao3Tracker.Api.Models;

/// <summary>
/// AO3's content rating. Ordered by ascending explicitness so range filters
/// ("Teen and below") translate to a plain <c>&lt;=</c> comparison on an indexed column.
/// </summary>
public enum Ao3Rating
{
    Unknown = 0,
    NotRated = 1,
    GeneralAudiences = 2,
    TeenAndUpAudiences = 3,
    Mature = 4,
    Explicit = 5,
}

/// <summary>
/// AO3 work categories. A work can carry several, so this is a flags enum rather than a
/// single value — the blurb's category <c>title</c> attribute is a comma-separated list
/// (e.g. "F/M, M/M, Multi"), not a single token.
///
/// Stored as a plain int. Do NOT add HasConversion&lt;string&gt;(): filtering is
/// <c>(Categories &amp; mask) != 0</c>, and a string column makes that untranslatable.
/// </summary>
[Flags]
public enum Ao3Category
{
    None = 0,
    FF = 1 << 0,
    FM = 1 << 1,
    Gen = 1 << 2,
    MM = 1 << 3,
    Multi = 1 << 4,
    Other = 1 << 5,

    /// <summary>AO3's explicit "No category" choice — distinct from "we didn't parse one".</summary>
    NoCategory = 1 << 6,

    /// <summary>
    /// Set when AO3 shows a category token this build doesn't recognise. Deliberately a set
    /// bit rather than a thrown exception: AO3 occasionally renames its controlled vocabulary,
    /// and a rename must produce a greppable log line, not a dead multi-hour backfill.
    /// </summary>
    Unknown = 1 << 30,
}

/// <summary>
/// AO3 archive warnings. Flags for the same reason as <see cref="Ao3Category"/> — the
/// warning <c>title</c> attribute is a comma-separated list
/// (e.g. "Graphic Depictions Of Violence, Rape/Non-Con").
/// </summary>
[Flags]
public enum Ao3Warning
{
    None = 0,
    NoArchiveWarningsApply = 1 << 0,
    ChooseNotToUseArchiveWarnings = 1 << 1,
    GraphicDepictionsOfViolence = 1 << 2,
    MajorCharacterDeath = 1 << 3,
    RapeNonCon = 1 << 4,
    Underage = 1 << 5,

    /// <summary>See <see cref="Ao3Category.Unknown"/>. AO3 renamed "Underage" to "Underage Sex" in 2024.</summary>
    Unknown = 1 << 30,
}

/// <summary>
/// Which AO3 tag list a tag came from. Fandoms come from the blurb's <c>h5.fandoms</c>;
/// the rest come from <c>ul.tags.commas</c>. Categories are NOT tags — they exist only as
/// <see cref="Ao3Category"/> flags, because AO3 does not list them in the tag list.
/// </summary>
public enum Ao3TagType : byte
{
    Fandom = 1,
    Relationship = 2,
    Character = 3,
    Freeform = 4,

    /// <summary>
    /// Warnings appear both here and as <see cref="Ao3Warning"/> flags. Not redundant: the tag
    /// rows make the UI's tag list match AO3's and keep warnings findable by tag search, while
    /// the flags column is the authoritative, indexable filter.
    /// </summary>
    Warning = 5,
}

/// <summary>A user's reading progress for one work. Per-user data; never touched by a scrape.</summary>
public enum ReadingStatus : byte
{
    None = 0,
    ToRead = 1,
    Reading = 2,
    Read = 3,
    Dropped = 4,
}

public enum Ao3DownloadFormat : byte
{
    Epub = 1,
    Mobi = 2,
    Pdf = 3,
    Html = 4,
    Azw3 = 5,
}

public enum DownloadStatus : byte
{
    Pending = 0,
    Downloading = 1,
    Complete = 2,
    Failed = 3,
}

/// <summary>
/// What a scrape run was trying to do. The scheduler prefers <see cref="Incremental"/> over
/// <see cref="Backfill"/>: all outbound requests share one global gate, so a multi-hour
/// backfill would otherwise starve every other ship's cheap daily update.
/// </summary>
public enum ScrapeRunMode : byte
{
    /// <summary>Newest-first, bounded by the ship's watermark. Normally a single request.</summary>
    Incremental = 0,

    /// <summary>Resumable walk into the tag's back catalogue, bounded by the per-run budget.</summary>
    Backfill = 1,

    /// <summary>Full re-walk. The only pass allowed to conclude a work has left the tag.</summary>
    FullSweep = 2,

    /// <summary>Per-work detail page fetch (published date, deletion checks).</summary>
    Detail = 3,
}

/// <summary>
/// Whether AO3 has confirmed a tag exists. A tag is accepted on trust when someone follows it and
/// checked afterwards, because checking costs a real AO3 request behind a 5–8s gate — making the
/// user wait on that would be a worse trade than telling them shortly after.
/// </summary>
public enum ShipVerificationState : byte
{
    /// <summary>
    /// Not checked yet, or a check failed in a way that says nothing about the tag (AO3 down, no
    /// operator contact configured, connection refused). Retried with backoff, indefinitely: an
    /// archive being unreachable for a day is not evidence about a tag.
    /// </summary>
    Pending = 0,

    /// <summary>AO3 served the tag's works index.</summary>
    Verified = 1,

    /// <summary>
    /// AO3 returned 404. Almost always a typo. Terminal — the ship's schedule is switched off,
    /// since there is nothing there to scrape.
    /// </summary>
    NotFoundOnAo3 = 2,
}

public enum ShipBackfillState : byte
{
    NotStarted = 0,
    InProgress = 1,
    Complete = 2,
    Failed = 3,
}
