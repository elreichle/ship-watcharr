using Ao3Tracker.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ao3Tracker.Api.Data.Configurations;

public class SavedWorkFilterConfiguration : IEntityTypeConfiguration<SavedWorkFilter>
{
    public void Configure(EntityTypeBuilder<SavedWorkFilter> entity)
    {
        entity.HasKey(f => f.Id);

        entity.Property(f => f.Name).HasMaxLength(100).IsRequired();
        entity.Property(f => f.LanguageCode).HasMaxLength(16);
        entity.Property(f => f.Sort).HasMaxLength(32).IsRequired();

        entity.HasOne(f => f.User)
            .WithMany(u => u.SavedWorkFilters)
            .HasForeignKey(f => f.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ships are global and outlive any one subscription, so a filter naming one stays valid
        // even after its author unwatches it. Restrict rather than cascade: silently deleting
        // someone's saved view because a ship row went away would lose named work, and nothing in
        // the app deletes ships today anyway.
        entity.HasOne(f => f.Ship)
            .WithMany()
            .HasForeignKey(f => f.ShipId)
            .OnDelete(DeleteBehavior.Restrict);

        // The name is how the user picks the set out of a dropdown, so two identical ones would be
        // indistinguishable. Case-sensitivity differs between the providers here; the controller
        // does the comparison it wants and this index is the backstop.
        entity.HasIndex(f => new { f.UserId, f.Name }).IsUnique();

        // Answers "which set opens by default", which is a lookup on every unqualified works
        // request. Deliberately NOT a unique filtered index enforcing one default per user:
        // HasFilter takes raw provider-specific SQL, and one model has to serve both SQLite and
        // PostgreSQL. SavedFiltersController holds that invariant inside a transaction instead.
        entity.HasIndex(f => new { f.UserId, f.IsDefault });
    }
}

public class SavedWorkFilterTagConfiguration : IEntityTypeConfiguration<SavedWorkFilterTag>
{
    public void Configure(EntityTypeBuilder<SavedWorkFilterTag> entity)
    {
        entity.HasKey(t => new { t.SavedWorkFilterId, t.TagId });

        entity.HasOne(t => t.SavedWorkFilter)
            .WithMany(f => f.Tags)
            .HasForeignKey(t => t.SavedWorkFilterId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(t => t.Tag)
            .WithMany()
            .HasForeignKey(t => t.TagId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(t => t.TagId);
    }
}

public class SavedWorkFilterAuthorConfiguration : IEntityTypeConfiguration<SavedWorkFilterAuthor>
{
    public void Configure(EntityTypeBuilder<SavedWorkFilterAuthor> entity)
    {
        entity.HasKey(a => new { a.SavedWorkFilterId, a.PseudId });

        entity.HasOne(a => a.SavedWorkFilter)
            .WithMany(f => f.Authors)
            .HasForeignKey(a => a.SavedWorkFilterId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(a => a.Pseud)
            .WithMany()
            .HasForeignKey(a => a.PseudId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(a => a.PseudId);
    }
}
