using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Data;

/// <summary>Concrete context for the PostgreSQL provider. Migrations live in Data/Migrations/Postgres.</summary>
public class PostgresAppDbContext : AppDbContext
{
    public PostgresAppDbContext(DbContextOptions<PostgresAppDbContext> options) : base(options)
    {
    }
}
