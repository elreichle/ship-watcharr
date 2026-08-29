using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class PreviousDownloadCopy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PreviousWorkDownloadFileId",
                table: "Downloads",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_PreviousWorkDownloadFileId",
                table: "Downloads",
                column: "PreviousWorkDownloadFileId");

            migrationBuilder.AddForeignKey(
                name: "FK_Downloads_WorkDownloadFiles_PreviousWorkDownloadFileId",
                table: "Downloads",
                column: "PreviousWorkDownloadFileId",
                principalTable: "WorkDownloadFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Downloads_WorkDownloadFiles_PreviousWorkDownloadFileId",
                table: "Downloads");

            migrationBuilder.DropIndex(
                name: "IX_Downloads_PreviousWorkDownloadFileId",
                table: "Downloads");

            migrationBuilder.DropColumn(
                name: "PreviousWorkDownloadFileId",
                table: "Downloads");
        }
    }
}
