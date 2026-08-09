using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Postgres
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
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextVerificationAttemptAt",
                table: "Ships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VerificationAttempts",
                table: "Ships",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerificationCheckedAt",
                table: "Ships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerificationError",
                table: "Ships",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "VerificationState",
                table: "Ships",
                type: "smallint",
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
