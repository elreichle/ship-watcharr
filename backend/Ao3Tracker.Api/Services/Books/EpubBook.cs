using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;

namespace Ao3Tracker.Api.Services.Books;

/// <summary>One item of an EPUB's reading order: where it sits, what it is called, and where its markup lives in the archive.</summary>
public sealed record EpubChapter(int Index, string Title, string Href);

/// <summary>
/// An EPUB opened for reading: its title, its chapters in reading order, and the raw markup of any
/// one of them on request.
/// </summary>
/// <remarks>
/// Built on <see cref="ZipArchive"/> and <see cref="XDocument"/> alone. An EPUB is a zip with a
/// fixed entry point — <c>META-INF/container.xml</c> names the package document, whose
/// <c>&lt;spine&gt;</c> is the reading order — and nothing here assumes more than that. In
/// particular nothing is keyed to the file names AO3 happens to use, so an archive that names its
/// chapters differently still opens: the spine is the reading order, whatever it is called.
///
/// Chapter titles come from the EPUB 2 NCX, which is what AO3 writes, with the EPUB 3 navigation
/// document as the fallback and the chapter's own <c>&lt;title&gt;</c> or first heading behind
/// that. A chapter with none of the three is called by its number.
///
/// The archive is a file AO3 built but this code did not, so it is read on those terms: entries are
/// looked up by the exact archive path the package names, never resolved against a filesystem; the
/// total uncompressed size and the entry count are capped before anything is inflated; and the
/// XML is read with no DTD processing and no resolver, so a package document cannot ask for
/// anything outside the archive.
/// </remarks>
public sealed class EpubBook : IDisposable
{
    /// <summary>
    /// The most an archive may claim to inflate to. An AO3 EPUB is text and runs to a few MB at
    /// most; a zip that claims a thousand times that is a bomb, and is refused before any entry is
    /// opened rather than after the process has found out the hard way.
    /// </summary>
    public const long MaxUncompressedBytes = 64L * 1024 * 1024;

