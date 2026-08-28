using System.Net;
using System.Security.Claims;
using Ao3Tracker.Api.Controllers;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Credentials;
using Ao3Tracker.Api.Services.Downloads;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Settings;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Tests;

/// <summary>
/// Shared fixture for the two library endpoints, over a real SQLite database rather than a mocked
/// context. The queries under test are the point: watched-ship scoping, paging with a tie-break,
/// and the unique index that decides a race between two users naming the same tag are all things a
/// mock would happily agree with while the database disagreed.
/// </summary>
internal sealed class LibraryTestHost : IDisposable
{
    // Kept open for the fixture's lifetime: an in-memory SQLite database exists only as long as a
    // connection to it does.
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _request;

    /// <summary>Scopes handed out one per simulated request; disposed with the fixture.</summary>
    private readonly List<IServiceScope> _perRequestScopes = [];

    private readonly string _dataDirectory;

    /// <summary>
    /// AO3, faked. Every outbound request in these tests goes through it, so nothing here can
    /// reach the real archive by accident.
    /// </summary>
    public FakeAo3Http Http { get; } = new();

    /// <summary>
    /// What the stubbed resolver reports as this instance's operator contact. Null is the fresh-
    /// install state: no contact, so no request may be made at all.
    /// </summary>
    public string? OperatorContact { get; set; } = "ops@example.com";

    /// <summary>
    /// The signal a request uses to wake the scrape worker. There is no worker running in these
    /// tests, so a pending wake stays pending — which is what lets a test assert one was sent.
    /// </summary>
    public ScrapeWakeSignal ScrapeWake { get; } = new();

    /// <summary>
    /// The signal a download request uses to wake the download worker. Its own rather than the
    /// scraper's, which is the thing a test asserting "asking for a file did not start a scrape"
    /// depends on.
    /// </summary>
    public DownloadWakeSignal DownloadWake { get; } = new();

    /// <summary>
    /// The clock every service in this fixture reads, registered as the container's TimeProvider.
    /// Settable so a test can tell a timestamp the app wrote through its injected clock apart from
    /// one it wrote by calling DateTime.UtcNow directly — which is a difference nothing else here
    /// can see.
    /// </summary>
    public FixedClock Clock { get; } = new();

    /// <summary>
    /// The cooldown after a refused AO3 login, shared with the app the way the real singleton is —
    /// so a test can watch a second poll decline to try again, and watch saving a credential undo
    /// that.
    /// </summary>
    public Ao3LoginBackoff LoginBackoff { get; } = new();

    public LibraryTestHost(params IAo3Scraper[] scrapers) : this(null, scrapers) { }

    /// <summary>
    /// As above, with a last word on the container. For a test that needs a service this fixture
    /// does not register — in particular a *scoped* one, which the <c>scrapers</c> parameter cannot
    /// express because it registers the instances it is handed as singletons.
    /// </summary>
    public LibraryTestHost(Action<IServiceCollection>? configure, params IAo3Scraper[] scrapers)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dataDirectory = Directory.CreateTempSubdirectory("shipwatcharr-tests-").FullName;

        var services = new ServiceCollection();
        services.AddLogging();

        // Mirrors Program.cs: the abstract AppDbContext is the service type and the concrete
        // SqliteAppDbContext the implementation, so the tests exercise the composition the app uses.
        services.AddDbContext<AppDbContext, SqliteAppDbContext>(o => o.UseSqlite(_connection));

        // Supplies the real ProblemDetailsFactory behind ControllerBase.ValidationProblem, so the
        // 400 bodies asserted below are the ones the API actually returns.
        services.AddMvcCore();

        foreach (var scraper in scrapers) services.AddSingleton(scraper);
        services.AddScoped<ScraperRegistry>();

        // The real Ao3UserAgentProvider over a stubbed contact resolver, rather than a stub of the
        // provider itself: "can this instance talk to AO3 at all" is decided by that provider's own
        // validation rules, and a stub would let a test pass on a contact the app would reject.
        services.Configure<Ao3HttpClientOptions>(o => o.BaseUrl = BaseUrl);
        var storagePaths = new StoragePaths(
            _dataDirectory,
            Path.Combine(_dataDirectory, "test.db"),
            Path.Combine(_dataDirectory, "settings.json"),
            Path.Combine(_dataDirectory, "keys"));
        services.AddSingleton(InstanceIdentity.LoadOrCreate(storagePaths));

