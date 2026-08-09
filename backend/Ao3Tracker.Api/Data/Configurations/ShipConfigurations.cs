using Ao3Tracker.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ao3Tracker.Api.Data.Configurations;

public class ShipConfiguration : IEntityTypeConfiguration<Ship>
{
    public void Configure(EntityTypeBuilder<Ship> entity)
    {
        entity.HasKey(s => s.Id);

        entity.Property(s => s.CanonicalTagName).HasMaxLength(200).IsRequired();
        entity.Property(s => s.CanonicalTagNameNormalized).HasMaxLength(200).IsRequired();
        entity.Property(s => s.TagUrlSegment).HasMaxLength(400).IsRequired();

        // One row per tracked tag, however many users watch it.
        entity.HasIndex(s => s.CanonicalTagNameNormalized).IsUnique();
        entity.HasIndex(s => s.Ao3TagId);

        entity.HasOne(s => s.Tag)
            .WithMany()
            .HasForeignKey(s => s.TagId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class WatchedShipConfiguration : IEntityTypeConfiguration<WatchedShip>
{
    public void Configure(EntityTypeBuilder<WatchedShip> entity)
    {
        entity.HasKey(w => w.Id);

        entity.Property(w => w.DisplayNameOverride).HasMaxLength(200);

        entity.HasOne(w => w.User)
            .WithMany(u => u.WatchedShips)
            .HasForeignKey(w => w.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(w => w.Ship)
            .WithMany(s => s.Watchers)
            .HasForeignKey(w => w.ShipId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(w => new { w.UserId, w.ShipId }).IsUnique();
        entity.HasIndex(w => w.ShipId);
    }
}

public class ShipWorkConfiguration : IEntityTypeConfiguration<ShipWork>
{
    public void Configure(EntityTypeBuilder<ShipWork> entity)
    {
        entity.HasKey(sw => new { sw.ShipId, sw.WorkId });

        entity.HasOne(sw => sw.Ship)
            .WithMany(s => s.Works)
            .HasForeignKey(sw => sw.ShipId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(sw => sw.Work)
            .WithMany(w => w.Ships)
            .HasForeignKey(sw => sw.WorkId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(sw => sw.WorkId);

        // A completed sweep looks for rows it didn't touch this pass.
        entity.HasIndex(sw => new { sw.ShipId, sw.LastSeenAt });
    }
}
