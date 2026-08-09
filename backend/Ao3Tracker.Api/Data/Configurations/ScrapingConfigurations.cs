using Ao3Tracker.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ao3Tracker.Api.Data.Configurations;

public class ScrapeJobConfiguration : IEntityTypeConfiguration<ScrapeJob>
{
    public void Configure(EntityTypeBuilder<ScrapeJob> entity)
    {
        entity.HasKey(j => j.Id);

        entity.Property(j => j.Name).HasMaxLength(200).IsRequired();
        entity.Property(j => j.ScraperKey).HasMaxLength(100).IsRequired();

        entity.HasOne(j => j.Ship)
            .WithMany()
            .HasForeignKey(j => j.ShipId)
            .OnDelete(DeleteBehavior.Cascade);

        // One job per ship: the point of ship-scoped jobs is that N watchers produce one schedule.
        entity.HasIndex(j => j.ShipId).IsUnique();

        // The scheduler's hot query: enabled jobs whose next run is due.
        entity.HasIndex(j => new { j.IsEnabled, j.NextRunAt });
    }
}

public class ScrapeRunConfiguration : IEntityTypeConfiguration<ScrapeRun>
{
    public void Configure(EntityTypeBuilder<ScrapeRun> entity)
    {
        entity.HasKey(r => r.Id);

        entity.Property(r => r.StopReason).HasMaxLength(50);

        entity.HasOne(r => r.ScrapeJob)
            .WithMany(j => j.Runs)
            .HasForeignKey(r => r.ScrapeJobId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(r => new { r.ScrapeJobId, r.StartedAt });

        // Startup reconciliation scans for runs still claiming to be Running.
        entity.HasIndex(r => r.Status);
    }
}
