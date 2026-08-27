using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Postgres
{
    /// <summary>
    /// Gives a pseud a normalized identity, so one AO3 creator is one row whatever case their
    /// byline was rendered in.
    ///
    /// The unique key moves from (Username, PseudName) to their uppercased forms. Existing rows are
    /// backfilled, and any that were already split across capitalisations are merged into the
    /// lowest id of the group — each work's link set first thinned to one link per creator, then
    /// what is left repointed at the survivor. However many spellings a work is linked to, it comes
    /// out of this linked to that creator exactly once.
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

            // A work linked to more than one capitalisation of one creator would end up linked to
            // the survivor once per spelling, which its own primary key forbids. Thin each work's
            // link set down to one link per creator first, keeping the lowest pseud id of the
            // group — which is the id the repoint below moves the survivor onto anyway.
            //
            // The rule is deliberately "a lower-id link on the same work in the same normalized
            // group wins", not "a link to the group's canonical row already exists". The latter
            // drops nothing at all when a work is linked to the second and third spelling but not
            // the first, and the repoint then collides two links onto one row mid-upgrade.
            migrationBuilder.Sql("""
                DELETE FROM "WorkAuthors"
                WHERE EXISTS (
                    SELECT 1 FROM "WorkAuthors" other
                    JOIN "Ao3Pseuds" theirs ON theirs."Id" = other."PseudId"
                    JOIN "Ao3Pseuds" mine
                      ON mine."UsernameNormalized" = theirs."UsernameNormalized"
                     AND mine."PseudNameNormalized" = theirs."PseudNameNormalized"
                    WHERE other."WorkId" = "WorkAuthors"."WorkId"
                      AND other."PseudId" < "WorkAuthors"."PseudId"
                      AND mine."Id" = "WorkAuthors"."PseudId");
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