        // settings.json under the fixture's temp directory, so the admin endpoints that read and
        // write it do so for real without touching the dev instance's data directory.
        services.AddSingleton(storagePaths);
        services.AddScoped<IPersistedSettingsStore, PersistedSettingsStore>();

        // A real UserManager over the same database, because the admin endpoints decide access by
        // reading the caller's row back through it — a stub would be asserting the test's own idea
        // of who is an admin rather than the app's.
        services.AddIdentityCore<ApplicationUser>().AddEntityFrameworkStores<AppDbContext>();

        // Real Data Protection over keys in this fixture's temp directory, the way Program.cs
        // configures it: the credential store's whole job is encryption at rest, so a fake
        // protector would leave the interesting failure — ciphertext that cannot be read back —
        // untested.
        services.AddDataProtection()
            .SetApplicationName("Ao3Tracker")
            .PersistKeysToFileSystem(new DirectoryInfo(storagePaths.KeysDirectory));
        services.AddScoped<IAo3InstanceCredentialStore, Ao3InstanceCredentialStore>();

        // The real gate, over the real contact resolver and the real credential store: what these
        // tests are about is which of the two missing things holds a job, and a stubbed gate would
        // only ever assert the worker asked something.
        services.AddScoped<ScrapingGate>();
        services.AddSingleton<IOperatorContactResolver>(new StubContacts(() => OperatorContact));
        services.AddScoped<Ao3UserAgentProvider>();

        services.AddSingleton<IRateLimitedHttpClient>(Http);

        // The real login stack over the fake transport, rather than a stub provider: whether a due
        // job runs now depends on an actual round trip through the actual login parser, so a stub
        // would assert only that the worker asked something.
        services.AddSingleton<IAo3SessionCache, Ao3SessionCache>();
        services.AddScoped<IAo3SessionEstablisher, Ao3SessionEstablisher>();
        services.AddSingleton(LoginBackoff);
        services.AddScoped<IAo3SessionProvider, Ao3SessionProvider>();

        services.AddScoped<IShipVerifier, Ao3ShipVerifier>();
        services.AddSingleton(ScrapeWake);
        services.AddSingleton(DownloadWake);

        // The real fetcher over the same database and the same fake archive: what these tests are
        // about is which rows and which files a drain leaves behind.
        services.AddScoped<IDownloadFetcher, DownloadFetcher>();

        // The real ingestor and the real ship-index scraper, over the same in-memory database as
        // everything else here: what these tests are about is which rows a walk leaves behind, and a
        // stubbed ingestor would assert only that the scraper called something.
        services.AddSingleton<TimeProvider>(Clock);
        services.AddScoped<IWorkIngestor, WorkIngestor>();
        services.AddScoped<Ao3ShipIndexScraper>();

        configure?.Invoke(services);

        _provider = services.BuildServiceProvider();

        using (var setup = _provider.CreateScope())
            setup.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

