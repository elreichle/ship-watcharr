using Ao3Tracker.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Ao3Tracker.Tests;

/// <summary>
/// The <c>NormalizedPseudIdentity</c> upgrade, run against a database that already holds rows.
///
/// Everything else in the suite starts from <c>EnsureCreated</c>, which builds the current schema
/// and never executes a migration — so the SQL inside one has no other test seam. These tests
/// migrate to the revision immediately before it, write the rows by hand, and then run the one
/// migration under test, which is the only way to watch it merge duplicates rather than trust that
/// it would.
///
/// The merge is the part that can fail: the upgrade moves the unique key onto the uppercased pair,
/// so every capitalisation of one creator has to collapse onto a single row, and every work linked
/// to more than one of them has to end up linked to the survivor exactly once. Two links repointed
/// onto the same survivor collide on <c>PK_WorkAuthors</c> and take the whole upgrade down.
///
/// A saved filter's author criteria are the second thing pointing at a pseud, and they fail the
/// other way round: <c>SavedWorkFilterAuthors.PseudId</c> cascades on delete, so a criterion naming
/// a losing spelling is taken out with it silently — an upgrade that succeeds and quietly stops
/// narrowing to the creator it was saved for. They need the same thin-then-repoint treatment.
/// </summary>
public class PseudMigrationTests : IDisposable
{
    private const string Before = "20260822182050_RetirePerUserAo3Credential";
    private const string Under = "20260822182752_NormalizedPseudIdentity";

    // An in-memory SQLite database lives only as long as a connection to it, so this one is held
    // open across the two migrations and the seed between them.
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly IMigrator _migrator;

    public PseudMigrationTests()
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
    public void Three_capitalisations_of_one_creator_collapse_onto_one_row()
    {
        // The case the two-variant merge was written for and does not cover: a work linked to the
        // second and third spelling but not the first. Neither link is a duplicate of an existing
        // link to the survivor, so a merge that only drops links already sitting beside the
        // survivor drops nothing — and then repoints both onto id 1.
        SeedPseud(1, "someuser", "somepseud");
        SeedPseud(2, "SomeUser", "SomePseud");
        SeedPseud(3, "SOMEUSER", "SOMEPSEUD");
        SeedWork(100);
        SeedAuthorLink(100, 2, position: 0);
        SeedAuthorLink(100, 3, position: 1);

        _migrator.Migrate(Under);

        Assert.Equal([1], PseudIds());
        Assert.Equal([(100L, 1)], AuthorLinks());
    }

    [Fact]
    public void A_work_linked_to_both_the_survivor_and_a_duplicate_keeps_one_link()
    {
        // The two-variant case, which worked before and has to keep working: the loser's link is a
        // duplicate of one the work already has, so it is dropped rather than repointed.
        SeedPseud(1, "someuser", "somepseud");
        SeedPseud(2, "SomeUser", "SomePseud");
        SeedWork(100);
        SeedAuthorLink(100, 1, position: 0);
        SeedAuthorLink(100, 2, position: 1);

        _migrator.Migrate(Under);

        Assert.Equal([1], PseudIds());
        Assert.Equal([(100L, 1)], AuthorLinks());
    }

    [Fact]
    public void Two_different_creators_on_one_work_both_survive()
    {
        // The over-deletion this merge must not commit. Co-authors share a work and nothing else;
        // dropping a link merely because another link sits beside it would erase half of every
        // collaboration in the library.
        SeedPseud(1, "someuser", "somepseud");
        SeedPseud(2, "SomeUser", "SomePseud");
        SeedPseud(3, "otheruser", "otherpseud");
        SeedWork(100);
        SeedAuthorLink(100, 2, position: 0);
        SeedAuthorLink(100, 3, position: 1);

        _migrator.Migrate(Under);

        Assert.Equal([1, 3], PseudIds());
        Assert.Equal([(100L, 1), (100L, 3)], AuthorLinks());
    }

