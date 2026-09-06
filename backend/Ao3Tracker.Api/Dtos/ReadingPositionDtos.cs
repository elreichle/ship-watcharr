namespace Ao3Tracker.Api.Dtos;

/// <summary>
/// Where one reader is in one work, as the in-app reader left it. PER-USER.
/// </summary>
/// <param name="ChapterIndex">The chapter, by its place in the book's reading order.</param>
/// <param name="BlockIndex">The block-level element at the top of the reader's view within that
/// chapter, by its place among the chapter's top-level children. A paragraph rather than a scroll
/// offset, so the place survives a change of font size or window width.</param>
/// <param name="Progress">How far through the whole book, 0 to 1, for a progress bar.</param>
public record ReadingPositionDto(int ChapterIndex, int BlockIndex, double Progress, DateTime UpdatedAt);

/// <summary>A replacement for the caller's position in one work. The whole position, every time.</summary>
public record SetReadingPositionRequest(int ChapterIndex, int BlockIndex, double Progress);
