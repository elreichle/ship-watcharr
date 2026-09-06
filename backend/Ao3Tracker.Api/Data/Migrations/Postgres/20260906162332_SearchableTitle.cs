using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Postgres
{
    /// <summary>
    /// Gives a work an uppercased copy of its title for the works list to search through, so a
    /// title search answers the same on SQLite and PostgreSQL — see <c>Work.TitleNormalized</c>.
    ///
    /// Existing rows are backfilled with SQL <c>UPPER</c>, which on SQLite folds ASCII only, while
    /// the application normalizes with <c>ToUpperInvariant</c>. So on SQLite a title with a
    /// non-ASCII letter can carry a normalized form the app would spell differently until the work
    /// is next scraped — the same documented limitation as the pseud backfill. The consequence is
    /// a search on that letter missing the work in the meantime, not a failed save, and the next
    /// listing pass rewrites the title and puts it right.
    /// </summary>
    public partial class SearchableTitle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TitleNormalized",
                table: "Works",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE "Works" SET "TitleNormalized" = UPPER("Title");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TitleNormalized",
                table: "Works");
        }
    }
}
