using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class ReadingCriteriaOnSavedFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxUserRating",
                table: "SavedWorkFilters",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinUserRating",
                table: "SavedWorkFilters",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "ReadingStatus",
                table: "SavedWorkFilters",
                type: "smallint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxUserRating",
                table: "SavedWorkFilters");

            migrationBuilder.DropColumn(
                name: "MinUserRating",
                table: "SavedWorkFilters");

            migrationBuilder.DropColumn(
                name: "ReadingStatus",
                table: "SavedWorkFilters");
        }
    }
}
