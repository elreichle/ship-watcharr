using System.Net;
using System.Security.Claims;
using Ao3Tracker.Api.Controllers;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    public LibraryTestHost(params IAo3Scraper[] scrapers)
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
        services.AddSingleton(InstanceIdentity.LoadOrCreate(new StoragePaths(
            _dataDirectory,
            Path.Combine(_dataDirectory, "test.db"),
            Path.Combine(_dataDirectory, "settings.json"),
            Path.Combine(_dataDirectory, "keys"))));
        services.AddSingleton<IOperatorContactResolver>(new StubContacts(() => OperatorContact));
        services.AddScoped<Ao3UserAgentProvider>();

        services.AddSingleton<IRateLimitedHttpClient>(Http);
        services.AddScoped<IShipVerifier, Ao3ShipVerifier>();
        services.AddSingleton(ScrapeWake);

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

    /// <summary>A context of its own, so persistence assertions are real round-trips.</summary>
    public AppDbContext NewContext() => new SqliteAppDbContext(
        new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite(_connection).Options);

    public ApplicationUser SeedUser(string userName = "emma")
    {
        using var db = NewContext();

        var user = new ApplicationUser
        {
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
        };
        db.Users.Add(user);
        db.SaveChanges();

        return user;
    }

    public ShipsController Ships(ApplicationUser user) => Build(new ShipsController(
        _request.ServiceProvider.GetRequiredService<AppDbContext>(),
        _request.ServiceProvider.GetRequiredService<ScraperRegistry>(),
        _request.ServiceProvider.GetRequiredService<Ao3UserAgentProvider>(),
        _request.ServiceProvider.GetRequiredService<ScrapeWakeSignal>()), user);

    public WorksController Works(ApplicationUser user) => Build(new WorksController(
        _request.ServiceProvider.GetRequiredService<AppDbContext>()), user);

    public SavedFiltersController SavedFilters(ApplicationUser user) => Build(new SavedFiltersController(
        _request.ServiceProvider.GetRequiredService<AppDbContext>()), user);

    public LookupsController Lookups(ApplicationUser user) => Build(new LookupsController(
        _request.ServiceProvider.GetRequiredService<AppDbContext>()), user);

    /// <summary>
    /// Attaches the principal an auth cookie would carry. NameIdentifier is the claim Identity
    /// keeps the user id under, and the one both controllers read.
    /// </summary>
    private T Build<T>(T controller, ApplicationUser user) where T : ControllerBase
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id)], "Test");
        var http = new DefaultHttpContext
        {
            RequestServices = _request.ServiceProvider,
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

    /// <summary>Set to make the transport itself fail, the way an unreachable archive does.</summary>
    public Exception? Fails { get; set; }

    public Func<string, ScrapeHttpResponse>? Responds { get; set; }

    public Task<ScrapeHttpResponse> GetAsync(string url, CancellationToken ct = default)
    {
        Requested.Add(url);
        if (Fails is not null) throw Fails;

        return Task.FromResult(Responds?.Invoke(url)
            ?? new ScrapeHttpResponse("", HttpStatusCode.OK, FromCache: false, FinalUrl: url));
    }
}

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
/// </summary>
internal sealed class StubScraper(string key) : IAo3Scraper
{
    public string Key { get; } = key;

    public bool Supports(ScrapeRunMode mode) => true;

    public Task<ScrapeOutcome> ExecuteAsync(ScrapeContext context, CancellationToken ct = default) =>
        Task.FromResult(ScrapeOutcome.Empty("stub"));
}
