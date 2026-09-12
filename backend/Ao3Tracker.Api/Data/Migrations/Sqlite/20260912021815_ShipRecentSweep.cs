using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ShipRecentSweep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastRecentSweepCompletedAt",
                table: "Ships",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastRecentSweepStartedAt",
                table: "Ships",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecentSweepFrom",
                table: "Ships",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecentSweepNextPage",
                table: "Ships",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRecentSweepCompletedAt",
                table: "Ships");

            migrationBuilder.DropColumn(
                name: "LastRecentSweepStartedAt",
                table: "Ships");

            migrationBuilder.DropColumn(
                name: "RecentSweepFrom",
                table: "Ships");

            migrationBuilder.DropColumn(
                name: "RecentSweepNextPage",
                table: "Ships");
        }
    }
}
