using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class WholeListingCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // True on every ship that exists already, which is the only honest value for them: no
            // backfill before this recorded the session its pages were read with, and on the first
            // production instance most of them were read logged out. A backfill beginning after this
            // clears it for itself (Ao3ShipIndexScraper.BeginBackfill), and a new ship is inserted
            // with the model's false, so the default only ever lands on these rows.
            migrationBuilder.AddColumn<bool>(
                name: "BackfillReadAnonymously",
                table: "Ships",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WholeListingReadLoggedInAt",
                table: "Ships",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackfillReadAnonymously",
                table: "Ships");

            migrationBuilder.DropColumn(
                name: "WholeListingReadLoggedInAt",
                table: "Ships");
        }
    }
}