    [Fact]
    public void Two_works_by_the_same_duplicated_creator_are_both_repointed()
    {
        // The merge is per work, not per creator: each work's link set is deduplicated on its own,
        // and a creator's other works have to follow them onto the survivor all the same.
        SeedPseud(1, "someuser", "somepseud");
        SeedPseud(2, "SomeUser", "SomePseud");
        SeedPseud(3, "SOMEUSER", "SOMEPSEUD");
        SeedWork(100);
        SeedWork(101);
        SeedAuthorLink(100, 2, position: 0);
        SeedAuthorLink(100, 3, position: 1);
        SeedAuthorLink(101, 3, position: 0);

        _migrator.Migrate(Under);

        Assert.Equal([1], PseudIds());
        Assert.Equal([(100L, 1), (101L, 1)], AuthorLinks());
    }

    [Fact]
    public void A_saved_filters_author_criterion_follows_the_creator_onto_the_survivor()
    {
        // The cascade this task exists for. `SavedWorkFilterAuthors.PseudId` is declared
        // `onDelete: Cascade`, so a criterion naming the loser is deleted with it rather than
        // moved — the upgrade reports success and the filter silently stops naming anyone.
        SeedPseud(1, "someuser", "somepseud");
        SeedPseud(2, "SomeUser", "SomePseud");
        SeedUser("user-1");
        SeedFilter(10, "user-1");
        SeedFilterAuthor(10, 2, exclude: false);

        _migrator.Migrate(Under);

        Assert.Equal([1], PseudIds());
        Assert.Equal([(10, 1, false)], FilterAuthors());
    }

    [Fact]
    public void Two_spellings_of_one_creator_on_one_filter_collapse_onto_one_criterion()
    {
        // `PK_SavedWorkFilterAuthors (SavedWorkFilterId, PseudId)` collides exactly the way
        // `PK_WorkAuthors` does, so repointing without thinning first would swap the silent loss
        // for a failed upgrade.
        SeedPseud(1, "someuser", "somepseud");
        SeedPseud(2, "SomeUser", "SomePseud");
        SeedPseud(3, "SOMEUSER", "SOMEPSEUD");
        SeedUser("user-1");
        SeedFilter(10, "user-1");
        SeedFilterAuthor(10, 2, exclude: false);
        SeedFilterAuthor(10, 3, exclude: false);

        _migrator.Migrate(Under);

        Assert.Equal([1], PseudIds());
        Assert.Equal([(10, 1, false)], FilterAuthors());
    }

    [Fact]
    public void An_include_and_an_exclude_of_one_creator_merge_as_an_include()
    {
        // Two spellings can be named in opposite directions, which the API forbids for one pseud
        // id but cannot forbid across ids it does not know are the same creator. The include is
        // what makes the filter narrow, and the pre-merge answer was always some of that
        // creator's works — so the include survives, and it survives from the *higher* id here to
        // show the rule is the direction and not the id order that thins the links.
        SeedPseud(1, "someuser", "somepseud");
        SeedPseud(2, "SomeUser", "SomePseud");
        SeedUser("user-1");
        SeedFilter(10, "user-1");
        SeedFilterAuthor(10, 1, exclude: true);
        SeedFilterAuthor(10, 2, exclude: false);

        _migrator.Migrate(Under);

        Assert.Equal([1], PseudIds());
        Assert.Equal([(10, 1, false)], FilterAuthors());
    }

    [Fact]
    public void An_exclusion_of_a_losing_spelling_is_still_an_exclusion()
    {
        // Nothing to merge with: a lone exclude is repointed like any other criterion, and a
        // filter that hid a creator keeps hiding them.
        SeedPseud(1, "someuser", "somepseud");
        SeedPseud(2, "SomeUser", "SomePseud");
        SeedUser("user-1");
        SeedFilter(10, "user-1");
        SeedFilterAuthor(10, 2, exclude: true);

        _migrator.Migrate(Under);

        Assert.Equal([(10, 1, true)], FilterAuthors());
    }

