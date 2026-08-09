using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Data;

/// <summary>Concrete context for the SQLite provider. Migrations live in Data/Migrations/Sqlite.</summary>
public class SqliteAppDbContext : AppDbContext
{
    public SqliteAppDbContext(DbContextOptions<SqliteAppDbContext> options) : base(options)
    {
    }
}
