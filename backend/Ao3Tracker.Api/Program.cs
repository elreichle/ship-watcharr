using System.Net;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Credentials;
using Ao3Tracker.Api.Services.Downloads;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Settings;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Resolve where this instance keeps its local state (SQLite db, Data Protection
// keys, admin-editable settings.json) before anything else needs it ----
var storagePaths = StoragePaths.Resolve(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(storagePaths);

// Admin-configured overrides (currently just DB provider selection) layer on top of
// appsettings.json/appsettings.{Environment}.json here. This intentionally makes the
// persisted file win over environment variables/command-line args too: once someone
// saves a choice through the admin UI, that's the source of truth until they change it
// again (or an operator deletes settings.json). See README for the full precedence story.
builder.Configuration.AddJsonFile(storagePaths.SettingsFilePath, optional: true, reloadOnChange: false);

// ---- Database (SQLite by default — zero-config, one file, works out of the box for a
// self-hosted install. PostgreSQL is opt-in, configured either via the admin UI or the
// Database:Provider/Database:PostgresConnectionString settings, e.g. through the
// docker-compose.postgres.yml override.) ----
var databaseProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
if (string.Equals(databaseProvider, "Postgres", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration["Database:PostgresConnectionString"]
        ?? throw new InvalidOperationException("Database:Provider is Postgres but Database:PostgresConnectionString is not set.");

    builder.Services.AddDbContext<AppDbContext, PostgresAppDbContext>(options => options.UseNpgsql(connectionString));
}
else
{
    builder.Services.AddDbContext<AppDbContext, SqliteAppDbContext>(options =>
        options.UseSqlite($"Data Source={storagePaths.SqliteDbPath}"));
}

builder.Services.AddScoped<IPersistedSettingsStore, PersistedSettingsStore>();

// ---- Dashboard login (cookie-based ASP.NET Core Identity) ----
// AddIdentityCore alone does not register an authentication scheme; AddIdentityCookies()
// is what actually wires up the ApplicationScheme cookie handler that [Authorize] and
// SignInManager depend on.
builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddIdentityCookies();

builder.Services
    .AddIdentityCore<ApplicationUser>(options =>
    {
        // Length is the only rule, and it is the one that actually resists guessing. Identity's
        // composition defaults (a digit, both cases, a symbol) are off because they mostly push
        // people toward Password1! while the register form only ever promised a length -- so they
        // bought no real strength and produced an unexplained 400 for anyone who took the hint
        // at its word.
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;

        // Sign-in is by username; email is optional. Identity's user validator treats
        // RequireUniqueEmail as "email is mandatory too", so it has to stay off here.
        options.User.RequireUniqueEmail = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddAuthorization();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "Ao3Tracker.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;

    // This is an API consumed by a SPA: return status codes instead of redirecting to a login page.
    options.Events.OnRedirectToLogin = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = ctx =>
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

// ---- Data Protection (encrypts AO3 credentials + session cookies at rest) ----
// Keys live under the same data directory as everything else, so one mounted
// volume/folder is enough to survive restarts. Losing this key ring makes every stored
// AO3 credential unrecoverable.
builder.Services.AddDataProtection()
    .SetApplicationName("Ao3Tracker")
    .PersistKeysToFileSystem(new DirectoryInfo(storagePaths.KeysDirectory));

// ---- AO3 credential storage ----
builder.Services.AddScoped<IAo3InstanceCredentialStore, Ao3InstanceCredentialStore>();

// ---- Rate-limited scraping HTTP client ----
builder.Services.Configure<Ao3HttpClientOptions>(builder.Configuration.GetSection(Ao3HttpClientOptions.SectionName));
builder.Services.AddMemoryCache();

// Stable random id for this deployment, so two installs are distinguishable in AO3's logs
// without either revealing who runs them. Created on first run under the data directory.
builder.Services.AddSingleton(sp => InstanceIdentity.LoadOrCreate(sp.GetRequiredService<StoragePaths>()));

// Both scoped: resolving the operator contact reads settings.json and may fall back to the admin
// account's address, so it needs the (scoped) DbContext and must reflect changes made through the
// settings UI without a restart. The User-Agent is therefore built per request, not per client.
builder.Services.AddScoped<IOperatorContactResolver, OperatorContactResolver>();
builder.Services.AddScoped<Ao3UserAgentProvider>();

// The two gates on scraping — an honest User-Agent, and an AO3 login — answered together, so the
// worker that holds jobs and the admin screen that explains why cannot disagree. Scoped for the
// same reason as the two above: it re-reads settings and the credential row on every poll.
builder.Services.AddScoped<ScrapingGate>();

// The one outbound channel: the semaphore every request queues on, the spacing since the last
// send, and the hold AO3's 429s ask for. A singleton because it *is* the instance-wide fact —
// see Ao3RateGate — and the scrape worker reads it to defer a job to when AO3 said.
builder.Services.AddSingleton<Ao3RateGate>();

builder.Services
    .AddHttpClient<IRateLimitedHttpClient, RateLimitedAo3HttpClient>(client =>
    {
        // No default User-Agent here on purpose — see RateLimitedAo3HttpClient.SendOnceAsync,
        // which sets it per request so a settings change takes effect immediately.
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    // Cookies off, redirects off — two halves of one fact. The session cookie is set by hand
    // (the instance's session lives in a database row, and a per-handler jar would be a second,
    // divergent copy of it), and HttpClientHandler copies hand-set headers onto every redirect it
    // follows, wherever the Location points — so with automatic redirects an AO3 link that 302s
    // off-archive takes the instance's session with it. The client therefore walks redirects
    // itself, deciding per hop whether the session may travel and refusing to leave the archive at
    // all: see RateLimitedAo3HttpClient.SendFollowingRedirectsAsync. A synonym tag is still
    // recognised by where the request ended up, because the manual walk records that the same way.
    //
    // Compression on: a listing page is a few hundred kilobytes of HTML that gzips to a fraction
    // of that, and AO3 offers it (Vary: Accept-Encoding). It changes nothing about how many
    // requests go out or how far apart — only what each one costs the archive to serve.
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
    });

// The login POST's transport. A successful login answers with a 302 whose Set-Cookie *is* the
// session, and following it spends that cookie on a page nobody asked for — so this send never
// follows a redirect, not even one that stays on the archive, which is the one behaviour the
// scraping client's manual walk would allow. Same gate and same User-Agent — see
// Ao3LoginHttpClient.
builder.Services
    .AddHttpClient<Ao3LoginHttpClient>(client => client.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
    });

// ---- The instance's AO3 session ----
// Three seams rather than one, and the split is what keeps them acyclic: the HTTP client reads the
// cached cookie through IAo3SessionCache (which cannot log in), the establisher performs the round
// trip through the HTTP client, and the provider decides whether one is needed. Nothing that logs
// in is reachable from the thing that attaches cookies.
// Singleton, unlike its two neighbours: it owns the scope it reads and writes the session in,
// precisely so that a cookie check reached from inside a scrape does not save through the scrape's
// own DbContext. See Ao3SessionCache.
builder.Services.AddSingleton<IAo3SessionCache, Ao3SessionCache>();
builder.Services.AddScoped<IAo3SessionEstablisher, Ao3SessionEstablisher>();

// Singleton, like ScrapeWakeSignal and for the same reason: the worker that backs off and the admin
// request that cancels the backoff are the two things that have to agree about it.
builder.Services.AddSingleton<Ao3LoginBackoff>();
builder.Services.AddScoped<IAo3SessionProvider, Ao3SessionProvider>();

// ---- Scrapers (add new IAo3Scraper implementations here; ScraperRegistry picks them up automatically) ----
// Scoped rather than singleton: a scraper holds the DbContext it writes through, and the worker
// resolves one per job inside that job's own scope.
// Registered explicitly rather than left to the constructors' defaults, so a test can substitute a
// fake clock through the same container the app composes.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddScoped<IWorkIngestor, WorkIngestor>();
builder.Services.AddScoped<IAo3Scraper, Ao3ShipIndexScraper>();
builder.Services.AddScoped<ScraperRegistry>();

// The per-work detail pass, which is not an IAo3Scraper and so not in the registry: a ScrapeJob is
// one row per ship walking one tag's listing, and a work's publication date and full tag list are
// neither ship-scoped nor paginated. See Ao3WorkDetailScraper for the whole of that reasoning.
builder.Services.AddScoped<IAo3WorkDetailScraper, Ao3WorkDetailScraper>();

// Singleton, because it is what one pass remembers for the next: the works whose page this process
// has asked for and could not read. See WorkDetailAttempts for why it is memory and not a column.
builder.Services.AddSingleton<WorkDetailAttempts>();

// ---- Ship verification (confirms a followed tag exists on AO3, and folds synonyms into
// their canonical tag). Shares the rate-limited client, so it cannot outpace scraping. ----
builder.Services.AddScoped<IShipVerifier, Ao3ShipVerifier>();

// ---- Downloads ----
// Scoped for the same reason a scraper is: the fetcher writes through the DbContext of the scope
// the worker resolved it in, one per queued request.
builder.Services.AddScoped<IDownloadFetcher, DownloadFetcher>();

// The rules for which file, if any, stands behind a reader's request — shared by the endpoint
// that serves the bytes and the one that opens them as a book, so the two cannot disagree.
builder.Services.AddScoped<StoredCopyResolver>();

// The rules for what asking for a copy does to the queue — shared by the endpoint behind the
// format buttons and the favorite mark that can ask on a reader's behalf, so the two cannot
// disagree about what "already asked for" means.
builder.Services.AddScoped<DownloadRequests>();

// ---- Background workers ----
// Singleton, and registered before the worker that waits on it: the signal is the one piece of
// state a scoped request and the long-lived worker have to share.
builder.Services.AddSingleton<ScrapeWakeSignal>();
builder.Services.AddSingleton<DownloadWakeSignal>();
builder.Services.AddHostedService<ScrapeWorker>();
builder.Services.AddHostedService<ShipVerificationWorker>();

// Shares the rate gate with the two above, so a queue of downloads cannot outpace scraping or be
// outpaced by it — one instance, one stream of requests to AO3.
builder.Services.AddHostedService<DownloadWorker>();

// The same gate again, and the same rate limiter: reading a work's own page is one more request
// this instance makes to AO3, and the least urgent of the three.
builder.Services.AddHostedService<WorkDetailWorker>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

var app = builder.Build();

if (!EF.IsDesignTime)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// No UseHttpsRedirection: the container serves plain HTTP and expects TLS termination
// (if any) to happen at a reverse proxy in front of it — see README.

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/healthz");

// SPA fallback: any non-API, non-file route serves the React app's index.html so
// client-side routing (React Router, etc.) works on refresh/deep links.
app.MapFallbackToFile("index.html");

app.Run();
