namespace Ao3Tracker.Api.Services.Books;

/// <summary>
/// The file is not an EPUB this reader can open: not a zip, or a zip with no container, no package
/// document, or a spine pointing at entries that are not there. One exception for every such
/// shape, because the caller has one answer to all of them — the file cannot be read here, and
/// the message says which part of it was wrong.
/// </summary>
public sealed class EpubFormatException : Exception
{
    public EpubFormatException(string message) : base(message)
    {
    }

    public EpubFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}
