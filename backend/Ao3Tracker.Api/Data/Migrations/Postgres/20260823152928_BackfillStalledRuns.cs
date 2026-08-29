using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class BackfillStalledRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BackfillStalledRuns",
                table: "Ships",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackfillStalledRuns",
                table: "Ships");
        }
    }
}
