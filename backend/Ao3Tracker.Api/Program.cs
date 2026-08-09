using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Credentials;
using Ao3Tracker.Api.Services.Scraping;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---- Database (Npgsql by default; swap the provider package + this call to move to SQL Server) ----
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:Default configuration.")));

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
// Persist keys to a mounted volume in production so they survive container restarts;
// see docker-compose.yml / README for the DataProtection:KeyPath setting.
var keyPath = builder.Configuration["DataProtection:KeyPath"];
var dataProtectionBuilder = builder.Services.AddDataProtection().SetApplicationName("Ao3Tracker");
if (!string.IsNullOrWhiteSpace(keyPath))
{
    dataProtectionBuilder.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
}

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
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Default")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:Default configuration."));

var app = builder.Build();

if (!Microsoft.EntityFrameworkCore.EF.IsDesignTime)
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
