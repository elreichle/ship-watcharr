using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Postgres
{
    /// <summary>
    /// Gives a pseud a normalized identity, so one AO3 creator is one row whatever case their
    /// byline was rendered in.
    ///
    /// The unique key moves from (Username, PseudName) to their uppercased forms. Existing rows are
    /// backfilled, and any that were already split across two capitalisations are merged into the
    /// lowest id — their work links repointed, and links that would then collide with an existing
    /// one dropped rather than duplicated.
    ///
    /// One documented limitation: the backfill uses SQL <c>UPPER</c>, which on SQLite folds ASCII
    /// only, while the application normalizes with <c>ToUpperInvariant</c>. AO3 usernames are
    /// ASCII, but pseud names need not be — so on SQLite a pre-existing pseud with a non-ASCII name
    /// can keep a normalized form the app would spell differently. The consequence is a second row
    /// for that pseud the next time it is seen, not a failed save; nothing is lost, and re-scraping
    /// does not compound it.
    /// </summary>
    public partial class NormalizedPseudIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Ao3Pseuds_Username",
                table: "Ao3Pseuds");

            migrationBuilder.DropIndex(
                name: "IX_Ao3Pseuds_Username_PseudName",
                table: "Ao3Pseuds");

            migrationBuilder.AddColumn<string>(
                name: "PseudNameNormalized",
                table: "Ao3Pseuds",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "UsernameNormalized",
                table: "Ao3Pseuds",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            // Backfill before the unique index exists — the index is over these columns, and they
            // are empty strings until this runs.
            migrationBuilder.Sql("""
                UPDATE "Ao3Pseuds"
                SET "UsernameNormalized" = UPPER("Username"),
                    "PseudNameNormalized" = UPPER("PseudName");
                """);

            // A work linked to both capitalisations of one creator would end up linked twice to the
            // survivor, which its own primary key forbids. Drop the loser link first.
            migrationBuilder.Sql("""
                DELETE FROM "WorkAuthors"
                WHERE EXISTS (
                    SELECT 1 FROM "WorkAuthors" other
                    WHERE other."WorkId" = "WorkAuthors"."WorkId"
                      AND other."PseudId" <> "WorkAuthors"."PseudId"
                      AND other."PseudId" = (
                          SELECT MIN(keep."Id") FROM "Ao3Pseuds" keep
                          JOIN "Ao3Pseuds" dup
                            ON keep."UsernameNormalized" = dup."UsernameNormalized"
                           AND keep."PseudNameNormalized" = dup."PseudNameNormalized"
                          WHERE dup."Id" = "WorkAuthors"."PseudId"));
                """);

            migrationBuilder.Sql("""
                UPDATE "WorkAuthors"
                SET "PseudId" = (
                    SELECT MIN(keep."Id") FROM "Ao3Pseuds" keep
                    JOIN "Ao3Pseuds" dup
                      ON keep."UsernameNormalized" = dup."UsernameNormalized"
                     AND keep."PseudNameNormalized" = dup."PseudNameNormalized"
                    WHERE dup."Id" = "WorkAuthors"."PseudId")
                WHERE "PseudId" <> (
                    SELECT MIN(keep."Id") FROM "Ao3Pseuds" keep
                    JOIN "Ao3Pseuds" dup
                      ON keep."UsernameNormalized" = dup."UsernameNormalized"
                     AND keep."PseudNameNormalized" = dup."PseudNameNormalized"
                    WHERE dup."Id" = "WorkAuthors"."PseudId");
                """);

            migrationBuilder.Sql("""
                DELETE FROM "Ao3Pseuds"
                WHERE "Id" <> (
                    SELECT MIN(keep."Id") FROM "Ao3Pseuds" keep
                    WHERE keep."UsernameNormalized" = "Ao3Pseuds"."UsernameNormalized"
                      AND keep."PseudNameNormalized" = "Ao3Pseuds"."PseudNameNormalized");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Ao3Pseuds_UsernameNormalized",
                table: "Ao3Pseuds",
                column: "UsernameNormalized");

            migrationBuilder.CreateIndex(
                name: "IX_Ao3Pseuds_UsernameNormalized_PseudNameNormalized",
                table: "Ao3Pseuds",
                columns: new[] { "UsernameNormalized", "PseudNameNormalized" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Ao3Pseuds_UsernameNormalized",
                table: "Ao3Pseuds");

            migrationBuilder.DropIndex(
                name: "IX_Ao3Pseuds_UsernameNormalized_PseudNameNormalized",
                table: "Ao3Pseuds");

            migrationBuilder.DropColumn(
                name: "PseudNameNormalized",
                table: "Ao3Pseuds");

            migrationBuilder.DropColumn(
                name: "UsernameNormalized",
                table: "Ao3Pseuds");

            migrationBuilder.CreateIndex(
                name: "IX_Ao3Pseuds_Username",
                table: "Ao3Pseuds",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_Ao3Pseuds_Username_PseudName",
                table: "Ao3Pseuds",
                columns: new[] { "Username", "PseudName" },
                unique: true);
        }
    }
}