    [Fact]
    public void Criteria_are_thinned_per_filter_and_only_within_one_creator()
    {
        // The two over-reaches the thinning must not commit: a second creator on the same filter
        // is not a duplicate of the first, and a second filter naming the same creator belongs to
        // its own owner and keeps its own criterion.
        SeedPseud(1, "someuser", "somepseud");
        SeedPseud(2, "SomeUser", "SomePseud");
        SeedPseud(3, "otheruser", "otherpseud");
        SeedUser("user-1");
        SeedUser("user-2");
        SeedFilter(10, "user-1");
        SeedFilter(11, "user-2");
        SeedFilterAuthor(10, 2, exclude: false);
        SeedFilterAuthor(10, 3, exclude: true);
        SeedFilterAuthor(11, 2, exclude: false);

        _migrator.Migrate(Under);

        Assert.Equal([1, 3], PseudIds());
        Assert.Equal([(10, 1, false), (10, 3, true), (11, 1, false)], FilterAuthors());
    }

    private void SeedPseud(int id, string username, string pseudName) =>
        _db.Database.ExecuteSqlRaw(
            """
            INSERT INTO "Ao3Pseuds" ("Id", "Username", "PseudName", "DisplayName", "FirstSeenAt")
            VALUES ({0}, {1}, {2}, {3}, '2026-01-01 00:00:00');
            """,
            id, username, pseudName, $"{username} ({pseudName})");

    private void SeedWork(long id) =>
        _db.Database.ExecuteSqlRaw(
            """
            INSERT INTO "Works" (
                "Id", "Title", "Rating", "Categories", "Warnings", "IsComplete", "WordCount",
                "ChapterCount", "Hits", "Kudos", "CommentCount", "Bookmarks", "CollectionCount",
                "UpdatedAt", "UpdatedAtIsApproximate", "IsAnonymous", "IsRestricted",
                "FirstSeenAt", "LastSeenAt", "LastScrapedAt", "IsDeleted")
            VALUES (
                {0}, 'A work', 0, 0, 0, 0, 1000,
                1, 0, 0, 0, 0, 0,
                '2026-01-01 00:00:00', 0, 0, 0,
                '2026-01-01 00:00:00', '2026-01-01 00:00:00', '2026-01-01 00:00:00', 0);
            """,
            id);

    private void SeedAuthorLink(long workId, int pseudId, int position) =>
        _db.Database.ExecuteSqlRaw(
            """
            INSERT INTO "WorkAuthors" ("WorkId", "PseudId", "Position") VALUES ({0}, {1}, {2});
            """,
            workId, pseudId, position);

    private void SeedUser(string id) =>
        _db.Database.ExecuteSqlRaw(
            """
            INSERT INTO "AspNetUsers" (
                "Id", "IsAdmin", "UserName", "EmailConfirmed", "PhoneNumberConfirmed",
                "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ({0}, 0, {0}, 0, 0, 0, 0, 0);
            """,
            id);

    private void SeedFilter(int id, string userId) =>
        _db.Database.ExecuteSqlRaw(
            """
            INSERT INTO "SavedWorkFilters" (
                "Id", "UserId", "Name", "IsDefault", "Sort", "Ascending", "CreatedAt", "UpdatedAt")
            VALUES ({0}, {1}, 'A filter', 0, 'updated', 0,
                    '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            """,
            id, userId);

    private void SeedFilterAuthor(int filterId, int pseudId, bool exclude) =>
        _db.Database.ExecuteSqlRaw(
            """
            INSERT INTO "SavedWorkFilterAuthors" ("SavedWorkFilterId", "PseudId", "Exclude")
            VALUES ({0}, {1}, {2});
            """,
            filterId, pseudId, exclude ? 1 : 0);

    private List<int> PseudIds() => Query(
        """SELECT "Id" FROM "Ao3Pseuds" ORDER BY "Id";""",
        reader => reader.GetInt32(0));

    private List<(int FilterId, int PseudId, bool Exclude)> FilterAuthors() => Query(
        """
        SELECT "SavedWorkFilterId", "PseudId", "Exclude" FROM "SavedWorkFilterAuthors"
        ORDER BY "SavedWorkFilterId", "PseudId";
        """,
        reader => (reader.GetInt32(0), reader.GetInt32(1), reader.GetBoolean(2)));

    private List<(long WorkId, int PseudId)> AuthorLinks() => Query(
        """SELECT "WorkId", "PseudId" FROM "WorkAuthors" ORDER BY "WorkId", "PseudId";""",
        reader => (reader.GetInt64(0), reader.GetInt32(1)));

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }
}
