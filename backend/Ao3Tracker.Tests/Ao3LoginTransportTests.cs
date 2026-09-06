using System.Net;
using System.Text;
using Ao3Tracker.Api.Services.Credentials;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Tests;

/// <summary>
/// What the shared HTTP client actually puts on the wire once the instance has a session, over the
/// real <see cref="RateLimitedAo3HttpClient"/> and a handler standing in for AO3.
///
/// The fake used by the rest of the suite implements the interface, so it cannot answer any of the
/// questions here: whether a <c>Cookie</c> header is really sent, whether <c>Set-Cookie</c> really
/// survives back out, whether the login POST is really form-encoded. Those are decided by the
/// transport, so they are tested against the transport.
/// </summary>
public class Ao3LoginTransportTests : IDisposable
{
    private const string Url = "https://ao3.test/tags/Clarke%20Griffin*s*Lexa/works";

    private readonly string _dataDirectory =
        Directory.CreateTempSubdirectory("ship-watcharr-transport-").FullName;

    private readonly StubArchive _archive = new();

    /// <summary>
    /// A second stub behind the second transport. Separate rather than shared because the two
    /// clients differ only in handler configuration — which a stub handler replaces outright, and so
    /// cannot demonstrate. Keeping them apart is what makes "the login went through the transport
    /// configured not to follow redirects" an observable fact rather than an assumption.
    /// </summary>
    private readonly StubArchive _loginArchive = new();

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

    // ---- attaching the session -----------------------------------------------------------------

    [Fact]
    public async Task Attaches_the_cached_session_to_a_scraping_request()
    {
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);

        await Client().GetAsync(Url);

