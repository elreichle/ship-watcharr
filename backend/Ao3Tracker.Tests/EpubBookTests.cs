using System.IO.Compression;
using Ao3Tracker.Api.Services.Books;

namespace Ao3Tracker.Tests;

/// <summary>
/// Opening an EPUB and finding its chapters. The archive is a file AO3 built and this app did
/// not, so the questions are the ones a file can get wrong: the order, the names, an href that is
/// spelled differently from the entry it names, and the shapes that are not a book at all.
/// </summary>
public class EpubBookTests
{
    private static readonly EpubFixture.Chapter[] ThreeChapters =
    [
        new("preface.xhtml", "Preface", "<p>By someone.</p>"),
        new("chapter1.xhtml", "Chapter 1: Woods", "<p>They meet.</p>"),
        new("chapter2.xhtml", "Chapter 2: Home", "<p>They part.</p>"),
    ];

    [Fact]
    public void Reads_the_chapters_in_spine_order()
    {
        using var book = Open(EpubFixture.Book("A Work", ThreeChapters));

        Assert.Equal("A Work", book.Title);
        // The manifest lists them backwards on purpose; the spine is the reading order.
        Assert.Equal(["OEBPS/preface.xhtml", "OEBPS/chapter1.xhtml", "OEBPS/chapter2.xhtml"],
            book.Chapters.Select(c => c.Href));
        Assert.Equal([0, 1, 2], book.Chapters.Select(c => c.Index));
    }

    [Fact]
    public void Names_the_chapters_from_the_table_of_contents()
    {
        using var book = Open(EpubFixture.Book("A Work", ThreeChapters));

        Assert.Equal(["Preface", "Chapter 1: Woods", "Chapter 2: Home"], book.Chapters.Select(c => c.Title));
    }

    [Fact]
    public void Leaves_a_stylesheet_in_the_spine_out_of_the_chapters()
    {
        using var book = Open(EpubFixture.Book("A Work", ThreeChapters));

        Assert.Equal(3, book.Chapters.Count);
    }

    [Fact]
    public void Reads_a_chapter_as_the_archive_holds_it()
    {
        using var book = Open(EpubFixture.Book("A Work", ThreeChapters));

        var markup = book.ReadChapter(1);

        Assert.Contains("<p>They meet.</p>", markup);
        Assert.Contains("<title>Chapter 1: Woods</title>", markup);
    }

    [Fact]
    public void Refuses_a_chapter_the_book_does_not_have()
    {
        using var book = Open(EpubFixture.Book("A Work", ThreeChapters));

        Assert.Throws<ArgumentOutOfRangeException>(() => book.ReadChapter(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.ReadChapter(-1));
    }

    [Fact]
    public void Names_chapters_by_their_own_title_then_heading_then_number_without_a_table_of_contents()
    {
        var chapters = new EpubFixture.Chapter[]
        {
            new("one.xhtml", "Titled", "<p>one</p>"),
            new("two.xhtml", null!, "<h2>Headed</h2><p>two</p>"),
            new("three.xhtml", null!, "<p>three</p>"),
        };

        using var book = Open(EpubFixture.Book("A Work", chapters, ncx: false));

        Assert.Equal(["Titled", "Headed", "Chapter 3"], book.Chapters.Select(c => c.Title));
    }

    [Fact]
    public void Names_chapters_from_an_epub3_navigation_document_when_there_is_no_ncx()
    {
        using var book = Open(EpubFixture.Book("A Work", ThreeChapters, ncx: false, nav: true));

        Assert.Equal(["Preface", "Chapter 1: Woods", "Chapter 2: Home"], book.Chapters.Select(c => c.Title));
        // The navigation document is not a chapter, whatever its media type says.
        Assert.Equal(3, book.Chapters.Count);
    }

    [Fact]
    public void Resolves_a_percent_encoded_href_to_the_entry_it_names()
    {
        // The manifest says "my%20chapter.xhtml"; the archive holds "my chapter.xhtml". Both are
        // the same file, and the table of contents points at it with a fragment on top.
        var chapters = new EpubFixture.Chapter[] { new("my%20chapter.xhtml", "Spaced", "<p>hi</p>") };

        using var book = Open(EpubFixture.Book("A Work", chapters));

        var chapter = Assert.Single(book.Chapters);
        Assert.Equal("OEBPS/my chapter.xhtml", chapter.Href);
        Assert.Equal("Spaced", chapter.Title);
        Assert.Contains("<p>hi</p>", book.ReadChapter(0));
    }

    [Fact]
    public void Refuses_bytes_that_are_not_a_zip()
    {
        var e = Assert.Throws<EpubFormatException>(() => Open("<html>a login page</html>"u8.ToArray()));

        Assert.Contains("not a zip", e.Message);
    }

    [Fact]
    public void Refuses_an_archive_with_no_container()
    {
        var bytes = EpubFixture.Archive([("OEBPS/content.opf", "<package/>")], container: false);

        var e = Assert.Throws<EpubFormatException>(() => Open(bytes));

        Assert.Contains("container", e.Message);
    }

    [Fact]
    public void Refuses_a_spine_that_names_an_entry_the_archive_does_not_hold()
    {
        var bytes = EpubFixture.Archive(
        [
            ("OEBPS/content.opf",
                "<package xmlns=\"http://www.idpf.org/2007/opf\"><manifest>"
                + "<item id=\"c\" href=\"missing.xhtml\" media-type=\"application/xhtml+xml\"/></manifest>"
                + "<spine><itemref idref=\"c\"/></spine></package>"),
        ], container: true);

        var e = Assert.Throws<EpubFormatException>(() => Open(bytes));

        Assert.Contains("missing.xhtml", e.Message);
    }

    [Fact]
    public void Refuses_a_package_that_climbs_out_of_the_archive()
    {
        // "../../etc/passwd" is folded to a name inside the archive, which then names nothing. The
        // point is that no path out of the package ever reaches a filesystem call.
        var bytes = EpubFixture.Archive(
        [
            ("OEBPS/content.opf",
                "<package xmlns=\"http://www.idpf.org/2007/opf\"><manifest>"
                + "<item id=\"c\" href=\"../../../../etc/passwd\" media-type=\"application/xhtml+xml\"/></manifest>"
                + "<spine><itemref idref=\"c\"/></spine></package>"),
        ], container: true);

        var e = Assert.Throws<EpubFormatException>(() => Open(bytes));

        Assert.Contains("'etc/passwd'", e.Message);
    }

    [Fact]
    public void Refuses_an_archive_that_claims_to_inflate_past_the_cap()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("OEBPS/big.xhtml", CompressionLevel.Optimal).Open();
            var zeros = new byte[1024 * 1024];
            for (var written = 0L; written <= EpubBook.MaxUncompressedBytes; written += zeros.Length)
            {
                entry.Write(zeros);
            }
        }

        var e = Assert.Throws<EpubFormatException>(() => Open(stream.ToArray()));

        Assert.Contains("inflate", e.Message);
    }

    [Fact]
    public void Reads_a_package_whose_documents_carry_a_doctype()
    {
        // AO3's NCX and chapters both declare a DTD. Read with DTD processing off, the declaration
        // has to be skipped rather than fetched or refused.
        using var book = Open(EpubFixture.Book("A Work", ThreeChapters));

        Assert.Equal("Preface", book.Chapters[0].Title);
    }

    private static EpubBook Open(byte[] bytes) => EpubBook.Open(new MemoryStream(bytes));
}
