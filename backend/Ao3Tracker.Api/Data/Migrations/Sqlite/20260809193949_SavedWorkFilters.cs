using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class SavedWorkFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayNameNormalized",
                table: "Ao3Pseuds",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            // Hand-added: the default above would leave any existing pseud unsearchable. No rows
            // exist today (nothing writes pseuds until the AO3 parser lands), so this is insurance
            // rather than a real backfill — SQLite's UPPER is ASCII-only and would not exactly match
            // ToUpperInvariant on a non-ASCII byline.
            migrationBuilder.Sql(
                "UPDATE \"Ao3Pseuds\" SET \"DisplayNameNormalized\" = UPPER(\"DisplayName\");");

            migrationBuilder.CreateTable(
                name: "SavedWorkFilters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShipId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsComplete = table.Column<bool>(type: "INTEGER", nullable: true),
                    MinWordCount = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxWordCount = table.Column<int>(type: "INTEGER", nullable: true),
                    MinChapterCount = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxChapterCount = table.Column<int>(type: "INTEGER", nullable: true),
                    MinKudos = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxKudos = table.Column<int>(type: "INTEGER", nullable: true),
                    MinHits = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxHits = table.Column<int>(type: "INTEGER", nullable: true),
                    MinComments = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxComments = table.Column<int>(type: "INTEGER", nullable: true),
                    MinBookmarks = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxBookmarks = table.Column<int>(type: "INTEGER", nullable: true),
                    MinRating = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxRating = table.Column<int>(type: "INTEGER", nullable: true),
                    IncludeCategories = table.Column<int>(type: "INTEGER", nullable: true),
                    ExcludeCategories = table.Column<int>(type: "INTEGER", nullable: true),
                    IncludeWarnings = table.Column<int>(type: "INTEGER", nullable: true),
                    ExcludeWarnings = table.Column<int>(type: "INTEGER", nullable: true),
                    LanguageCode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    UpdatedAfter = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBefore = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Sort = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Ascending = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedWorkFilters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedWorkFilters_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SavedWorkFilters_Ships_ShipId",
                        column: x => x.ShipId,
                        principalTable: "Ships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SavedWorkFilterAuthors",
                columns: table => new
                {
                    SavedWorkFilterId = table.Column<int>(type: "INTEGER", nullable: false),
                    PseudId = table.Column<int>(type: "INTEGER", nullable: false),
                    Exclude = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedWorkFilterAuthors", x => new { x.SavedWorkFilterId, x.PseudId });
                    table.ForeignKey(
                        name: "FK_SavedWorkFilterAuthors_Ao3Pseuds_PseudId",
                        column: x => x.PseudId,
                        principalTable: "Ao3Pseuds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SavedWorkFilterAuthors_SavedWorkFilters_SavedWorkFilterId",
                        column: x => x.SavedWorkFilterId,
                        principalTable: "SavedWorkFilters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SavedWorkFilterTags",
                columns: table => new
                {
                    SavedWorkFilterId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    Exclude = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedWorkFilterTags", x => new { x.SavedWorkFilterId, x.TagId });
                    table.ForeignKey(
                        name: "FK_SavedWorkFilterTags_SavedWorkFilters_SavedWorkFilterId",
                        column: x => x.SavedWorkFilterId,
                        principalTable: "SavedWorkFilters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SavedWorkFilterTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ao3Pseuds_DisplayNameNormalized",
                table: "Ao3Pseuds",
                column: "DisplayNameNormalized");

            migrationBuilder.CreateIndex(
                name: "IX_SavedWorkFilterAuthors_PseudId",
                table: "SavedWorkFilterAuthors",
                column: "PseudId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedWorkFilters_ShipId",
                table: "SavedWorkFilters",
                column: "ShipId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedWorkFilters_UserId_IsDefault",
                table: "SavedWorkFilters",
                columns: new[] { "UserId", "IsDefault" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedWorkFilters_UserId_Name",
                table: "SavedWorkFilters",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedWorkFilterTags_TagId",
                table: "SavedWorkFilterTags",
                column: "TagId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedWorkFilterAuthors");

            migrationBuilder.DropTable(
                name: "SavedWorkFilterTags");

            migrationBuilder.DropTable(
                name: "SavedWorkFilters");

            migrationBuilder.DropIndex(
                name: "IX_Ao3Pseuds_DisplayNameNormalized",
                table: "Ao3Pseuds");

            migrationBuilder.DropColumn(
                name: "DisplayNameNormalized",
                table: "Ao3Pseuds");
        }
    }
}
