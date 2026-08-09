using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ao3Tracker.Api.Data;

/// <summary>Design-time-only factory; see SqliteAppDbContextFactory for why this exists.</summary>
public class PostgresAppDbContextFactory : IDesignTimeDbContextFactory<PostgresAppDbContext>
{
    public PostgresAppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PostgresAppDbContext>()
            .UseNpgsql("Host=localhost;Database=design-time;Username=design-time;Password=design-time")
            .Options;
        return new PostgresAppDbContext(options);
    }
}