        Assert.Equal("_otwarchive_session=abc123", Assert.Single(_archive.Received).Cookie);
    }

    [Fact]
    public async Task Sends_no_cookie_at_all_when_nothing_is_cached()
    {
        await Client().GetAsync(Url);

        Assert.Null(Assert.Single(_archive.Received).Cookie);
    }

    [Fact]
    public async Task Sends_no_cookie_when_the_cached_session_has_run_out()
    {
        // The cache is what applies the expiry; this pins that the client asks it rather than
        // reading the row itself.
        _sessions.Session = null;

        await Client().GetAsync(Url);

        Assert.Null(Assert.Single(_archive.Received).Cookie);
    }

    [Fact]
    public async Task Identifies_the_instance_honestly_on_every_request_including_the_login()
    {
        _loginArchive.Answers = _ => Redirect("https://ao3.test/users/shipwatcharr");

        await Client().PostFormAsync("https://ao3.test/users/login", Fields(), cookieHeader: null);

        var userAgent = Assert.Single(_loginArchive.Received).UserAgent;
        Assert.NotNull(userAgent);
        Assert.Contains("ShipWatcharr", userAgent);
        Assert.Contains("emma@example.com", userAgent);
    }

    // ---- reading the session back off the page -------------------------------------------------

    [Fact]
    public async Task Reports_a_page_AO3_served_to_the_session_as_authenticated()
    {
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = _ => Ok(Fixtures.Load(Fixtures.EmptyListing));

        var response = await Client().GetAsync(Url);

        Assert.True(response.Authenticated);
    }

    [Fact]
    public async Task Reports_a_page_that_proves_nothing_as_not_authenticated()
    {
        // "Not proven logged in" is the safe direction: a full sweep reading this flag must never
        // be told a total was the authenticated one on the strength of a page with no header at all.
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = _ => Ok("<html><body>Not Found</body></html>");

        Assert.False((await Client().GetAsync(Url)).Authenticated);
    }

    [Fact]
    public async Task Reports_an_anonymous_request_as_not_authenticated()
    {
        _archive.Answers = _ => Ok(Fixtures.Load(Fixtures.EmptyListing));

        Assert.False((await Client().GetAsync(Url)).Authenticated);
    }

    [Fact]
    public async Task Discards_a_session_AO3_has_stopped_honouring()
    {
        // The whole expiry mechanism. AO3 answers a dead session with a 200 and the anonymous view,
        // so nothing below this notices, and the row would otherwise sit there for ever while every
        // restricted work quietly went missing.
        _sessions.Session = new Ao3Session("_otwarchive_session=expired", DateTime.UtcNow, null);
        _archive.Answers = _ => Ok(Fixtures.Load(Fixtures.LoginPage));

        var response = await Client().GetAsync(Url);

        Assert.False(response.Authenticated);
        Assert.True(_sessions.Discarded);
    }

    [Fact]
    public async Task Keeps_a_session_when_the_page_says_nothing_about_it()
    {
        // A 404 is not evidence. Discarding on one would re-login after every miss.
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = _ => Ok("<html><body>Not Found</body></html>");

        await Client().GetAsync(Url);

        Assert.False(_sessions.Discarded);
    }

    [Fact]
    public async Task Does_not_go_looking_for_a_session_it_never_sent()
    {
        // An anonymous instance reading the logged-out view is not a session that expired, and
        // clearing a row that does not exist would log a warning on every page of every run.
        _archive.Answers = _ => Ok(Fixtures.Load(Fixtures.LoginPage));

        await Client().GetAsync(Url);

        Assert.False(_sessions.Discarded);
    }

    [Fact]
    public async Task Does_not_carry_a_scraped_pages_cookies_back_out()
    {
        // Nothing reads them, and the response is cached process-wide for fifteen minutes. Carrying
        // cookie material through that cache for no purpose is the sort of thing that is only ever
        // noticed by whatever eventually goes looking for it.
        _archive.Answers = _ => WithCookie(Ok("<html><body>page</body></html>"), "_otwarchive_session=abc");

        var response = await Client().GetAsync(Url);

        Assert.Empty(response.SetCookies);
    }

    // ---- the cache ------------------------------------------------------------------------------

    [Fact]
    public async Task Does_not_serve_an_anonymous_page_to_a_logged_in_request()
    {
        // AO3 hides restricted works from nobody-in-particular, so the two views of one URL are
        // different pages. Sharing a cache entry would hand a logged-in run the anonymous copy for
        // the whole cache window.
        var client = Client();
        _archive.Answers = _ => Ok("<html><body>anonymous</body></html>");
        await client.GetAsync(Url);

        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = _ => Ok("<html><body>logged in</body></html>");
        var response = await client.GetAsync(Url);

        Assert.False(response.FromCache);
        Assert.Contains("logged in", response.Content);
    }

    [Fact]
    public async Task Does_not_cache_the_page_that_proved_the_session_was_dead()
    {
        // The session lapses, this page comes back anonymous, and the cookie is discarded — but the
        // request still carried one, so the response would land under the `session:` key. The next
        // poll logs in again, computes that same key, and finds the dead session's anonymous copy
        // waiting: fifteen minutes of reading the logged-out archive immediately after
        // re-authenticating to avoid precisely that.
        var client = Client();
        _sessions.Session = new Ao3Session("_otwarchive_session=expired", DateTime.UtcNow, null);
        _archive.Answers = _ => Ok(Fixtures.Load(Fixtures.LoginPage));
        await client.GetAsync(Url);

        _sessions.Session = new Ao3Session("_otwarchive_session=fresh", DateTime.UtcNow, null);
        _archive.Answers = _ => Ok(Fixtures.Load(Fixtures.EmptyListing));
        var response = await client.GetAsync(Url);

        Assert.False(response.FromCache);
        Assert.True(response.Authenticated);
    }

    [Fact]
    public async Task Still_serves_a_repeat_of_the_same_request_from_cache()
    {
        // The other side of it: splitting the key must not defeat the cache for the case it exists
        // for, which is a run re-reading a page it just read.
        var client = Client();
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = _ => Ok("<html><body>page</body></html>");

        await client.GetAsync(Url);
        var response = await client.GetAsync(Url);

        Assert.True(response.FromCache);
        Assert.Single(_archive.Received);
    }

    [Fact]
    public async Task Never_caches_the_login_page()
    {
        // An authenticity token is single-use. Serving a cached one would hand the next login a
        // token Rails has already retired, which reads exactly like a wrong password.
        var client = Client();
        _archive.Answers = _ => Ok(Fixtures.Load(Fixtures.LoginPage));

        await client.GetLoggedOutAsync("https://ao3.test/users/login");
        await client.GetLoggedOutAsync("https://ao3.test/users/login");

        Assert.Equal(2, _archive.Received.Count);
    }

    [Fact]
    public async Task Sends_no_session_on_the_login_page_fetch_even_when_one_is_cached()
    {
        _sessions.Session = new Ao3Session("_otwarchive_session=abc123", DateTime.UtcNow, null);
        _archive.Answers = _ => Ok(Fixtures.Load(Fixtures.LoginPage));

        await Client().GetLoggedOutAsync("https://ao3.test/users/login");

        Assert.Null(Assert.Single(_archive.Received).Cookie);
    }

    // ---- the login POST ------------------------------------------------------------------------

    [Fact]
    public async Task Posts_the_login_through_the_transport_that_does_not_follow_redirects()
    {
        // The scraping client follows redirects, because that is how a synonym tag is recognised.
        // Posting the login through it would follow the 302 a successful login answers with, and the
        // Set-Cookie riding on that response — the session itself — would never reach the caller.
        _loginArchive.Answers = _ => Redirect("https://ao3.test/users/shipwatcharr");

        await Client().PostFormAsync("https://ao3.test/users/login", Fields(), cookieHeader: null);

        Assert.Single(_loginArchive.Received);
        Assert.Empty(_archive.Received);
    }

    [Fact]
    public async Task Posts_the_form_url_encoded()
    {
        _loginArchive.Answers = _ => Redirect("https://ao3.test/users/shipwatcharr");

        await Client().PostFormAsync("https://ao3.test/users/login", Fields(), cookieHeader: null);

        var sent = Assert.Single(_loginArchive.Received);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("application/x-www-form-urlencoded", sent.ContentType);
        Assert.Contains("user%5Blogin%5D=shipwatcharr", sent.Body);
    }

    [Fact]
    public async Task Posts_with_the_cookie_it_was_given_rather_than_the_cached_session()
    {
        // The login POST has to carry the cookies the *form* arrived with, because Rails ties the
        // authenticity token to them. A stale cached session here would fail every login.
        _sessions.Session = new Ao3Session("_otwarchive_session=stale", DateTime.UtcNow, null);
        _loginArchive.Answers = _ => Redirect("https://ao3.test/users/shipwatcharr");

        await Client().PostFormAsync(
            "https://ao3.test/users/login", Fields(), cookieHeader: "_otwarchive_session=from-the-form");

        Assert.Equal("_otwarchive_session=from-the-form", Assert.Single(_loginArchive.Received).Cookie);
    }

    [Fact]
    public async Task Hands_back_the_cookies_the_login_response_set()
    {
        _loginArchive.Answers = _ => Redirect(
            "https://ao3.test/users/shipwatcharr", "_otwarchive_session=logged-in; path=/; HttpOnly");

        var response = await Client()
            .PostFormAsync("https://ao3.test/users/login", Fields(), cookieHeader: null);

        Assert.Contains(response.SetCookies, header => header.StartsWith(
            "_otwarchive_session=logged-in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Does_not_follow_the_redirect_a_login_answers_with()
    {
        // Nothing in this codebase may follow it either: the 302 and its Location are what tell a
        // refused login from an accepted one. (The handler-level AllowAutoRedirect=false that backs
        // this up is configured in Program.cs, and a stub handler stands in for exactly the
        // component that would honour it — hence the sibling test above, which pins that the login
        // goes through that transport at all.)
        _loginArchive.Answers = _ => Redirect(
            "https://ao3.test/users/shipwatcharr", "_otwarchive_session=logged-in; path=/");

        var response = await Client()
            .PostFormAsync("https://ao3.test/users/login", Fields(), cookieHeader: null);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://ao3.test/users/shipwatcharr", response.Location);
        Assert.Single(_loginArchive.Received);
    }

    // ---- the harness ----------------------------------------------------------------------------

    private static Dictionary<string, string> Fields() => new()
    {
        ["authenticity_token"] = "token",
        ["user[login]"] = "shipwatcharr",
        ["user[password]"] = "hunter2",
    };

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html"),
    };

    private static HttpResponseMessage WithCookie(HttpResponseMessage response, string setCookie)
    {
        response.Headers.TryAddWithoutValidation("Set-Cookie", setCookie);
        return response;
    }

    private static HttpResponseMessage Redirect(string location, params string[] setCookies)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found)
        {
            Content = new StringContent("", Encoding.UTF8, "text/html"),
            Headers = { Location = new Uri(location) },
        };

        foreach (var cookie in setCookies) response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
        return response;
    }

    /// <summary>
    /// The real client, spaced at zero. The rate gate's arithmetic has its own tests; making a
    /// transport test wait out five seconds a request would only prove the clock works.
    /// </summary>
    private RateLimitedAo3HttpClient Client()
    {
        var options = Options.Create(new Ao3HttpClientOptions
        {
            BaseUrl = "https://ao3.test",
            MinDelayBetweenRequests = TimeSpan.Zero,
            MaxDelayBetweenRequests = TimeSpan.Zero,
        });

        // The real User-Agent provider over a stub contact: what goes in the header is decided by
        // that provider's rules, and stubbing it would let this pass on a header the app refuses
        // to send.
        var storagePaths = new StoragePaths(
            _dataDirectory,
            Path.Combine(_dataDirectory, "test.db"),
            Path.Combine(_dataDirectory, "settings.json"),
            Path.Combine(_dataDirectory, "keys"));

        var userAgents = new Ao3UserAgentProvider(
            options,
            InstanceIdentity.LoadOrCreate(storagePaths),
            new StubContacts(() => "emma@example.com"));

        return new RateLimitedAo3HttpClient(
            new Ao3RateGate(options, TimeProvider.System, NullLogger<Ao3RateGate>.Instance),
            new HttpClient(_archive),
            new Ao3LoginHttpClient(new HttpClient(_loginArchive)),
            new MemoryCache(new MemoryCacheOptions()),
            options,
            userAgents,
            _sessions,
            TimeProvider.System,
            NullLogger<RateLimitedAo3HttpClient>.Instance);
    }
}