        // One scope stands in for "the request under test". Seeding uses its own context so this
        // one starts with a cold change tracker, the way a real request does.
        _request = _provider.CreateScope();
    }

    /// <summary>The archive root the verifier builds tag URLs against.</summary>
    public const string BaseUrl = "https://ao3.test";

    public static string TagUrl(string segment) => $"{BaseUrl}/tags/{segment}/works";

    public void Dispose()
    {
        foreach (var scope in _perRequestScopes) scope.Dispose();
        _request.Dispose();
        _provider.Dispose();
        _connection.Dispose();

        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>
    /// Runs a verification the way the worker does — in a scope of its own. A merge deletes one of
    /// the ships involved, so a verifier sharing the controller's context would leave that context
    /// tracking a row that no longer exists.
    /// </summary>
    public async Task<ShipVerificationResult> VerifyAsync(int shipId)
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IShipVerifier>().VerifyAsync(shipId);
    }

    /// <summary>
    /// One tick of the background worker, without a host or a timer. Used where the behaviour under
    /// test belongs to the worker rather than the verifier — which ships it picks, and the
    /// operator-contact gate it applies before touching any of them.
    /// </summary>
    public Task RunVerificationTickAsync() => new ShipVerificationWorker(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ILogger<ShipVerificationWorker>>())
        .VerifyDueShipsAsync(CancellationToken.None);

    /// <summary>
    /// One poll of the scrape worker, without a host or a timer — the same call its loop makes.
    /// Returned rather than constructed per call so a test can tick the *same* worker twice, which
    /// is what "the next poll picks it up, no restart" means.
    /// </summary>
    public ScrapeWorker NewScrapeWorker() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        _provider.GetRequiredService<ILogger<ScrapeWorker>>(),
        _provider.GetRequiredService<IOptions<Ao3HttpClientOptions>>(),
        ScrapeWake);

    /// <summary>
    /// One poll of the download worker, without a host or a timer — the same call its loop makes.
    /// Returned rather than constructed per call so a test can drain twice through the same worker,
    /// which is what "the next poll picks up what the last one left queued" means.
    /// </summary>
    public DownloadWorker NewDownloadWorker() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        _provider.GetRequiredService<ILogger<DownloadWorker>>(),
        _provider.GetRequiredService<IOptions<Ao3HttpClientOptions>>(),
        DownloadWake,
        _provider.GetRequiredService<StoragePaths>());

    /// <summary>Where this fixture's instance keeps everything it persists, downloads included.</summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>Stores an instance AO3 login, the way the admin endpoint does.</summary>
    public async Task SaveAo3LoginAsync(string username = "shipwatcharr", string password = "hunter2")
    {
        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAo3InstanceCredentialStore>()
            .SetCredentialAsync(username, password);
    }

    /// <summary>
    /// Runs <paramref name="work"/> inside one request-shaped scope, so a test can hold that scope's
    /// own <c>AppDbContext</c> and watch what a service reached from inside it does to it.
    /// </summary>
    public async Task WithScopeAsync(Func<IServiceProvider, Task> work)
    {
        using var scope = _provider.CreateScope();
        await work(scope.ServiceProvider);
    }

    /// <summary>One AO3 login round trip, in a scope of its own the way the worker performs it.</summary>
    public async Task<Ao3LoginResult> LogInToAo3Async()
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAo3SessionEstablisher>().LogInAsync();
    }

    /// <summary>"Make sure there is a session", which is what the worker actually calls.</summary>
    public async Task<Ao3LoginResult> EnsureAo3SessionAsync()
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAo3SessionProvider>().EnsureSessionAsync();
    }

    /// <summary>The gate as the worker reads it, in a scope of its own.</summary>
    public async Task<ScrapingGateState> EvaluateScrapingGateAsync()
    {
        using var scope = _provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ScrapingGate>().EvaluateAsync();
    }

    /// <summary>
    /// One scrape of a ship, in a scope of its own the way the worker runs them. The budget is
    /// overridable so a test can starve a walk without waiting out five hundred fake requests.
    /// </summary>
    public async Task<ScrapeOutcome> ScrapeAsync(
        int shipId, ScrapeRunMode mode = ScrapeRunMode.Incremental, ScrapeBudget? budget = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ship = await db.Ships.FirstAsync(s => s.Id == shipId);
        var job = await db.ScrapeJobs.FirstAsync(j => j.ShipId == shipId);

        return await scope.ServiceProvider.GetRequiredService<Ao3ShipIndexScraper>().ExecuteAsync(
            new ScrapeContext(job, ship, mode, budget ?? new ScrapeBudget(new Ao3HttpClientOptions())));
    }

    /// <summary>
    /// One queued download, fetched in a scope of its own the way the worker fetches them — but
    /// without the worker's own "only Pending rows" query in front of it. That is the point: the
    /// fetcher decides what it may claim, and a test that could only reach it through the worker
    /// would be asserting the query rather than the rule.
    /// </summary>
    public async Task<DownloadFetchOutcome> FetchDownloadAsync(int downloadId, ScrapeBudget? budget = null)
    {
        using var scope = _provider.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IDownloadFetcher>()
            .FetchAsync(downloadId, budget ?? new ScrapeBudget(new Ao3HttpClientOptions()));
    }

    /// <summary>
    /// Parses a listing and writes it, without a scrape around it. Lets a test exercise what
    /// re-reading a work does to its rows, which the scraper's own stopping rules would otherwise
    /// prevent it from ever reaching twice.
    /// </summary>
    public async Task<IngestResult> IngestAsync(int shipId, string html)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ship = await db.Ships.FirstAsync(s => s.Id == shipId);
        var page = Ao3BlurbParser.ParseListing(html);

        return await scope.ServiceProvider.GetRequiredService<IWorkIngestor>().IngestAsync(ship, page.Works);
    }

    /// <summary>A context of its own, so persistence assertions are real round-trips.</summary>
    public AppDbContext NewContext() => new SqliteAppDbContext(
        new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite(_connection).Options);

    public ApplicationUser SeedUser(string userName = "emma", bool isAdmin = false)
    {
        using var db = NewContext();

        var user = new ApplicationUser
        {
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            IsAdmin = isAdmin,
        };
        db.Users.Add(user);
        db.SaveChanges();

        return user;
    }

    public ShipsController Ships(ApplicationUser user) => Build(new ShipsController(
        _request.ServiceProvider.GetRequiredService<AppDbContext>(),
        _request.ServiceProvider.GetRequiredService<ScraperRegistry>(),
        _request.ServiceProvider.GetRequiredService<ScrapingGate>(),
        _request.ServiceProvider.GetRequiredService<ScrapeWakeSignal>()), user);

    public WorksController Works(ApplicationUser user) => Build(new WorksController(
        _request.ServiceProvider.GetRequiredService<AppDbContext>()), user);

    /// <summary>
    /// A works controller on a scope of its own, for the per-user state endpoints: those tests
    /// write through one "request" and read back through the next, and a shared context would
    /// answer the second out of the change tracker the first warmed. <see cref="Works"/> keeps the
    /// shared scope, which is all the read-only list tests need.
    /// </summary>
    public WorksController NewWorksRequest(ApplicationUser user)
    {
        var scope = _provider.CreateScope();
        _perRequestScopes.Add(scope);

        return Build(
            new WorksController(scope.ServiceProvider.GetRequiredService<AppDbContext>()),
            user,
            scope.ServiceProvider);
    }

    /// <summary>
    /// A downloads controller on a scope of its own, for the same reason as
    /// <see cref="NewWorksRequest"/>: these tests queue through one "request" and read the queue
    /// back through the next, and a shared context would answer the second out of the change
    /// tracker the first warmed.
    /// </summary>
    public DownloadsController NewDownloadsRequest(ApplicationUser user)
    {
        var scope = _provider.CreateScope();
        _perRequestScopes.Add(scope);

        return Build(
            new DownloadsController(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                scope.ServiceProvider.GetRequiredService<DownloadWakeSignal>(),
                scope.ServiceProvider.GetRequiredService<StoragePaths>(),
                scope.ServiceProvider.GetRequiredService<ILogger<DownloadsController>>()),
            user,
            scope.ServiceProvider);
    }

    /// <summary>
    /// A statistics controller on a scope of its own, for the reason <see cref="NewWorksRequest"/>
    /// gives: every one of these tests writes reading state through one "request" and then asks a
    /// later one to count it, and a shared context would answer the second out of the change
    /// tracker the first warmed.
    /// </summary>
    public StatsController Stats(ApplicationUser user)
    {
        var scope = _provider.CreateScope();
        _perRequestScopes.Add(scope);

        return Build(
            new StatsController(scope.ServiceProvider.GetRequiredService<AppDbContext>()),
            user,
            scope.ServiceProvider);
    }

    public SavedFiltersController SavedFilters(ApplicationUser user) => Build(new SavedFiltersController(
        _request.ServiceProvider.GetRequiredService<AppDbContext>()), user);

    public LookupsController Lookups(ApplicationUser user) => Build(new LookupsController(
        _request.ServiceProvider.GetRequiredService<AppDbContext>()), user);

    /// <summary>
    /// A scope of its own per call, unlike the library controllers above. The credential endpoints
    /// are exercised several "requests" deep in one test, with the store written between them; a
    /// shared context would answer a later request out of a change tracker the previous one warmed,
    /// which no real request ever does.
    /// </summary>
    public AdminAo3CredentialController AdminAo3Credential(ApplicationUser user)
    {
        var scope = _provider.CreateScope();
        _perRequestScopes.Add(scope);

        return Build(new AdminAo3CredentialController(
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<IAo3InstanceCredentialStore>(),
            scope.ServiceProvider.GetRequiredService<Ao3LoginBackoff>(),
            scope.ServiceProvider.GetRequiredService<ILogger<AdminAo3CredentialController>>()),
            user, scope.ServiceProvider);
    }

    /// <summary>
    /// A scope of its own per call, for the same reason as the credential endpoints above: a
    /// restart is written through one "request" and read back through the next, and a shared
    /// context would answer the second out of the change tracker the first warmed.
    /// </summary>
    public AdminShipsController AdminShips(ApplicationUser user)
    {
        var scope = _provider.CreateScope();
        _perRequestScopes.Add(scope);

        return Build(new AdminShipsController(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<ILogger<AdminShipsController>>()),
            user, scope.ServiceProvider);
    }

    /// <summary>
    /// A scope of its own per call, for the same reason as the credential endpoints above.
    /// </summary>
    public AdminScrapingController AdminScraping(ApplicationUser user)
    {
        var scope = _provider.CreateScope();
        _perRequestScopes.Add(scope);

        return Build(new AdminScrapingController(
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            scope.ServiceProvider.GetRequiredService<IPersistedSettingsStore>(),
            scope.ServiceProvider.GetRequiredService<IOperatorContactResolver>(),
            scope.ServiceProvider.GetRequiredService<ScrapingGate>(),
            scope.ServiceProvider.GetRequiredService<InstanceIdentity>(),
            scope.ServiceProvider.GetRequiredService<IOptions<Ao3HttpClientOptions>>(),
            scope.ServiceProvider.GetRequiredService<ILogger<AdminScrapingController>>()),
            user, scope.ServiceProvider);
    }

    /// <summary>
    /// The credential store as the scraper sees it — a scope of its own, so a test reading a
    /// password back is reading what was persisted rather than what a tracked entity remembers.
    /// </summary>
    public async Task<T> WithCredentialStoreAsync<T>(Func<IAo3InstanceCredentialStore, Task<T>> work)
    {
        using var scope = _provider.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<IAo3InstanceCredentialStore>());
    }

    /// <summary>
    /// Attaches the principal an auth cookie would carry. NameIdentifier is the claim Identity
    /// keeps the user id under, and the one both controllers read.
    /// </summary>
    private T Build<T>(T controller, ApplicationUser user, IServiceProvider? services = null)
        where T : ControllerBase
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id)], "Test");
        var http = new DefaultHttpContext
        {
            RequestServices = services ?? _request.ServiceProvider,
            User = new ClaimsPrincipal(identity),
        };

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }
}

