using Ao3Tracker.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ao3Tracker.Api.Data.Configurations;

public class UserWorkStateConfiguration : IEntityTypeConfiguration<UserWorkState>
{
    public void Configure(EntityTypeBuilder<UserWorkState> entity)
    {
        entity.HasKey(s => s.Id);

        entity.Property(s => s.Note).HasMaxLength(4000);

        entity.HasOne(s => s.User)
            .WithMany(u => u.WorkStates)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(s => s.Work)
            .WithMany()
            .HasForeignKey(s => s.WorkId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(s => new { s.UserId, s.WorkId }).IsUnique();
        entity.HasIndex(s => new { s.UserId, s.Status });
        entity.HasIndex(s => new { s.UserId, s.Rating });
        entity.HasIndex(s => new { s.UserId, s.FavoritedAt });

        // Half-stars, 1-10. Double-quoted identifiers are portable across both providers.
        // Safe to declare on a brand-new table; SQLite cannot ALTER TABLE ADD CONSTRAINT, so
        // adding one later would force a full table rebuild.
        entity.ToTable(t => t.HasCheckConstraint(
            "CK_UserWorkStates_Rating",
            "\"Rating\" IS NULL OR (\"Rating\" >= 1 AND \"Rating\" <= 10)"));
    }
}

public class WorkDownloadFileConfiguration : IEntityTypeConfiguration<WorkDownloadFile>
{
    public void Configure(EntityTypeBuilder<WorkDownloadFile> entity)
    {
        entity.HasKey(f => f.Id);

        entity.Property(f => f.RelativePath).HasMaxLength(400).IsRequired();
        entity.Property(f => f.Sha256).HasMaxLength(64);

        entity.HasOne(f => f.Work)
            .WithMany()
            .HasForeignKey(f => f.WorkId)
            .OnDelete(DeleteBehavior.Cascade);

        // One stored file per work/format/version — this is what stops two users downloading
        // identical bytes twice.
        entity.HasIndex(f => new { f.WorkId, f.Format, f.WorkUpdatedAt }).IsUnique();
    }
}

public class DownloadConfiguration : IEntityTypeConfiguration<Download>
{
    public void Configure(EntityTypeBuilder<Download> entity)
    {
        entity.HasKey(d => d.Id);

        entity.HasOne(d => d.User)
            .WithMany(u => u.Downloads)
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(d => d.Work)
            .WithMany()
            .HasForeignKey(d => d.WorkId)
            .OnDelete(DeleteBehavior.Cascade);

        // Dropping a user's copy must not delete a file other users still reference.
        entity.HasOne(d => d.File)
            .WithMany()
            .HasForeignKey(d => d.WorkDownloadFileId)
            .OnDelete(DeleteBehavior.SetNull);

        // The copy the reader still holds while a re-fetch is outstanding. SetNull for the same
        // reason, and it is the same file table: what distinguishes the two references is which
        // version of the work the request is reporting, not where the bytes live.
        entity.HasOne(d => d.PreviousFile)
            .WithMany()
            .HasForeignKey(d => d.PreviousWorkDownloadFileId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.HasIndex(d => new { d.UserId, d.WorkId, d.Format }).IsUnique();
        entity.HasIndex(d => d.WorkDownloadFileId);
        entity.HasIndex(d => d.PreviousWorkDownloadFileId);
    }
}

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> entity)
    {
        entity.HasKey(n => n.Id);

        entity.HasOne(n => n.User)
            .WithMany(u => u.Notifications)
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // A merge deletes the synonym ship, and its notifications go with it. That is the right
        // answer rather than a loss: the row said "this tag gained a work", and the tag it named
        // has stopped existing. The work is still in the library under the canonical ship.
        entity.HasOne(n => n.Ship)
            .WithMany()
            .HasForeignKey(n => n.ShipId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(n => n.Work)
            .WithMany()
            .HasForeignKey(n => n.WorkId)
            .OnDelete(DeleteBehavior.Cascade);

        // The two reads this table has: one reader's list newest-first, and their unread count.
        // Both are keyed on the owner first, which is also the whole of "whose notification is
        // this" — see the remarks on Notification.
        entity.HasIndex(n => new { n.UserId, n.Id });
        entity.HasIndex(n => new { n.UserId, n.ReadAt });
    }
}
