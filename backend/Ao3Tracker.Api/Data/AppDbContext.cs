using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Data;

/// <summary>
/// Shared entity model. Abstract because EF Core migrations can't be shared across
/// providers from one context type — <see cref="SqliteAppDbContext"/> and
/// <see cref="PostgresAppDbContext"/> are the concrete, migratable contexts, each with
/// its own migration history. Everything else in the app depends on this base type and
/// is unaware of which provider is actually active.
///
/// The model divides into global scraped data (works, tags, authors, series, ships) stored once
/// and shared by every user, and per-user data (watched ships, reading state, downloads,
/// credentials) keyed by UserId. Keeping scrape state off the per-user rows is what allows two
/// users watching the same ship to share one scrape instead of duplicating it.
/// </summary>
public abstract class AppDbContext : IdentityDbContext<ApplicationUser>
{
    protected AppDbContext(DbContextOptions options) : base(options)
    {
    }

    // Per-user
    public DbSet<WatchedShip> WatchedShips => Set<WatchedShip>();
    public DbSet<UserWorkState> UserWorkStates => Set<UserWorkState>();
    public DbSet<Download> Downloads => Set<Download>();
    public DbSet<SavedWorkFilter> SavedWorkFilters => Set<SavedWorkFilter>();
    public DbSet<SavedWorkFilterTag> SavedWorkFilterTags => Set<SavedWorkFilterTag>();
    public DbSet<SavedWorkFilterAuthor> SavedWorkFilterAuthors => Set<SavedWorkFilterAuthor>();
    public DbSet<Notification> Notifications => Set<Notification>();

    // Global — scraped data
    public DbSet<Work> Works => Set<Work>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<WorkTag> WorkTags => Set<WorkTag>();
    public DbSet<Ao3Pseud> Ao3Pseuds => Set<Ao3Pseud>();
    public DbSet<WorkAuthor> WorkAuthors => Set<WorkAuthor>();
    public DbSet<Ao3Series> Ao3Series => Set<Ao3Series>();
    public DbSet<WorkSeries> WorkSeries => Set<WorkSeries>();
    public DbSet<Ship> Ships => Set<Ship>();
    public DbSet<ShipWork> ShipWorks => Set<ShipWork>();
    public DbSet<WorkDownloadFile> WorkDownloadFiles => Set<WorkDownloadFile>();

    // Instance-level — the one AO3 account this deployment scrapes as. Not per-user: a ship is
    // scraped once for everyone following it, so there is no per-user login for it to use.
    public DbSet<Ao3InstanceCredential> Ao3InstanceCredentials => Set<Ao3InstanceCredential>();

    // Scheduling
    public DbSet<ScrapeJob> ScrapeJobs => Set<ScrapeJob>();
    public DbSet<ScrapeRun> ScrapeRuns => Set<ScrapeRun>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // See UtcDateTimeConverter: guarantees every DateTime read back from either
        // provider comes back Kind=Utc, so it serializes to JSON with a "Z", and that every
        // value written is Kind=Utc, which Npgsql requires and will otherwise reject.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Configuration lives in Data/Configurations. Both concrete contexts scan the same
        // assembly, so neither provider can drift from the other's model — which is the
        // invariant the whole two-context arrangement depends on.
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
