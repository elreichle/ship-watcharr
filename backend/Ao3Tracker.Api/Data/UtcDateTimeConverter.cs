using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ao3Tracker.Api.Data;

/// <summary>
/// Every DateTime this app stores is UTC by convention (always DateTime.UtcNow). Neither
/// SQLite nor Npgsql reliably preserve DateTimeKind on round-trip, so without this every
/// value read back comes back Kind=Unspecified — which System.Text.Json then serializes
/// with no "Z"/offset, and the frontend's `new Date(...)` would misread it as local time
/// instead of UTC. Applied to every DateTime property via AppDbContext.ConfigureConventions.
/// </summary>
public class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter() : base(
        v => v,
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}
