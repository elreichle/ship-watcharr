using System.Net;

namespace Ao3Tracker.Api.Services.Scraping;

/// <param name="Authenticated">
/// Whether the content in front of us was rendered for the instance's AO3 account — read off the
/// page itself, not off "we attached a cookie", because AO3 answers a dead session with a 200 and
/// the anonymous view rather than a 401. It travels with the content rather than with the run
/// because that is what it describes: a page served from cache is the page the *caching* request
/// was given, so a logged-in run reading a cached anonymous copy is reading anonymous content and
/// must say so.
///
/// False whenever the page carries no evidence either way — a 404, a file body, an error page.
/// "Not proven logged in" is the safe direction for every consumer of this flag.
/// </param>
/// <param name="FinalUrl">
/// Where the request ended up after redirects, which is not always where it was sent. AO3 answers
/// a synonym tag by redirecting to its canonical one, so this is the only thing in the response
/// that says "the tag you asked for is really called something else" — the body of a synonym's
/// works index is otherwise indistinguishable from the canonical tag's. Null if the transport
/// didn't report one.
/// </param>
/// <param name="Location">
/// The <c>Location</c> header, when the response carried one and nothing followed it. Only the
/// login POST asks for a transport that does not follow redirects, and this is what tells it
/// whether AO3 sent the login somewhere or straight back to the form.
/// </param>
/// <param name="SetCookieHeaders">
/// Every <c>Set-Cookie</c> the response carried, verbatim and unparsed. Empty for ordinary scraping
/// requests, which have no use for them; the login flow is the only caller that reads this, because
/// establishing a session is exactly "keep what the response set".
/// </param>
public record ScrapeHttpResponse(
    string Content,
    HttpStatusCode StatusCode,
    bool FromCache,
    string? FinalUrl = null,
    bool Authenticated = false,
    IReadOnlyList<string>? SetCookieHeaders = null,
    string? Location = null)
{
    public IReadOnlyList<string> SetCookies => SetCookieHeaders ?? [];
}

/// <summary>
/// What a fetch of a file rather than a page came back with. The bytes are not here: they went to
/// the stream the caller supplied, so that nothing buffers a whole download in memory on the way to
/// disk.
/// </summary>
/// <param name="BytesWritten">
/// How much reached the destination stream. Zero on any non-OK status — a body AO3 sent to explain
/// an error is not a copy of the work, and writing it would leave an error page on disk under a
/// name saying it was an EPUB.
/// </param>
/// <param name="ExceededSizeLimit">
/// True when the response ran past <see cref="Ao3HttpClientOptions.MaxDownloadBytes"/> and the copy
/// was abandoned. Reported rather than thrown because it is an outcome the caller records against
/// one request, the same as a 404 — and because whatever was written is not a whole file, which is
/// exactly what the caller needs to know before naming it one.
/// </param>
public record ScrapeDownloadResponse(
    HttpStatusCode StatusCode,
    long BytesWritten,
    string? FinalUrl = null,
    bool ExceededSizeLimit = false)
{
    public bool IsSuccess => StatusCode == HttpStatusCode.OK && !ExceededSizeLimit;
}

/// <summary>
/// Shared HTTP entry point for every scraper. Enforces a minimum delay between requests,
/// retries with backoff on throttling/server errors, and serves unchanged pages from cache
/// instead of re-fetching them. Scrapers must not construct their own HttpClient — going
/// through this wrapper is what keeps the whole scraping subsystem rate-limited.
/// </summary>
public interface IRateLimitedHttpClient
{
    /// <summary>
    /// Fetches a page as the instance's AO3 account when a session is cached, and anonymously when
    /// one is not. Every scraper uses this and nothing else.
    /// </summary>
    Task<ScrapeHttpResponse> GetAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// A GET that deliberately carries no stored session and is never cached — the login flow's
    /// only GET.
    ///
    /// Both halves matter. Asking for the login page as an already-logged-in session gets a
    /// redirect rather than a form, and caching an authenticity token would hand the next login a
    /// token Rails has already retired.
    /// </summary>
    Task<ScrapeHttpResponse> GetLoggedOutAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Posts a form, carrying the cookie it is given rather than the stored session, and following
    /// no redirect — a successful login answers with one, and the session it sets is on that
    /// response rather than on wherever it points.
    ///
    /// The login POST is the <em>only</em> write this software makes to AO3. Kudos, comments,
    /// bookmarks, subscriptions and posting are non-goals in the spec, not merely unimplemented:
    /// the session exists so that logged-in-only pages can be read, and for nothing else.
    /// </summary>
    Task<ScrapeHttpResponse> PostFormAsync(
        string url,
        IReadOnlyDictionary<string, string> fields,
        string? cookieHeader,
        CancellationToken ct = default);

    /// <summary>
    /// Fetches a file into <paramref name="destination"/>, as the instance's AO3 account when a
    /// session is cached.
    /// </summary>
    /// <remarks>
    /// Downloads go through this rather than a client of their own for the reason every other
    /// request does: this is where the global 5-8s spacing lives, and a second way out of it would
    /// be a second load this instance puts on AO3 without saying so. A download and a scrape
    /// therefore queue behind one another, by design.
    ///
    /// Streamed rather than returned, because the caller is writing to disk and a
    /// <see cref="ScrapeHttpResponse"/> would mean holding an entire EPUB as a string first.
    /// Nothing is cached either — the cache exists to stop a page being re-read within fifteen
    /// minutes, and the same file's bytes are already de-duplicated by the row that names them.
    /// </remarks>
    Task<ScrapeDownloadResponse> DownloadAsync(
        string url, Stream destination, CancellationToken ct = default);
}