/// <summary>
/// Stands in for AO3. Answers 200 with an empty body and no redirect unless a test says otherwise,
/// which is the "tag exists and is already canonical" case.
/// </summary>
internal sealed class FakeAo3Http : IRateLimitedHttpClient
{
    /// <summary>Every URL asked for, in order — so a test can assert nothing was fetched at all.</summary>
    public List<string> Requested { get; } = [];

    /// <summary>
    /// Login-page fetches, kept apart from <see cref="Requested"/> on purpose: that list means
    /// "pages the scraper asked for", and folding an authentication round trip into it would change
    /// what every existing assertion over it is saying.
    /// </summary>
    public List<string> LoginPagesRequested { get; } = [];

    /// <summary>Every form posted, in order. The login POST is the only one this app makes.</summary>
    public List<PostedForm> Posted { get; } = [];

    /// <summary>
    /// Every file fetched, in order. Kept apart from <see cref="Requested"/> for the same reason
    /// the login page is: that list means "pages", and a download is two requests of which only the
    /// first is one.
    /// </summary>
    public List<string> FilesRequested { get; } = [];

    /// <summary>What AO3 serves for a download. Defaults to a small file that arrives intact.</summary>
    public Func<string, (HttpStatusCode Status, byte[] Body)> RespondsToDownload { get; set; } =
        _ => (HttpStatusCode.OK, "EPUB bytes"u8.ToArray());

