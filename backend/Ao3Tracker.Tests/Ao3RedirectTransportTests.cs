using System.Net;
using Ao3Tracker.Api.Services.Credentials;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Tests;

/// <summary>
/// Where a redirect may take this instance's traffic, and what travels with it — over the real
/// <see cref="RateLimitedAo3HttpClient"/> and a handler standing in for AO3.
///
/// The handler's automatic following is off (Program.cs), because it copies a hand-set
/// <c>Cookie</c> header onto whatever request comes next, wherever the Location points — .NET
/// strips <c>Authorization</c> across a cross-origin redirect, but not a cookie set by hand. The
/// transport walks redirects itself instead, and these tests are what pin the walk: it exists,
/// it stays on the archive, and the session never leaves it.
/// </summary>
public class Ao3RedirectTransportTests : IDisposable
{
    private const string Url = "https://ao3.test/tags/Clarke%20Griffin*s*Lexa/works";
    private const string CanonicalUrl = "https://ao3.test/tags/Clexa/works";

    private readonly string _dataDirectory =
        Directory.CreateTempSubdirectory("ship-watcharr-redirects-").FullName;

    private readonly StubArchive _archive = new();
    private readonly StubSessionCache _sessions = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the test is untidy, never a failure.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Follows_a_redirect_that_stays_on_the_archive()
    {
        // The behaviour the handler's automatic following used to provide, now the transport's
        // own: a synonym tag redirecting to its canonical listing is still walked, and the caller
        // still recognises the synonym by where the request ended up.
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = request => request.RequestUri!.ToString() == CanonicalUrl
            ? Ok("<html></html>")
            : Redirect(CanonicalUrl);

        var response = await Client().GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CanonicalUrl, response.FinalUrl);

        // Both hops address the archive, so both carry the session.
        Assert.Equal(2, _archive.Received.Count);
        Assert.All(_archive.Received, sent => Assert.Equal("_otwarchive_session=abc123", sent.Cookie));
    }

    [Fact]
    public async Task Resolves_a_relative_location_against_the_page_that_answered()
    {
        _archive.Answers = request => request.RequestUri!.ToString() == CanonicalUrl
            ? Ok("<html></html>")
            : Redirect("/tags/Clexa/works");

        var response = await Client().GetAsync(Url);

        Assert.Equal(CanonicalUrl, response.FinalUrl);
    }

    [Fact]
    public async Task Refuses_to_follow_a_redirect_off_the_archive()
    {
        // The leak this walk exists to close: an AO3 response that 302s off-origin. Nothing is
        // sent there at all — not a cookie-less courtesy visit, and least of all the session — and
        // the 3xx comes back with its Location intact, for the caller to refuse with a reason.
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = _ => Redirect("https://elsewhere.test/tags/Clexa/works");

        var response = await Client().GetAsync(Url);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://elsewhere.test/tags/Clexa/works", response.Location);

        // Compared as a Uri renders it: ToString unescapes the %20 the constant carries.
        var sent = Assert.Single(_archive.Received);
        Assert.Equal(new Uri(Url).ToString(), sent.Url);
    }

    [Fact]
    public async Task A_scheme_downgrade_is_off_the_archive_too()
    {
        // Same host, http: a session followed there would go over the wire in the clear. Origin
        // means scheme, host and port together — Ao3Origin's rule, applied per hop.
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = _ => Redirect("http://ao3.test/tags/Clexa/works");

        var response = await Client().GetAsync(Url);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(_archive.Received);
    }

    [Fact]
    public async Task Sends_the_session_only_to_the_archive_on_the_first_hop_too()
    {
        // Defence in depth: no caller today fetches anything but archive URLs, but the rule is per
        // request rather than per chain, so a URL off the configured origin gets this instance's
        // identity and never its session.
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = _ => Ok("<html></html>");

        await Client().GetAsync("https://elsewhere.test/works/1");

        var sent = Assert.Single(_archive.Received);
        Assert.Null(sent.Cookie);
        Assert.Contains("ShipWatcharr", sent.UserAgent);
    }

    [Fact]
    public async Task Stops_walking_a_chain_that_never_ends()
    {
        // AO3's real chains are one hop. A listing that redirects to itself for ever must come
        // back as the 3xx it is, not hold the gate while the walk goes round.
        _archive.Answers = request => Redirect(request.RequestUri!.ToString());

        var response = await Client().GetAsync(Url);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        // The first request plus at most MaxRedirects follows.
        Assert.Equal(11, _archive.Received.Count);
    }

    [Fact]
    public async Task A_download_redirected_within_the_archive_is_followed_to_the_file()
    {
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = request => request.RequestUri!.AbsolutePath.EndsWith(".epub")
            ? File("EPUB bytes"u8.ToArray())
            : Redirect("https://ao3.test/downloads/1/work.epub");

        using var destination = new MemoryStream();
        var result = await Client().DownloadAsync("https://ao3.test/downloads/1", destination);

        Assert.True(result.IsSuccess);
        Assert.Equal("EPUB bytes"u8.ToArray(), destination.ToArray());
        Assert.Equal(2, _archive.Received.Count);
    }

    [Fact]
    public async Task A_download_redirected_off_the_archive_writes_nothing()
    {
        // The T72 reviewer's reproduction: an origin-checked download link whose response 302s
        // off-archive. The fetch must not follow — neither the session nor the bytes may come
        // from anywhere but the archive — and nothing may reach the disk.
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = _ => Redirect("https://elsewhere.test/work.epub");

        using var destination = new MemoryStream();
        var result = await Client().DownloadAsync("https://ao3.test/downloads/1/work.epub", destination);

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Found, result.StatusCode);
        Assert.Empty(destination.ToArray());
        Assert.Single(_archive.Received);
    }

    private static HttpResponseMessage Ok(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html"),
    };

    private static HttpResponseMessage File(byte[] body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
    };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private RateLimitedAo3HttpClient Client()
    {
        var options = Options.Create(new Ao3HttpClientOptions
        {
            BaseUrl = "https://ao3.test",
            MinDelayBetweenRequests = TimeSpan.Zero,
            MaxDelayBetweenRequests = TimeSpan.Zero,
        });

        var storagePaths = new StoragePaths(
            _dataDirectory,
            Path.Combine(_dataDirectory, "test.db"),
            Path.Combine(_dataDirectory, "settings.json"),
            Path.Combine(_dataDirectory, "keys"));

        // The real User-Agent provider over a stub contact, as the other transport tests do: what
        // goes in the header is decided by that provider's rules rather than by this test.
        var userAgents = new Ao3UserAgentProvider(
            options,
            InstanceIdentity.LoadOrCreate(storagePaths),
            new StubContacts(() => "emma@example.com"));

        return new RateLimitedAo3HttpClient(
            new HttpClient(_archive),
            new Ao3LoginHttpClient(new HttpClient(new StubArchive())),
            new MemoryCache(new MemoryCacheOptions()),
            options,
            userAgents,
            _sessions,
            TimeProvider.System,
            NullLogger<RateLimitedAo3HttpClient>.Instance);
    }
}
