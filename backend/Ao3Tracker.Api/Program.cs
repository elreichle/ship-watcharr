using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Credentials;
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
builder.Services.AddScoped<IAo3CredentialStore, Ao3CredentialStore>();

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

builder.Services
    .AddHttpClient<IRateLimitedHttpClient, RateLimitedAo3HttpClient>(client =>
    {
        // No default User-Agent here on purpose — see RateLimitedAo3HttpClient.SendWithRetryAsync,
        // which sets it per request so a settings change takes effect immediately.
        client.Timeout = TimeSpan.FromSeconds(30);
    });

// ---- Scrapers (add new IAo3Scraper implementations here; ScraperRegistry picks them up automatically) ----
// None registered yet: the placeholder scraper was removed along with the placeholder schema,
// and the real ship-index scraper arrives with the AO3 parser.
builder.Services.AddScoped<ScraperRegistry>();

// ---- Ship verification (confirms a followed tag exists on AO3, and folds synonyms into
// their canonical tag). Shares the rate-limited client, so it cannot outpace scraping. ----
builder.Services.AddScoped<IShipVerifier, Ao3ShipVerifier>();

// ---- Background workers ----
// Singleton, and registered before the worker that waits on it: the signal is the one piece of
// state a scoped request and the long-lived worker have to share.
builder.Services.AddSingleton<ScrapeWakeSignal>();
builder.Services.AddHostedService<ScrapeWorker>();
builder.Services.AddHostedService<ShipVerificationWorker>();

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
