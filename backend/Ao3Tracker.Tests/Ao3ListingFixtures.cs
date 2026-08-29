using System.Net;
using Ao3Tracker.Api.Services.Scraping;

namespace Ao3Tracker.Tests;

/// <summary>
/// The AO3 listing markup these tests hand the parser, and the stub that serves it a page at a time.
///
/// Shared rather than private to one test class because there are now two walks over the same
/// listing to test — the incremental/backfill passes and the full sweep — and a second copy of the
/// blurb builder would be a second listing to keep in step with the parser.
/// </summary>
internal static class Ao3ListingFixtures
{
    internal const string Lexa = "Clarke Griffin/Lexa";

    internal static DateTime Jan(int day) => new(2023, 1, day, 12, 0, 0, DateTimeKind.Utc);

    internal static ScrapeHttpResponse Ok(string url, string html) =>
        new(html, HttpStatusCode.OK, FromCache: false, FinalUrl: url);

    /// <summary>
    /// Answers each request with the page its query string asks for. Page 1 carries no
    /// <c>page=</c> parameter, which is what the scraper actually sends.
    /// </summary>
    internal static Func<string, ScrapeHttpResponse> Pages(params FakePage[] pages) => url =>
    {
        var number = 1;
        var marker = url.IndexOf("page=", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var digits = new string([.. url[(marker + 5)..].TakeWhile(char.IsDigit)]);
            number = int.Parse(digits);
        }

        var page = pages.FirstOrDefault(p => p.Number == number);
        return page is null
            ? new ScrapeHttpResponse("", HttpStatusCode.NotFound, FromCache: false, FinalUrl: url)
            : Ok(url, page.Html);
    };

    internal sealed record FakePage(int Number, string Html);

    /// <summary>
    /// <see cref="Pages"/>, but every page that answers is served to a logged-in session — which is
    /// what a real scrape looks like, since <c>ScrapeWorker</c> holds every job until this instance
    /// has one. The full sweep is the only pass that reads that flag, so its tests are the only ones
    /// that have to say so.
    /// </summary>
    internal static Func<string, ScrapeHttpResponse> LoggedInPages(params FakePage[] pages)
    {
        var anonymous = Pages(pages);

        return url =>
        {
            var response = anonymous(url);
            return response.StatusCode == HttpStatusCode.OK
                ? response with { Authenticated = true }
                : response;
        };
    }

    /// <summary>
    /// A blurb the parser selects and then cannot name: <c>li.blurb</c> with a <c>work_</c> id
    /// carrying no number and no heading link to fall back to, which is what
    /// <see cref="Ao3Tracker.Api.Services.Scraping.Ao3BlurbParser"/> counts a parse warning for.
    /// A blurb with no <c>work_</c> id at all is never selected, so it produces no warning either.
    /// </summary>
    internal static string Nameless() => """<li id="work_" class="work blurb group"></li>""";

    internal static FakePage Page(int number, string[] blurbs, bool nextPage = false, int? total = null) =>
        new(number, $"""
            <div id="main">
              {(total is null ? "" : $"<h2 class='heading'>1 - 20 of {total} Works in {Lexa}</h2>")}
              <ol class="work index group">{string.Join('\n', blurbs)}</ol>
              {(nextPage ? """<ol class="pagination actions"><li><a href="?page=next">Next &rarr;</a></li></ol>""" : "")}
            </div>
            """);

    /// <summary>
    /// One blurb. <paramref name="undated"/> renders the shape AO3 has served on occasion and the
    /// parser reports as DateTime.MinValue: no <c>updated_at</c> comment, and a visible date in
    /// none of the formats it knows.
    /// </summary>
    internal static string Blurb(
        long id,
        DateTime? updatedAt = null,
        int kudos = 10,
        string[]? freeforms = null,
        bool undated = false,
        bool restricted = false)
    {
        var epoch = new DateTimeOffset(updatedAt ?? Jan(1)).ToUnixTimeSeconds();
        var tags = string.Join('\n', (freeforms ?? ["Fluff"])
            .Select(f => $"""<li class="freeforms"><a class="tag" href="/tags/{f}/works">{f}</a></li>"""));

        return $"""
            <li id="work_{id}" class="work blurb group">
              <div class="header module">
                <h4 class="heading">
                  {(restricted ? """<img class="symbol" title="Restricted" alt="Restricted" />""" : "")}
                  <a href="/works/{id}">Work {id}</a>
                  by <a rel="author" href="/users/someuser/pseuds/somepseud">somepseud (someuser)</a>
                </h4>
                <ul class="required-tags">
                  <li><span class="rating-teen rating" title="Teen And Up Audiences"></span></li>
                  <li><span class="warning-no warnings" title="No Archive Warnings Apply"></span></li>
                  <li><span class="category-femslash category" title="F/F"></span></li>
                  <li><span class="complete-yes iswip" title="Complete Work"></span></li>
                </ul>
                {(undated ? "" : $"<!-- updated_at={epoch} -->")}
                <p class="datetime">{(undated ? "some time ago" : "1 Jan 2023")}</p>
              </div>
              <ul class="tags commas">
                <li class="relationships"><a class="tag" href="/tags/lexa/works">{Lexa}</a></li>
                {tags}
              </ul>
              <dl class="stats">
                <dt class="words">Words:</dt><dd class="words">1,000</dd>
                <dt class="chapters">Chapters:</dt><dd class="chapters">1/1</dd>
                <dt class="kudos">Kudos:</dt><dd class="kudos">{kudos}</dd>
              </dl>
            </li>
            """;
    }
}
