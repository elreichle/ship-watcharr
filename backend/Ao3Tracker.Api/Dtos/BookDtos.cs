namespace Ao3Tracker.Api.Dtos;

/// <summary>One chapter's place and name in a book's reading order, without its text.</summary>
public record ChapterHeadingDto(int Index, string Title);

/// <summary>
/// A downloaded EPUB opened for reading in the app: what it is, and the chapters it is made of.
/// The text of a chapter is a separate request — see <see cref="ChapterDto"/> — so that opening a
/// long work costs one chapter's worth of markup, not the whole book's.
/// </summary>
/// <param name="Title">The book's own title, falling back to the work's where the package has none.</param>
/// <param name="IsEarlierCopy">True where the bytes behind this are the copy the reader held before
/// asking for a newer version of the work — the same distinction the Downloads page draws with
/// "Save earlier copy".</param>
public record BookDto(
    int DownloadId,
    long WorkId,
    string Title,
    bool IsEarlierCopy,
    IReadOnlyList<ChapterHeadingDto> Chapters);

/// <summary>
/// One chapter, sanitized for rendering. <paramref name="Html"/> has been through
/// <c>WorkChapterHtml</c>: an allowlist of elements and three checked attributes, so what arrives
/// at the client has nowhere left to carry script.
/// </summary>
public record ChapterDto(int Index, string Title, string Html);
