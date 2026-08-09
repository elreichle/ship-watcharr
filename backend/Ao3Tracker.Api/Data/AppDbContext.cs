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
/// </summary>
public abstract class AppDbContext : IdentityDbContext<ApplicationUser>
{
    protected AppDbContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<Ao3Credential> Ao3Credentials => Set<Ao3Credential>();
    public DbSet<ScrapeJob> ScrapeJobs => Set<ScrapeJob>();
    public DbSet<ScrapeRun> ScrapeRuns => Set<ScrapeRun>();
    public DbSet<ScrapedItem> ScrapedItems => Set<ScrapedItem>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // See UtcDateTimeConverter: guarantees every DateTime read back from either
        // provider comes back Kind=Utc, so it serializes to JSON with a "Z".
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Ao3Credential>(entity =>
        {
            entity.HasIndex(c => c.UserId).IsUnique();
            entity.HasOne(c => c.User)
                .WithOne(u => u.Ao3Credential)
                .HasForeignKey<Ao3Credential>(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ScrapeJob>(entity =>
        {
            entity.HasOne(j => j.User)
                .WithMany(u => u.ScrapeJobs)
                .HasForeignKey(j => j.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ScrapeRun>(entity =>
        {
            entity.HasOne(r => r.ScrapeJob)
                .WithMany(j => j.Runs)
                .HasForeignKey(r => r.ScrapeJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ScrapedItem>(entity =>
        {
            entity.HasOne(i => i.ScrapeRun)
                .WithMany(r => r.Items)
                .HasForeignKey(i => i.ScrapeRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
