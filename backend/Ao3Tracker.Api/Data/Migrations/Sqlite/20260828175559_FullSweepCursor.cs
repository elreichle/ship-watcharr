using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class FullSweepCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FullSweepNextPage",
                table: "Ships",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FullSweepNextPage",
                table: "Ships");
        }
    }
}
