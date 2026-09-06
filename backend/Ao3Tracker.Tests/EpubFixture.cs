using System.IO.Compression;
using System.Text;

namespace Ao3Tracker.Tests;

/// <summary>
/// EPUBs built in memory, in the shape AO3 writes them: a container naming one package document,
/// a package with a manifest and a spine, an NCX for chapter titles, and one XHTML file per
/// chapter. Every test that opens a book starts from one of these rather than from a checked-in
/// binary, so what a test asserts about is what it can see in the test.
/// </summary>
internal static class EpubFixture
{
    public sealed record Chapter(string Href, string Title, string Body);

    /// <summary>
    /// A whole book. The manifest lists the chapters in reverse so that a reader following it
    /// rather than the spine gets them backwards.
    /// </summary>
    /// <param name="ncx">Whether to write a table of contents. Without one the titles have to come
    /// from the chapters themselves.</param>
    /// <param name="nav">Whether to write an EPUB 3 navigation document instead.</param>
    public static byte[] Book(string title, IReadOnlyList<Chapter> chapters, bool ncx = true, bool nav = false)
    {
        var manifest = new StringBuilder();
        foreach (var chapter in chapters.Reverse())
        {
            manifest.Append(
                $"<item id=\"{Id(chapter.Href)}\" href=\"{chapter.Href}\" media-type=\"application/xhtml+xml\"/>");
        }
        if (ncx) manifest.Append("<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>");
        if (nav) manifest.Append("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
        manifest.Append("<item id=\"css\" href=\"stylesheet.css\" media-type=\"text/css\"/>");

        var spine = new StringBuilder();
        foreach (var chapter in chapters) spine.Append($"<itemref idref=\"{Id(chapter.Href)}\"/>");
        // A stylesheet in the spine is not a chapter, and a real archive can carry one.
        spine.Append("<itemref idref=\"css\"/>");

        var opf =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\" unique-identifier=\"id\">"
            + "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">"
            + $"<dc:title>{title}</dc:title><dc:identifier id=\"id\">work-1</dc:identifier>"
            + "</metadata>"
            + $"<manifest>{manifest}</manifest>"
            + $"<spine{(ncx ? " toc=\"ncx\"" : "")}>{spine}</spine>"
            + "</package>";

        var entries = new List<(string Path, string Content)>
        {
            ("OEBPS/content.opf", opf),
            ("OEBPS/stylesheet.css", "p { margin: 0 }"),
        };

        if (ncx)
        {
            var points = new StringBuilder();
            var order = 1;
            foreach (var chapter in chapters)
            {
                points.Append(
                    $"<navPoint id=\"np{order}\" playOrder=\"{order}\"><navLabel><text>{chapter.Title}</text></navLabel>"
                    + $"<content src=\"{chapter.Href}#top\"/></navPoint>");
                order++;
            }

            entries.Add(("OEBPS/toc.ncx",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<!DOCTYPE ncx PUBLIC \"-//NISO//DTD ncx 2005-1//EN\" \"http://www.daisy.org/z3986/2005/ncx-2005-1.dtd\">"
                + "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">"
                + $"<docTitle><text>{title}</text></docTitle><navMap>{points}</navMap></ncx>"));
        }

        if (nav)
        {
            var links = new StringBuilder();
            foreach (var chapter in chapters) links.Append($"<li><a href=\"{chapter.Href}\">{chapter.Title}</a></li>");

            entries.Add(("OEBPS/nav.xhtml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">"
                + "<head><title>Contents</title></head><body>"
                + $"<nav epub:type=\"toc\"><ol>{links}</ol></nav></body></html>"));
        }

        foreach (var chapter in chapters)
        {
            entries.Add(($"OEBPS/{Uri.UnescapeDataString(chapter.Href)}", Xhtml(chapter.Title, chapter.Body)));
        }

        return Archive(entries, container: true);
    }

    /// <summary>One chapter file, as AO3 writes one: a full XHTML document with a title of its own.</summary>
    public static string Xhtml(string? title, string body) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
        + "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">"
        + "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head>"
        + (title is null ? "" : $"<title>{title}</title>")
        + "<link rel=\"stylesheet\" href=\"stylesheet.css\" type=\"text/css\"/>"
        + $"</head><body>{body}</body></html>";

    /// <summary>A zip of exactly these entries, with or without the container an EPUB starts from.</summary>
    public static byte[] Archive(IEnumerable<(string Path, string Content)> entries, bool container)
    {
        using var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);

            if (container)
            {
                Write(archive, "META-INF/container.xml",
                    "<?xml version=\"1.0\"?>"
                    + "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">"
                    + "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/>"
                    + "</rootfiles></container>");
            }

            foreach (var (path, content) in entries) Write(archive, path, content);
        }

        return stream.ToArray();
    }

    private static void Write(ZipArchive archive, string path, string content, CompressionLevel level = CompressionLevel.Fastest)
    {
        var entry = archive.CreateEntry(path, level);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string Id(string href) => "item-" + string.Concat(href.Where(char.IsLetterOrDigit));
}