/// <summary>
/// A session held in memory, so a transport test can say "there is one" without a database. The
/// interface is two methods precisely so that this is all a stub of it has to be.
/// </summary>
internal sealed class StubSessionCache : IAo3SessionCache
{
    public Ao3Session? Session { get; set; }

    /// <summary>Whether the client concluded the session had stopped working.</summary>
    public bool Discarded { get; private set; }

    public Task<Ao3Session?> GetUsableAsync(CancellationToken ct = default) => Task.FromResult(Session);

    public Task DiscardAsync(CancellationToken ct = default)
    {
        Discarded = true;
        Session = null;
        return Task.CompletedTask;
    }
}

/// <param name="Cookie">The <c>Cookie</c> header as it was actually sent, or null if there was none.</param>
internal sealed record SentRequest(
    HttpMethod Method, string Url, string? Cookie, string? UserAgent, string? ContentType, string Body);

/// <summary>
/// AO3, as far as an <see cref="HttpClient"/> can tell. Records what was sent — headers and body,
/// not just the URL — because that is the half a fake of the interface above it cannot see.
/// </summary>
internal sealed class StubArchive : HttpMessageHandler
{
    public List<SentRequest> Received { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Answers { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("", Encoding.UTF8, "text/html"),
        };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

        Received.Add(new SentRequest(
            request.Method,
            request.RequestUri?.ToString() ?? "",
            request.Headers.TryGetValues("Cookie", out var cookies) ? string.Join("; ", cookies) : null,
            request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
            request.Content?.Headers.ContentType?.MediaType,
            body));

        var response = Answers(request);
        response.RequestMessage = request;
        return response;
    }
}
