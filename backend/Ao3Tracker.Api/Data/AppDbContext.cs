using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Ao3Credential> Ao3Credentials => Set<Ao3Credential>();
    public DbSet<ScrapeJob> ScrapeJobs => Set<ScrapeJob>();
    public DbSet<ScrapeRun> ScrapeRuns => Set<ScrapeRun>();
    public DbSet<ScrapedItem> ScrapedItems => Set<ScrapedItem>();

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
