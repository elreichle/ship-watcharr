using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class InstanceAo3Credential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Ao3InstanceCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Ao3Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EncryptedPassword = table.Column<string>(type: "text", nullable: false),
                    EncryptedSessionCookie = table.Column<string>(type: "text", nullable: true),
                    SessionEstablishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SessionExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ao3InstanceCredentials", x => x.Id);
                    table.CheckConstraint("CK_Ao3InstanceCredentials_SingleRow", "\"Id\" = 1");
                });

            migrationBuilder.Sql(CarryOverExistingCredentialSql);
        }

        /// <summary>
        /// Adopts an already-saved per-user credential as the instance account, copying the password
        /// as ciphertext. Both stores share one Data Protection purpose string (see
        /// <c>Ao3InstanceCredentialStore.Purpose</c>), so the blob is moved without being decrypted:
        /// no plaintext password is handled here, and nobody has to re-enter one they already saved.
        ///
        /// The admin's credential wins where several exist, oldest otherwise. A fresh install has
        /// nothing to copy and this does nothing.
        ///
        /// Byte-identical to the SQLite migration's copy, and duplicated rather than shared on
        /// purpose: the two histories are independent records of what has already been applied, so
        /// neither may be able to rewrite the other by being edited later. The CASE over IsAdmin
        /// needs no <c>= TRUE</c>, which is what would otherwise have differed between SQLite's
        /// integer boolean and this provider's real one.
        /// </summary>
        private const string CarryOverExistingCredentialSql = """
            INSERT INTO "Ao3InstanceCredentials"
                ("Id", "Ao3Username", "EncryptedPassword", "EncryptedSessionCookie",
                 "SessionEstablishedAt", "SessionExpiresAt", "CreatedAt", "UpdatedAt")
            SELECT 1, c."Ao3Username", c."EncryptedPassword", c."EncryptedSessionCookie",
                   c."SessionEstablishedAt", c."SessionExpiresAt", c."CreatedAt", c."UpdatedAt"
            FROM "Ao3Credentials" c
            JOIN "AspNetUsers" u ON u."Id" = c."UserId"
            ORDER BY CASE WHEN u."IsAdmin" THEN 0 ELSE 1 END, c."CreatedAt"
            LIMIT 1;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Ao3InstanceCredentials");
        }
    }
}