    /// <summary>
    /// Makes every download report that it ran past what the instance will store. The real
    /// transport decides this by counting bytes against a configured ceiling, which is its own
    /// business — what a caller has to do about it is this fixture's.
    /// </summary>
    public bool DownloadsExceedTheSizeLimit { get; set; }

    /// <summary>
    /// Where a download request ends up, when that is not where it was sent. The real transport
    /// follows redirects, so AO3 declining a download answers 200 from somewhere else entirely —
    /// which is the one case a status code cannot describe.
    /// </summary>
    public string? DownloadsLandOn { get; set; }

    /// <summary>Set to make the transport itself fail, the way an unreachable archive does.</summary>
    public Exception? Fails { get; set; }

    public Func<string, ScrapeHttpResponse>? Responds { get; set; }

    /// <summary>
    /// What AO3's login page answers. Defaults to the real capture, so a test that is about
    /// something else still authenticates through the actual parser rather than around it.
    /// </summary>
    public Func<string, ScrapeHttpResponse> RespondsToLoginPage { get; set; } =
        url => new ScrapeHttpResponse(
            Fixtures.Load(Fixtures.LoginPage), HttpStatusCode.OK, FromCache: false, FinalUrl: url,
            SetCookieHeaders: ["_otwarchive_session=before-login; path=/; HttpOnly"]);

