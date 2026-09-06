namespace Ao3Tracker.Api.Models;

/// <summary>
/// Where one user is in one work, as the in-app reader last saw them. PER-USER.
///
/// A table of its own rather than columns on <see cref="UserWorkState"/>: that row is stored as no
/// row at all when it holds nothing, and is replaced whole by every write, while this is written
/// every few seconds for as long as someone is reading. Two write cadences on one row would have
/// the reader's scroll fighting the reader's rating for the same save. Like the state row it is
/// keyed by user and work and untouched by a re-scrape.
/// </summary>
public class ReadingPosition
{
    public int Id { get; set; }

    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    public long WorkId { get; set; }
    public Work Work { get; set; } = null!;

    /// <summary>The chapter, by its place in the book's reading order.</summary>
    public int ChapterIndex { get; set; }

    /// <summary>
    /// The block-level element at the top of the view within that chapter, by its place among the
    /// chapter's top-level children. A paragraph rather than a scroll offset, so the place survives
    /// a change of font size or window width — and, mostly, a new version of the work.
    /// </summary>
    public int BlockIndex { get; set; }

    /// <summary>How far through the whole book, 0 to 1. For the progress bar, not for finding the place.</summary>
    public double Progress { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