    /// <summary>The most entries an archive may hold, for the same reason.</summary>
    public const int MaxEntries = 4096;

    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
    };

    private static readonly HtmlParser Html = new();

    private readonly ZipArchive _archive;

    private EpubBook(ZipArchive archive, string? title, IReadOnlyList<EpubChapter> chapters)
    {
        _archive = archive;
        Title = title;
        Chapters = chapters;
    }

    /// <summary>The package's own title, or null where it names none.</summary>
    public string? Title { get; }

    /// <summary>The reading order, first to last.</summary>
    public IReadOnlyList<EpubChapter> Chapters { get; }

    /// <summary>
    /// Opens an EPUB and reads its structure. Takes ownership of the stream; disposing the book
    /// closes it. Throws <see cref="EpubFormatException"/> for anything that is not an EPUB this
    /// reader can open.
    /// </summary>
    public static EpubBook Open(Stream stream)
    {
        ZipArchive archive;

        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException e)
        {
            stream.Dispose();
            throw new EpubFormatException("The file is not a zip archive, so it is not an EPUB.", e);
        }

        try
        {
            return Read(archive);
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private static EpubBook Read(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries)
        {
            throw new EpubFormatException($"The archive holds more than {MaxEntries} entries, which no EPUB of one work does.");
        }

        long claimed = 0;
        foreach (var entry in archive.Entries)
        {
            claimed += Math.Max(0, entry.Length);
            if (claimed > MaxUncompressedBytes)
            {
                throw new EpubFormatException("The archive claims to inflate to more than this reader will open.");
            }
        }

        var container = LoadXml(archive, "META-INF/container.xml", "container");
        var packagePath = container.Root?
            .Element(Container + "rootfiles")?
            .Elements(Container + "rootfile")
            .Select(e => e.Attribute("full-path")?.Value)
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

        if (packagePath is null)
        {
            throw new EpubFormatException("The container names no package document.");
        }

        var package = LoadXml(archive, packagePath, "package document");
        var root = package.Root;
        if (root is null || root.Name != Opf + "package")
        {
            throw new EpubFormatException("The package document is not an OPF package.");
        }

        var packageDirectory = DirectoryOf(packagePath);

        var title = root.Element(Opf + "metadata")?.Element(Dc + "title")?.Value.Trim();
        if (string.IsNullOrEmpty(title)) title = null;

        // Manifest: id → (archive path, media type, properties). Hrefs are relative to the package
        // document, and may be percent-encoded, which the archive's entry names are not.
        var manifest = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        foreach (var item in root.Element(Opf + "manifest")?.Elements(Opf + "item") ?? [])
        {
            var id = item.Attribute("id")?.Value;
            var href = item.Attribute("href")?.Value;
            if (id is null || href is null) continue;

            manifest[id] = new ManifestItem(
                Resolve(packageDirectory, href),
                item.Attribute("media-type")?.Value ?? "",
                item.Attribute("properties")?.Value ?? "");
        }

        var spine = root.Element(Opf + "spine");
        if (spine is null)
        {
            throw new EpubFormatException("The package document has no spine, so there is no reading order.");
        }

        // Reading order. Only the documents: an EPUB may put an image or a stylesheet in its spine,
        // and neither is a chapter.
        var ordered = new List<ManifestItem>();
        foreach (var itemref in spine.Elements(Opf + "itemref"))
        {
            var idref = itemref.Attribute("idref")?.Value;
            if (idref is null || !manifest.TryGetValue(idref, out var item)) continue;
            if (!IsDocument(item)) continue;

            if (archive.GetEntry(item.Path) is null)
            {
                throw new EpubFormatException($"The spine names '{item.Path}', which is not in the archive.");
            }

            ordered.Add(item);
        }

        if (ordered.Count == 0)
        {
            throw new EpubFormatException("The spine names no chapter that is in the archive.");
        }

        var titles = ReadNcxTitles(archive, manifest, spine.Attribute("toc")?.Value);
        if (titles.Count == 0) titles = ReadNavTitles(archive, manifest);

        var chapters = new List<EpubChapter>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var path = ordered[i].Path;

            if (!titles.TryGetValue(path, out var chapterTitle))
            {
                chapterTitle = TitleFromDocument(archive, path) ?? $"Chapter {i + 1}";
            }

            chapters.Add(new EpubChapter(i, chapterTitle, path));
        }

        return new EpubBook(archive, title, chapters);
    }

    /// <summary>The raw markup of one chapter, as the archive holds it. The caller sanitizes it.</summary>
    public string ReadChapter(int index)
    {
        if (index < 0 || index >= Chapters.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Not a chapter of this book.");
        }

        return ReadText(_archive, Chapters[index].Href)
            ?? throw new EpubFormatException($"Chapter {index + 1} has left the archive.");
    }

    public void Dispose() => _archive.Dispose();

    // ---- titles --------------------------------------------------------------------------------

    /// <summary>
    /// Chapter titles by archive path, from the EPUB 2 NCX the spine's <c>toc</c> attribute names.
    /// Empty where there is none, or where it cannot be read — a table of contents is a nicety and
    /// not a reason to refuse a book.
    /// </summary>
    private static Dictionary<string, string> ReadNcxTitles(
        ZipArchive archive, Dictionary<string, ManifestItem> manifest, string? tocId)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);

        var ncx = tocId is not null && manifest.TryGetValue(tocId, out var byId)
            ? byId
            : manifest.Values.FirstOrDefault(i => i.MediaType == "application/x-dtbncx+xml");

        if (ncx is null) return titles;

        XDocument document;
        try
        {
            document = LoadXml(archive, ncx.Path, "table of contents");
        }
        catch (EpubFormatException)
        {
            return titles;
        }

        var directory = DirectoryOf(ncx.Path);

        // Document order, which is what puts a chapter's own entry ahead of a sub-heading that
        // points into the same file: the first label for a path wins.
        foreach (var navPoint in document.Descendants(Ncx + "navPoint"))
        {
            var label = navPoint.Element(Ncx + "navLabel")?.Element(Ncx + "text")?.Value.Trim();
            var src = navPoint.Element(Ncx + "content")?.Attribute("src")?.Value;
            if (string.IsNullOrEmpty(label) || src is null) continue;

            titles.TryAdd(Resolve(directory, src), label);
        }

        return titles;
    }

    /// <summary>The same, from an EPUB 3 navigation document, for an archive that has no NCX.</summary>
    private static Dictionary<string, string> ReadNavTitles(ZipArchive archive, Dictionary<string, ManifestItem> manifest)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);

        var nav = manifest.Values.FirstOrDefault(i =>
            i.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav"));
        if (nav is null) return titles;

        var markup = ReadText(archive, nav.Path);
        if (markup is null) return titles;

        var directory = DirectoryOf(nav.Path);
        var document = Html.ParseDocument(markup);

        // The toc nav where one is marked, else the first nav there is.
        var toc = document.QuerySelectorAll("nav")
            .FirstOrDefault(n => (n.GetAttribute("epub:type") ?? "").Split(' ').Contains("toc"))
            ?? document.QuerySelector("nav");
        if (toc is null) return titles;

        foreach (var anchor in toc.QuerySelectorAll("a[href]"))
        {
            var label = anchor.TextContent.Trim();
            var href = anchor.GetAttribute("href");
            if (string.IsNullOrEmpty(label) || href is null) continue;

            titles.TryAdd(Resolve(directory, href), label);
        }

        return titles;
    }

    /// <summary>What the chapter calls itself: its <c>&lt;title&gt;</c>, else its first heading.</summary>
    private static string? TitleFromDocument(ZipArchive archive, string path)
    {
        var markup = ReadText(archive, path);
        if (markup is null) return null;

        var document = Html.ParseDocument(markup);

        var title = document.Title?.Trim();
        if (!string.IsNullOrEmpty(title)) return title;

        var heading = document.QuerySelector("h1, h2, h3")?.TextContent.Trim();
        return string.IsNullOrEmpty(heading) ? null : heading;
    }

    // ---- archive access ------------------------------------------------------------------------

    private static XDocument LoadXml(ZipArchive archive, string path, string what)
    {
        var entry = archive.GetEntry(path)
            ?? throw new EpubFormatException($"The archive has no {what} at '{path}'.");

        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, XmlSettings);
            return XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new EpubFormatException($"The {what} at '{path}' is not well-formed XML.", e);
        }
    }

    private static string? ReadText(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        if (entry is null) return null;

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool IsDocument(ManifestItem item) =>
        item.MediaType is "application/xhtml+xml" or "text/html";

    /// <summary>The directory part of an archive path, with its trailing slash, or empty at the root.</summary>
    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..(slash + 1)];
    }

    /// <summary>
    /// An href as the archive path it names: decoded, stripped of its fragment, and with any
    /// <c>..</c> segments folded, so that the result is an exact entry name and nothing more. An
    /// href that climbs above the archive's root is folded to the root rather than refused — it
    /// then names nothing, and naming nothing is what the spine check catches.
    /// </summary>
    private static string Resolve(string directory, string href)
    {
        var hash = href.IndexOf('#');
        if (hash >= 0) href = href[..hash];

        href = Uri.UnescapeDataString(href);

        var segments = new List<string>();
        foreach (var segment in (directory + href).Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    break;
                case "..":
                    if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                    break;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return string.Join('/', segments);
    }

    private sealed record ManifestItem(string Path, string MediaType, string Properties);
}
