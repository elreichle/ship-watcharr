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
        options.Password.RequiredLength = 8;
        options.User.RequireUniqueEmail = true;
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
builder.Services
    .AddHttpClient<IRateLimitedHttpClient, RateLimitedAo3HttpClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Ao3HttpClientOptions>>().Value;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        client.Timeout = TimeSpan.FromSeconds(30);
    });

// ---- Scrapers (add new IAo3Scraper implementations here; ScraperRegistry picks them up automatically) ----
builder.Services.AddScoped<IAo3Scraper, PlaceholderScraper>();
builder.Services.AddScoped<ScraperRegistry>();

// ---- Background scheduling worker ----
builder.Services.AddHostedService<ScrapeWorker>();

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
