using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Postgres
{
    /// <summary>
    /// Drops the retired per-user AO3 credential table.
    ///
    /// Safe to drop: the earlier InstanceAo3Credential migration already carried the row an
    /// operator was most likely to have — the admin's — across as ciphertext, both stores sharing
    /// the protector purpose "Ao3Tracker.Ao3Credentials.v1", so nobody has to type a password
    /// again. A login is now entered in exactly one place, under System → Scraping.
    ///
    /// Down recreates the table but not its rows. There is nothing to put back: the passwords in it
    /// were encrypted with a key ring this migration does not touch, but the rows themselves are
    /// gone once this runs.
    /// </summary>
    public partial class RetirePerUserAo3Credential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Ao3Credentials");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Ao3Credentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Ao3Username = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EncryptedPassword = table.Column<string>(type: "text", nullable: false),
                    EncryptedSessionCookie = table.Column<string>(type: "text", nullable: true),
                    SessionEstablishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SessionExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ao3Credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ao3Credentials_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ao3Credentials_UserId",
                table: "Ao3Credentials",
                column: "UserId",
                unique: true);
        }
    }
}
