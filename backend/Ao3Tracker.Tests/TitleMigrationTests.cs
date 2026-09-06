using Ao3Tracker.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Ao3Tracker.Tests;

/// <summary>
/// The <c>SearchableTitle</c> upgrade, run against a database that already holds works. Same
/// seam as <see cref="PseudMigrationTests"/>: nothing else in the suite executes a migration's
/// SQL, and a backfill that left the new column empty would fail no test and quietly make every
/// work scraped before the upgrade unsearchable until its next listing pass.
/// </summary>
public class TitleMigrationTests : IDisposable
{
    private const string Before = "20260906160533_FavoriteWorks";
    private const string Under = "20260906162323_SearchableTitle";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly IMigrator _migrator;

    public TitleMigrationTests()
    {
        _connection.Open();
        _db = new SqliteAppDbContext(
            new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite(_connection).Options);
        _migrator = _db.GetInfrastructure().GetRequiredService<IMigrator>();
        _migrator.Migrate(Before);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Existing_works_get_an_uppercased_title_to_search_through()
    {
        SeedWork(100, "the Long night");

        _migrator.Migrate(Under);

        using var command = _connection.CreateCommand();
        command.CommandText = """SELECT "TitleNormalized" FROM "Works" WHERE "Id" = 100;""";
        Assert.Equal("THE LONG NIGHT", command.ExecuteScalar());
    }

    private void SeedWork(long id, string title) =>
        _db.Database.ExecuteSqlRaw(
            """
            INSERT INTO "Works" (
                "Id", "Title", "Rating", "Categories", "Warnings", "IsComplete", "WordCount",
                "ChapterCount", "Hits", "Kudos", "CommentCount", "Bookmarks", "CollectionCount",
                "UpdatedAt", "UpdatedAtIsApproximate", "IsAnonymous", "IsRestricted",
                "FirstSeenAt", "LastSeenAt", "LastScrapedAt", "IsDeleted")
            VALUES (
                {0}, {1}, 0, 0, 0, 0, 1000,
                1, 0, 0, 0, 0, 0,
                '2026-01-01 00:00:00', 0, 0, 0,
                '2026-01-01 00:00:00', '2026-01-01 00:00:00', '2026-01-01 00:00:00', 0);
            """,
            id, title);
}
