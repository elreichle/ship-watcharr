using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class WorkVersionObservedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAtObservedAt",
                table: "Works",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpdatedAtObservedAt",
                table: "Works");
        }
    }
}
