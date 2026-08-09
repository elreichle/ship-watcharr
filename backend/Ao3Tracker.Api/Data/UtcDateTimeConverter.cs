using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ao3Tracker.Api.Data;

/// <summary>
/// Every DateTime this app stores is UTC by convention (always DateTime.UtcNow). Neither
/// SQLite nor Npgsql reliably preserve DateTimeKind on round-trip, so without this every
/// value read back comes back Kind=Unspecified — which System.Text.Json then serializes
/// with no "Z"/offset, and the frontend's `new Date(...)` would misread it as local time
/// instead of UTC. Applied to every DateTime property via AppDbContext.ConfigureConventions.
///
/// The write side normalizes Kind as well, and that is not cosmetic: Npgsql maps DateTime to
/// `timestamp with time zone` and throws on any value whose Kind is not Utc. Values built by
/// DateTime.UtcNow are fine, but parsed ones are not — DateTime.ParseExact returns
/// Kind=Unspecified, so a scraper parsing a date off an AO3 page would silently work on
/// SQLite and blow up on PostgreSQL. Normalizing here means neither provider can be fed a
/// value it rejects. (Parsers should still pass DateTimeStyles.AssumeUniversal so the value
/// is correct, not merely accepted — this converter guarantees storage, not correctness.)
/// </summary>
public class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter() : base(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}
