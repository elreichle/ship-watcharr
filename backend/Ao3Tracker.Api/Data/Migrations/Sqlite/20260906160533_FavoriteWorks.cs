using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class FavoriteWorks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FavoritedAt",
                table: "UserWorkStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserWorkStates_UserId_FavoritedAt",
                table: "UserWorkStates",
                columns: new[] { "UserId", "FavoritedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserWorkStates_UserId_FavoritedAt",
                table: "UserWorkStates");

            migrationBuilder.DropColumn(
                name: "FavoritedAt",
                table: "UserWorkStates");
        }
    }
}
