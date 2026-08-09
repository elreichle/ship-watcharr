using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ShipVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RequestedTagName",
                table: "WatchedShips",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextVerificationAttemptAt",
                table: "Ships",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VerificationAttempts",
                table: "Ships",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerificationCheckedAt",
                table: "Ships",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationError",
                table: "Ships",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "VerificationState",
                table: "Ships",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.CreateIndex(
                name: "IX_Ships_VerificationState_NextVerificationAttemptAt",
                table: "Ships",
                columns: new[] { "VerificationState", "NextVerificationAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Ships_VerificationState_NextVerificationAttemptAt",
                table: "Ships");

            migrationBuilder.DropColumn(
                name: "RequestedTagName",
                table: "WatchedShips");

            migrationBuilder.DropColumn(
                name: "NextVerificationAttemptAt",
                table: "Ships");

            migrationBuilder.DropColumn(
                name: "VerificationAttempts",
                table: "Ships");

            migrationBuilder.DropColumn(
                name: "VerificationCheckedAt",
                table: "Ships");

            migrationBuilder.DropColumn(
                name: "VerificationError",
                table: "Ships");

            migrationBuilder.DropColumn(
                name: "VerificationState",
                table: "Ships");
        }
    }
}
