using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// Reading AO3's listing markup.
///
/// The parser is the one piece of this system whose correctness is decided by somebody else's HTML,
/// so these tests are written as a record of what that HTML is assumed to look like. When AO3
/// changes and a field starts coming back empty, the fixture below is the thing to re-capture and
/// diff — not the parser.
///
/// The other half of what is being pinned down here is the degradation rule: a blurb missing a field
/// yields the rest of its fields, and a blurb that cannot be read at all does not take the rest of
/// the page down with it.
/// </summary>
public class Ao3BlurbParserTests
{
    // ---- a whole blurb -------------------------------------------------------------------------

    [Fact]
    public void Reads_the_core_fields_of_a_work()
    {
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(FullBlurb)).Works);

        Assert.Equal(12345678, work.WorkId);
        Assert.Equal("A Study in Lexa", work.Title);
        Assert.Contains("They meet in the woods", work.SummaryHtml);
        Assert.Equal("en", work.LanguageCode);
        Assert.Equal("English", work.LanguageName);
    }

    [Fact]
    public void Reads_the_statistics()
    {
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(FullBlurb)).Works);

        // Every one of these arrives with AO3's thousands separators.
        Assert.Equal(12345, work.WordCount);
        Assert.Equal(45678, work.Hits);
        Assert.Equal(6789, work.Kudos);
        Assert.Equal(234, work.CommentCount);
        Assert.Equal(1011, work.Bookmarks);
        Assert.Equal(2, work.CollectionCount);
    }

    [Fact]
    public void Reads_the_required_tag_symbols()
    {
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(FullBlurb)).Works);

        Assert.Equal(Ao3Rating.TeenAndUpAudiences, work.Rating);
        Assert.Equal(Ao3Category.FF, work.Categories);
        Assert.Equal(Ao3Warning.NoArchiveWarningsApply, work.Warnings);
        Assert.True(work.IsComplete);
    }

    // ---- AO3's controlled vocabulary ------------------------------------------------------------

    [Fact]
    public void Reads_every_value_of_a_multi_valued_symbol()
    {
        // Categories and warnings are comma-separated lists inside one title attribute, which is
        // why they are flags rather than single values.
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(Blurb(
            category: "F/F, M/M, Multi",
            warnings: "Graphic Depictions Of Violence, Major Character Death"))).Works);

        Assert.Equal(Ao3Category.FF | Ao3Category.MM | Ao3Category.Multi, work.Categories);
        Assert.Equal(
            Ao3Warning.GraphicDepictionsOfViolence | Ao3Warning.MajorCharacterDeath, work.Warnings);
    }

    [Fact]
    public void Flags_a_vocabulary_it_does_not_recognise_rather_than_throwing()
    {
        // AO3 renames these. A rename must produce a greppable flag, not a dead backfill.
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(Blurb(
            category: "F/F, Something New",
            warnings: "Underage Sex"))).Works);

        Assert.True(work.Categories.HasFlag(Ao3Category.FF));
        Assert.True(work.Categories.HasFlag(Ao3Category.Unknown));

        // The 2024 rename of "Underage" is a known alias, so it is understood rather than flagged.
        Assert.Equal(Ao3Warning.Underage, work.Warnings);
    }

    [Fact]
    public void Treats_an_unfinished_work_as_incomplete()
    {
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(Blurb(isWip: "Work in Progress"))).Works);
        Assert.False(work.IsComplete);
    }

    // ---- tags ------------------------------------------------------------------------------------

    [Fact]
    public void Types_each_tag_by_the_list_it_came_from()
    {
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(FullBlurb)).Works);

        Assert.Equal(["The 100 (TV)"], Named(work, Ao3TagType.Fandom));
        Assert.Equal(["Clarke Griffin/Lexa"], Named(work, Ao3TagType.Relationship));
        Assert.Equal(["Clarke Griffin", "Lexa"], Named(work, Ao3TagType.Character));
        Assert.Equal(["Fluff", "Slow Burn"], Named(work, Ao3TagType.Freeform));
        Assert.Equal(["No Archive Warnings Apply"], Named(work, Ao3TagType.Warning));
    }

    [Fact]
    public void Records_a_tag_once_even_though_AO3_renders_it_twice()
    {
        // A warning appears both as a required-tag symbol and in the tag list. Two rows would
        // violate the (WorkId, TagId) primary key and fail the whole page's save.
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(FullBlurb)).Works);

        Assert.Single(work.Tags, t => t.Name == "No Archive Warnings Apply");
    }

    // ---- creators --------------------------------------------------------------------------------

    [Fact]
    public void Credits_only_the_authors_never_the_gift_recipient()
    {
        // AO3 renders recipients as links in the same heading. Selecting all anchors would record
        // whoever a work was gifted to as having written it.
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(FullBlurb)).Works);

        var author = Assert.Single(work.Authors);
        Assert.Equal("someuser", author.Username);
        Assert.Equal("somepseud", author.PseudName);
    }

    [Fact]
    public void Reads_a_default_pseud_from_a_bare_user_link()
    {
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(Blurb(
            byline: """<a rel="author" href="/users/plainuser">plainuser</a>"""))).Works);

        var author = Assert.Single(work.Authors);
        Assert.Equal("plainuser", author.Username);
        Assert.Equal("plainuser", author.PseudName);
    }

    [Fact]
    public void Treats_a_byline_with_no_author_link_as_anonymous()
    {
        // AO3 replaces the byline with plain text for anonymous works.
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(Blurb(byline: "Anonymous"))).Works);

        Assert.True(work.IsAnonymous);
        Assert.Empty(work.Authors);
    }

    [Fact]
    public void Counts_a_byline_it_cannot_read_as_a_parse_warning()
    {
        // The shortfall this file's summary describes, for the one field that never reported it.
        // A heading naming nobody and not saying "Anonymous" is a heading whose shape has moved,
        // and the run record is the only place that can say so.
        var page = Ao3BlurbParser.ParseListing(Page(Blurb(byline: """<span class="byline">somepseud</span>""")));

        Assert.Equal(1, page.ParseWarnings);

        // Null, not "anonymous": the work is not claimed to have no creators, only to have none
        // that were read. WorkIngestor declines to touch authorship on that.
        Assert.Null(Assert.Single(page.Works).IsAnonymous);
    }

    [Fact]
    public void Counts_an_author_link_it_cannot_parse_as_a_parse_warning()
    {
        // rel="author" present, but the href is not a pseud path. That is a byline, so the work is
        // not anonymous; it is simply one we failed to read.
        var page = Ao3BlurbParser.ParseListing(Page(Blurb(
            byline: """<a rel="author" href="/creators/someuser">somepseud</a>""")));

        Assert.Equal(1, page.ParseWarnings);
        Assert.Null(Assert.Single(page.Works).IsAnonymous);
    }

    [Fact]
    public void Reads_an_anonymous_byline_without_a_warning_even_when_the_work_is_a_gift()
    {
        // "Anonymous" is the whole of what distinguishes a work with no creators from a work whose
        // creators we could not read, so it has to survive the rest of the heading's furniture.
        var page = Ao3BlurbParser.ParseListing(Page(Blurb(
            byline: """Anonymous for <a href="/users/giftee">giftee</a>""")));

        Assert.Equal(0, page.ParseWarnings);

        var work = Assert.Single(page.Works);
        Assert.True(work.IsAnonymous);
        Assert.Empty(work.Authors);
    }

    [Fact]
    public void Does_not_read_a_work_titled_Anonymous_as_having_no_author()
    {
        // The title is the one part of the heading that is somebody else's words.
        var page = Ao3BlurbParser.ParseListing(Page(Blurb(
            title: "Anonymous", byline: """<span class="byline">somepseud</span>""")));

        Assert.Equal(1, page.ParseWarnings);
        Assert.Null(Assert.Single(page.Works).IsAnonymous);
    }

    [Theory]
    [InlineData("""<span class="byline">somepseud</span> for Anonymous""")]
    [InlineData("""<span class="byline">somepseud</span> for <a href="/users/Anonymous">Anonymous</a>""")]
    public void Does_not_read_a_gift_to_Anonymous_as_a_work_with_no_author(string byline)
    {
        // The failure mode this closes is the one T26 exists to prevent, reached the long way round:
        // if AO3 ever drops rel="author", every blurb arrives here crediting nobody, and a heading
        // whose *recipient* is anonymous would then be read as an anonymous work — deleting the
        // creators of exactly the works that were gifted.
        var page = Ao3BlurbParser.ParseListing(Page(Blurb(byline: byline)));

        Assert.Equal(1, page.ParseWarnings);
        Assert.Null(Assert.Single(page.Works).IsAnonymous);
    }

    [Fact]
    public void Keeps_the_authors_it_could_read_when_one_of_them_is_unreadable()
    {
        // One odd anchor is not a reshaped heading. The creators that were read are still real, and
        // the shortfall is reported rather than paid for by discarding them.
        var page = Ao3BlurbParser.ParseListing(Page(Blurb(byline: """
            <a rel="author" href="/users/first/pseuds/first">first</a>,
            <a rel="author" href="/creators/second">second</a>
            """)));

        var work = Assert.Single(page.Works);

        Assert.Equal(["first"], work.Authors.Select(a => a.Username));
        Assert.False(work.IsAnonymous);
        Assert.Equal(1, page.ParseWarnings);
    }

    [Fact]
    public void Keeps_multiple_creators_in_byline_order()
    {
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(Blurb(byline: """
            <a rel="author" href="/users/first/pseuds/first">first</a>,
            <a rel="author" href="/users/second/pseuds/second">second</a>
            """))).Works);

        Assert.Equal(["first", "second"], work.Authors.Select(a => a.Username));
    }

    // ---- chapters and series ----------------------------------------------------------------------

    [Theory]
    [InlineData("3/10", 3, 10)]
    [InlineData("1/1", 1, 1)]
    [InlineData("7/?", 7, null)]
    public void Reads_posted_and_planned_chapter_counts(string text, int posted, int? planned)
    {
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(Blurb(chapters: text))).Works);

        Assert.Equal(posted, work.ChapterCount);

        // "?" is an open-ended WIP, which is a different claim from "planned equals posted".
        Assert.Equal(planned, work.PlannedChapterCount);
    }

    [Fact]
    public void Reads_series_membership_with_its_part_number()
    {
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(FullBlurb)).Works);

        var series = Assert.Single(work.Series);
        Assert.Equal(987654, series.Id);
        Assert.Equal("The Woods Sequence", series.Title);
        Assert.Equal(2, series.Part);
    }

    // ---- timestamps --------------------------------------------------------------------------------

    [Fact]
    public void Prefers_the_exact_revision_timestamp_over_the_visible_date()
    {
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(FullBlurb)).Works);

        Assert.Equal(new DateTime(2023, 12, 25, 18, 30, 0, DateTimeKind.Utc), work.UpdatedAt);
        Assert.False(work.UpdatedAtIsApproximate);
    }

    [Fact]
    public void Falls_back_to_the_visible_date_and_says_so()
    {
        // The visible date is day-granular, so a row built from it must be marked approximate —
        // anything comparing revision times needs to know how much precision it has.
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(Blurb(updatedAtComment: null))).Works);

        Assert.Equal(new DateTime(2023, 12, 25, 0, 0, 0, DateTimeKind.Utc), work.UpdatedAt);
        Assert.True(work.UpdatedAtIsApproximate);
    }

    [Fact]
    public void Leaves_an_unreadable_date_at_MinValue_rather_than_now()
    {
        // Stamping it with the current time would make the work look like the newest thing in the
        // tag and truncate the next incremental pass at it.
        var page = Ao3BlurbParser.ParseListing(Page(Blurb(updatedAtComment: null, datetime: "not a date")));

        Assert.Equal(DateTime.MinValue, Assert.Single(page.Works).UpdatedAt);
        Assert.Equal(1, page.ParseWarnings);
    }

    // ---- page-level facts ---------------------------------------------------------------------------

    [Fact]
    public void Reads_the_total_from_the_listing_heading()
    {
        // The heading is a range — "1 - 20 of 4,317 Works" — so the total is the number before
        // "Works", not the first number in the string.
        Assert.Equal(4317, Ao3BlurbParser.ParseListing(Page(FullBlurb)).TotalWorks);
    }

    [Fact]
    public void Reports_no_total_rather_than_zero_when_the_heading_is_missing()
    {
        // An unknown total and an empty tag are different facts, and only one of them should be
        // allowed to overwrite the ship's last known count.
        var html = $"<div id='main'><ol class='work index group'>{FullBlurb}</ol></div>";

        Assert.Null(Ao3BlurbParser.ParseListing(html).TotalWorks);
    }

    [Fact]
    public void Detects_a_next_page_through_AO3s_arrow_glyph()
    {
        Assert.True(Ao3BlurbParser.ParseListing(Page(FullBlurb, nextPage: true)).HasNextPage);
        Assert.False(Ao3BlurbParser.ParseListing(Page(FullBlurb)).HasNextPage);
    }

    [Fact]
    public void Ignores_blurbs_outside_the_results_listing()
    {
        // AO3 renders the same blurb shape in sidebar modules. Ingesting those would attribute
        // unrelated works to the tag being walked.
        var html = $"""
            <div id="main">
              <h2 class="heading">1 - 1 of 1 Works in Clarke Griffin/Lexa</h2>
              <ol class="work index group">{FullBlurb}</ol>
              <div class="module"><ol class="index group"><li id="work_999" class="work blurb group">
                <h4 class="heading"><a href="/works/999">Sidebar work</a></h4>
              </li></ol></div>
            </div>
            """;

        Assert.Equal([12345678], Ao3BlurbParser.ParseListing(html).Works.Select(w => w.WorkId));
    }

    // ---- degradation ---------------------------------------------------------------------------------

    [Fact]
    public void One_unreadable_blurb_does_not_discard_the_rest_of_the_page()
    {
        var page = Ao3BlurbParser.ParseListing(Page($"""
            <li class="work blurb group"><h4 class="heading">no id at all</h4></li>
            {FullBlurb}
            """));

        Assert.Equal([12345678], page.Works.Select(w => w.WorkId));
    }

    [Fact]
    public void Keeps_a_work_whose_title_is_unreadable()
    {
        // The work is still real and still in this ship's index, so it is worth recording as seen.
        // Given a readable date, so the two warnings counted below are exactly the two fields this
        // heading fails to yield: the title, and the byline — an empty heading credits nobody and
        // does not say "Anonymous", which is the definition of a byline that was not read.
        var page = Ao3BlurbParser.ParseListing(Page("""
            <li id="work_555" class="work blurb group">
              <h4 class="heading"></h4>
              <p class="datetime">25 Dec 2023</p>
            </li>
            """));

        var work = Assert.Single(page.Works);
        Assert.Equal(555, work.WorkId);
        Assert.Equal("Unknown work 555", work.Title);
        Assert.Equal(2, page.ParseWarnings);
        Assert.Null(work.IsAnonymous);
    }

    [Fact]
    public void Reports_a_restricted_work()
    {
        // Restricted works are invisible to a logged-out scrape, so seeing one proves the run was
        // authenticated.
        var work = Assert.Single(Ao3BlurbParser.ParseListing(Page(Blurb(restricted: true))).Works);
        Assert.True(work.IsRestricted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html><body><p>Retry later</p></body></html>")]
    public void Yields_nothing_from_a_page_with_no_listing(string? html)
    {
        var page = Ao3BlurbParser.ParseListing(html);

        Assert.Empty(page.Works);
        Assert.False(page.HasNextPage);
    }

    // ---- fixtures ---------------------------------------------------------------------------------------

    private static IEnumerable<string> Named(Ao3WorkBlurb work, Ao3TagType type) =>
        work.Tags.Where(t => t.Type == type).Select(t => t.Name);

    private static string Page(string blurbs, bool nextPage = false) => $"""
        <div id="main">
          <h2 class="heading">1 - 20 of 4,317 Works in Clarke Griffin/Lexa</h2>
          <ol class="work index group">{blurbs}</ol>
          {(nextPage ? """<ol class="pagination actions"><li><a href="?page=2">Next <span class="arrow">&rarr;</span></a></li></ol>""" : "")}
        </div>
        """;

    private const string FullBlurb = """
        <li id="work_12345678" class="work blurb group">
          <div class="header module">
            <h4 class="heading">
              <a href="/works/12345678">A Study in Lexa</a>
              by
              <a rel="author" href="/users/someuser/pseuds/somepseud">somepseud (someuser)</a>
              for <a href="/users/giftee">giftee</a>
            </h4>
            <h5 class="fandoms heading">
              <span class="landmark">Fandoms:</span>
              <a class="tag" href="/tags/The%20100%20(TV)/works">The 100 (TV)</a>
            </h5>
            <ul class="required-tags">
              <li><span class="rating-teen rating" title="Teen And Up Audiences"></span></li>
              <li><span class="warning-no warnings" title="No Archive Warnings Apply"></span></li>
              <li><span class="category-femslash category" title="F/F"></span></li>
              <li><span class="complete-yes iswip" title="Complete Work"></span></li>
            </ul>
            <!-- updated_at=1703529000 -->
            <p class="datetime">25 Dec 2023</p>
          </div>
          <ul class="series">
            <li>Part <strong>2</strong> of <a href="/series/987654">The Woods Sequence</a></li>
          </ul>
          <blockquote class="userstuff summary"><p>They meet in the woods.</p></blockquote>
          <ul class="tags commas">
            <li class="warnings"><strong><a class="tag" href="/tags/x/works">No Archive Warnings Apply</a></strong></li>
            <li class="relationships"><a class="tag" href="/tags/y/works">Clarke Griffin/Lexa</a></li>
            <li class="characters"><a class="tag" href="/tags/z/works">Clarke Griffin</a></li>
            <li class="characters"><a class="tag" href="/tags/w/works">Lexa</a></li>
            <li class="freeforms"><a class="tag" href="/tags/v/works">Fluff</a></li>
            <li class="freeforms"><a class="tag" href="/tags/u/works">Slow Burn</a></li>
          </ul>
          <dl class="stats">
            <dt class="language">Language:</dt><dd class="language" lang="en">English</dd>
            <dt class="words">Words:</dt><dd class="words">12,345</dd>
            <dt class="chapters">Chapters:</dt><dd class="chapters">5/5</dd>
            <dt class="collections">Collections:</dt><dd class="collections"><a href="/c">2</a></dd>
            <dt class="comments">Comments:</dt><dd class="comments"><a href="/c">234</a></dd>
            <dt class="kudos">Kudos:</dt><dd class="kudos"><a href="/k">6,789</a></dd>
            <dt class="bookmarks">Bookmarks:</dt><dd class="bookmarks"><a href="/b">1,011</a></dd>
            <dt class="hits">Hits:</dt><dd class="hits">45,678</dd>
          </dl>
        </li>
        """;

    /// <summary>
    /// One blurb with the interesting parts substitutable, so a test can vary a single field
    /// without restating the markup around it.
    /// </summary>
    private static string Blurb(
        string title = "A Study in Lexa",
        string category = "F/F",
        string warnings = "No Archive Warnings Apply",
        string isWip = "Complete Work",
        string chapters = "5/5",
        string byline = """<a rel="author" href="/users/someuser/pseuds/somepseud">somepseud</a>""",
        string? updatedAtComment = "1703529000",
        string datetime = "25 Dec 2023",
        bool restricted = false) => $"""
        <li id="work_12345678" class="work blurb group">
          <div class="header module">
            <h4 class="heading">
              <a href="/works/12345678">{title}</a>
              {(restricted ? """<img class="symbol non-image" title="Restricted" src="/lock.png">""" : "")}
              by {byline}
            </h4>
            <ul class="required-tags">
              <li><span class="rating-teen rating" title="Teen And Up Audiences"></span></li>
              <li><span class="warning-no warnings" title="{warnings}"></span></li>
              <li><span class="category-femslash category" title="{category}"></span></li>
              <li><span class="complete-yes iswip" title="{isWip}"></span></li>
            </ul>
            {(updatedAtComment is null ? "" : $"<!-- updated_at={updatedAtComment} -->")}
            <p class="datetime">{datetime}</p>
          </div>
          <dl class="stats">
            <dt class="chapters">Chapters:</dt><dd class="chapters">{chapters}</dd>
          </dl>
        </li>
        """;
}