    /// <summary>
    /// What the login POST answers. Defaults to what a successful one looks like: a redirect away
    /// from the login page, carrying the session it just established.
    /// </summary>
    public Func<string, ScrapeHttpResponse> RespondsToPost { get; set; } =
        url => new ScrapeHttpResponse(
            "", HttpStatusCode.Found, FromCache: false, FinalUrl: url,
            SetCookieHeaders: ["_otwarchive_session=logged-in; path=/; HttpOnly"],
            Location: "https://ao3.test/users/shipwatcharr");

    public Task<ScrapeHttpResponse> GetAsync(string url, CancellationToken ct = default)
    {
        Requested.Add(url);
        if (Fails is not null) throw Fails;

        return Task.FromResult(Responds?.Invoke(url)
            ?? new ScrapeHttpResponse("", HttpStatusCode.OK, FromCache: false, FinalUrl: url));
    }

    public Task<ScrapeHttpResponse> GetLoggedOutAsync(string url, CancellationToken ct = default)
    {
        LoginPagesRequested.Add(url);
        if (Fails is not null) throw Fails;

        return Task.FromResult(RespondsToLoginPage(url));
    }

    public Task<ScrapeHttpResponse> PostFormAsync(
        string url,
        IReadOnlyDictionary<string, string> fields,
        string? cookieHeader,
        CancellationToken ct = default)
    {
        Posted.Add(new PostedForm(url, fields, cookieHeader));
        if (Fails is not null) throw Fails;

        return Task.FromResult(RespondsToPost(url));
    }

    public async Task<ScrapeDownloadResponse> DownloadAsync(
        string url, Stream destination, CancellationToken ct = default)
    {
        FilesRequested.Add(url);
        if (Fails is not null) throw Fails;

        var (status, body) = RespondsToDownload(url);

        // Only a 200 writes, the way the real transport only writes a 200: a body explaining a
        // failure is not the file, and a fake that wrote it anyway would let a test pass on bytes
        // the app would never have stored.
        if (status == HttpStatusCode.OK) await destination.WriteAsync(body, ct);

        return new ScrapeDownloadResponse(
            status,
            status == HttpStatusCode.OK ? body.Length : 0,
            DownloadsLandOn ?? url,
            ExceededSizeLimit: DownloadsExceedTheSizeLimit);
    }
}

internal sealed record PostedForm(
    string Url, IReadOnlyDictionary<string, string> Fields, string? CookieHeader);

/// <summary>Reports whatever contact the host currently holds, re-read on every call.</summary>
internal sealed class StubContacts(Func<string?> contact) : IOperatorContactResolver
{
    public Task<OperatorContactResolution> ResolveAsync(CancellationToken ct = default) =>
        Task.FromResult(new OperatorContactResolution(contact(), OperatorContactSource.AdminSetting));

    public Task<OperatorContactResolution> ResolveDefaultAsync(CancellationToken ct = default) =>
        ResolveAsync(ct);
}

/// <summary>
/// Claims a scraper key without doing anything. Only its presence in the registry matters — it is
/// what lets a test distinguish "no implementation is registered" from "one is", which is the
/// difference the Ships list reports as <c>ScraperAvailable</c>.
///
/// The stop reason is settable because a scraper reports most failures by *returning* one rather
/// than throwing — see <see cref="ScrapeStopReason.Error"/> — and what the worker records for such
/// a run is a question a stub that only ever succeeds cannot ask.
/// </summary>
internal sealed class StubScraper(string key, string stopReason = "stub", string? errorMessage = null)
    : IAo3Scraper
{
    public string Key { get; } = key;

    /// <summary>
    /// Which pass the worker asked for, in order. The worker chooses between three of them off the
    /// ship's own state, and that choice is not visible anywhere else — a stub that discarded it
    /// would leave the rule testable only through a real walk.
    /// </summary>
    public List<ScrapeRunMode> ModesRun { get; } = [];

    public bool Supports(ScrapeRunMode mode) => true;

    public Task<ScrapeOutcome> ExecuteAsync(ScrapeContext context, CancellationToken ct = default)
    {
        ModesRun.Add(context.Mode);
        return Task.FromResult(ScrapeOutcome.Empty(stopReason) with { ErrorMessage = errorMessage });
    }
}

/// <summary>
/// A clock a test can move. Starts at the wall clock so a fixture that never touches it behaves as
/// it always did, and hand-rolled rather than pulled in from a testing package — one settable
/// property is the whole requirement.
/// </summary>
internal sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => Now;
}
