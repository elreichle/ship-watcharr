using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ao3Tracker.Api.Data;

/// <summary>
/// Used only by `dotnet ef migrations add --context SqliteAppDbContext ...`. Takes priority
/// over the app's normal hosting/DI pipeline, so `dotnet ef` can generate SQLite migrations
/// regardless of which provider Program.cs is currently configured for. The connection
/// string here is never actually opened by `migrations add` — it only needs to name a
/// provider.
/// </summary>
public class SqliteAppDbContextFactory : IDesignTimeDbContextFactory<SqliteAppDbContext>
{
    public SqliteAppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SqliteAppDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;
        return new SqliteAppDbContext(options);
    }
}
