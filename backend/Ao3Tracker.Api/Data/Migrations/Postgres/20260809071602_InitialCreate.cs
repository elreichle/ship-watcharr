using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Ao3Tracker.Api.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Ao3Pseuds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PseudName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ao3Pseuds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Ao3Series",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ao3Series", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    IsAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<byte>(type: "smallint", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameNormalized = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Ao3TagId = table.Column<long>(type: "bigint", nullable: true),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Works",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SummaryHtml = table.Column<string>(type: "text", nullable: true),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    Categories = table.Column<int>(type: "integer", nullable: false),
                    Warnings = table.Column<int>(type: "integer", nullable: false),
                    IsComplete = table.Column<bool>(type: "boolean", nullable: false),
                    WordCount = table.Column<int>(type: "integer", nullable: false),
                    ChapterCount = table.Column<int>(type: "integer", nullable: false),
                    PlannedChapterCount = table.Column<int>(type: "integer", nullable: true),
                    Hits = table.Column<int>(type: "integer", nullable: false),
                    Kudos = table.Column<int>(type: "integer", nullable: false),
                    CommentCount = table.Column<int>(type: "integer", nullable: false),
                    Bookmarks = table.Column<int>(type: "integer", nullable: false),
                    CollectionCount = table.Column<int>(type: "integer", nullable: false),
                    LanguageCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    LanguageName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtIsApproximate = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsAnonymous = table.Column<bool>(type: "boolean", nullable: false),
                    IsRestricted = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastScrapedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DetailFetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Works", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Ao3Credentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Ao3Username = table.Column<string>(type: "text", nullable: false),
                    EncryptedPassword = table.Column<string>(type: "text", nullable: false),
                    EncryptedSessionCookie = table.Column<string>(type: "text", nullable: true),
                    SessionEstablishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SessionExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
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

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RoleId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Ships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CanonicalTagName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CanonicalTagNameNormalized = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TagUrlSegment = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Ao3TagId = table.Column<long>(type: "bigint", nullable: true),
                    TagId = table.Column<int>(type: "integer", nullable: true),
                    IncrementalWatermarkUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastIncrementalRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BackfillState = table.Column<byte>(type: "smallint", nullable: false),
                    BackfillNextPage = table.Column<int>(type: "integer", nullable: true),
                    BackfillMinUpdatedAtSeen = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BackfillBeforeUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BackfillStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BackfillCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastKnownTotalWorks = table.Column<int>(type: "integer", nullable: true),
                    LastKnownTotalWorksAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastKnownTotalWasAuthenticated = table.Column<bool>(type: "boolean", nullable: false),
                    LastFullSweepStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFullSweepCompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ships_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UserWorkStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    WorkId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<byte>(type: "smallint", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: true),
                    Note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserWorkStates", x => x.Id);
                    table.CheckConstraint("CK_UserWorkStates_Rating", "\"Rating\" IS NULL OR (\"Rating\" >= 1 AND \"Rating\" <= 10)");
                    table.ForeignKey(
                        name: "FK_UserWorkStates_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserWorkStates_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkAuthors",
                columns: table => new
                {
                    WorkId = table.Column<long>(type: "bigint", nullable: false),
                    PseudId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkAuthors", x => new { x.WorkId, x.PseudId });
                    table.ForeignKey(
                        name: "FK_WorkAuthors_Ao3Pseuds_PseudId",
                        column: x => x.PseudId,
                        principalTable: "Ao3Pseuds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkAuthors_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkDownloadFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkId = table.Column<long>(type: "bigint", nullable: false),
                    Format = table.Column<byte>(type: "smallint", nullable: false),
                    WorkUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RelativePath = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkDownloadFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkDownloadFiles_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkSeries",
                columns: table => new
                {
                    WorkId = table.Column<long>(type: "bigint", nullable: false),
                    SeriesId = table.Column<long>(type: "bigint", nullable: false),
                    Part = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkSeries", x => new { x.WorkId, x.SeriesId });
                    table.ForeignKey(
                        name: "FK_WorkSeries_Ao3Series_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Ao3Series",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkSeries_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkTags",
                columns: table => new
                {
                    WorkId = table.Column<long>(type: "bigint", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTags", x => new { x.WorkId, x.TagId });
                    table.ForeignKey(
                        name: "FK_WorkTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkTags_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScrapeJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShipId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ScraperKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Interval = table.Column<TimeSpan>(type: "interval", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapeJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScrapeJobs_Ships_ShipId",
                        column: x => x.ShipId,
                        principalTable: "Ships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShipWorks",
                columns: table => new
                {
                    ShipId = table.Column<int>(type: "integer", nullable: false),
                    WorkId = table.Column<long>(type: "bigint", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    MissingSinceAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShipWorks", x => new { x.ShipId, x.WorkId });
                    table.ForeignKey(
                        name: "FK_ShipWorks_Ships_ShipId",
                        column: x => x.ShipId,
                        principalTable: "Ships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShipWorks_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WatchedShips",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ShipId = table.Column<int>(type: "integer", nullable: false),
                    DisplayNameOverride = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NotificationsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchedShips", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchedShips_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WatchedShips_Ships_ShipId",
                        column: x => x.ShipId,
                        principalTable: "Ships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Downloads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    WorkId = table.Column<long>(type: "bigint", nullable: false),
                    Format = table.Column<byte>(type: "smallint", nullable: false),
                    WorkDownloadFileId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<byte>(type: "smallint", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Downloads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Downloads_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Downloads_WorkDownloadFiles_WorkDownloadFileId",
                        column: x => x.WorkDownloadFileId,
                        principalTable: "WorkDownloadFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Downloads_Works_WorkId",
                        column: x => x.WorkId,
                        principalTable: "Works",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScrapeRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ScrapeJobId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<byte>(type: "smallint", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HeartbeatAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    PagesFetched = table.Column<int>(type: "integer", nullable: false),
                    RequestsMade = table.Column<int>(type: "integer", nullable: false),
                    WorksSeen = table.Column<int>(type: "integer", nullable: false),
                    WorksAdded = table.Column<int>(type: "integer", nullable: false),
                    WorksUpdated = table.Column<int>(type: "integer", nullable: false),
                    ParseWarnings = table.Column<int>(type: "integer", nullable: false),
                    FirstPageFetched = table.Column<int>(type: "integer", nullable: true),
                    LastPageFetched = table.Column<int>(type: "integer", nullable: true),
                    HitRequestCap = table.Column<bool>(type: "boolean", nullable: false),
                    HitTimeCap = table.Column<bool>(type: "boolean", nullable: false),
                    StopReason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScrapeRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScrapeRuns_ScrapeJobs_ScrapeJobId",
                        column: x => x.ScrapeJobId,
                        principalTable: "ScrapeJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Ao3Credentials_UserId",
                table: "Ao3Credentials",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ao3Pseuds_Username",
                table: "Ao3Pseuds",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_Ao3Pseuds_Username_PseudName",
                table: "Ao3Pseuds",
                columns: new[] { "Username", "PseudName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_UserId_WorkId_Format",
                table: "Downloads",
                columns: new[] { "UserId", "WorkId", "Format" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_WorkDownloadFileId",
                table: "Downloads",
                column: "WorkDownloadFileId");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_WorkId",
                table: "Downloads",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_ScrapeJobs_IsEnabled_NextRunAt",
                table: "ScrapeJobs",
                columns: new[] { "IsEnabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapeJobs_ShipId",
                table: "ScrapeJobs",
                column: "ShipId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScrapeRuns_ScrapeJobId_StartedAt",
                table: "ScrapeRuns",
                columns: new[] { "ScrapeJobId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScrapeRuns_Status",
                table: "ScrapeRuns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Ships_Ao3TagId",
                table: "Ships",
                column: "Ao3TagId");

            migrationBuilder.CreateIndex(
                name: "IX_Ships_CanonicalTagNameNormalized",
                table: "Ships",
                column: "CanonicalTagNameNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ships_TagId",
                table: "Ships",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_ShipWorks_ShipId_LastSeenAt",
                table: "ShipWorks",
                columns: new[] { "ShipId", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ShipWorks_WorkId",
                table: "ShipWorks",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Ao3TagId",
                table: "Tags",
                column: "Ao3TagId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Type_NameNormalized",
                table: "Tags",
                columns: new[] { "Type", "NameNormalized" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserWorkStates_UserId_Rating",
                table: "UserWorkStates",
                columns: new[] { "UserId", "Rating" });

            migrationBuilder.CreateIndex(
                name: "IX_UserWorkStates_UserId_Status",
                table: "UserWorkStates",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_UserWorkStates_UserId_WorkId",
                table: "UserWorkStates",
                columns: new[] { "UserId", "WorkId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserWorkStates_WorkId",
                table: "UserWorkStates",
                column: "WorkId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchedShips_ShipId",
                table: "WatchedShips",
                column: "ShipId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchedShips_UserId_ShipId",
                table: "WatchedShips",
                columns: new[] { "UserId", "ShipId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkAuthors_PseudId",
                table: "WorkAuthors",
                column: "PseudId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkDownloadFiles_WorkId_Format_WorkUpdatedAt",
                table: "WorkDownloadFiles",
                columns: new[] { "WorkId", "Format", "WorkUpdatedAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Works_Bookmarks",
                table: "Works",
                column: "Bookmarks");

            migrationBuilder.CreateIndex(
                name: "IX_Works_CommentCount",
                table: "Works",
                column: "CommentCount");

            migrationBuilder.CreateIndex(
                name: "IX_Works_Hits",
                table: "Works",
                column: "Hits");

            migrationBuilder.CreateIndex(
                name: "IX_Works_IsComplete",
                table: "Works",
                column: "IsComplete");

            migrationBuilder.CreateIndex(
                name: "IX_Works_Kudos",
                table: "Works",
                column: "Kudos");

            migrationBuilder.CreateIndex(
                name: "IX_Works_LanguageCode",
                table: "Works",
                column: "LanguageCode");

            migrationBuilder.CreateIndex(
                name: "IX_Works_LastSeenAt",
                table: "Works",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Works_Rating",
                table: "Works",
                column: "Rating");

            migrationBuilder.CreateIndex(
                name: "IX_Works_UpdatedAt",
                table: "Works",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Works_WordCount",
                table: "Works",
                column: "WordCount");

            migrationBuilder.CreateIndex(
                name: "IX_WorkSeries_SeriesId",
                table: "WorkSeries",
                column: "SeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTags_TagId_WorkId",
                table: "WorkTags",
                columns: new[] { "TagId", "WorkId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Ao3Credentials");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "Downloads");

            migrationBuilder.DropTable(
                name: "ScrapeRuns");

            migrationBuilder.DropTable(
                name: "ShipWorks");

            migrationBuilder.DropTable(
                name: "UserWorkStates");

            migrationBuilder.DropTable(
                name: "WatchedShips");

            migrationBuilder.DropTable(
                name: "WorkAuthors");

            migrationBuilder.DropTable(
                name: "WorkSeries");

            migrationBuilder.DropTable(
                name: "WorkTags");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "WorkDownloadFiles");

            migrationBuilder.DropTable(
                name: "ScrapeJobs");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Ao3Pseuds");

            migrationBuilder.DropTable(
                name: "Ao3Series");

            migrationBuilder.DropTable(
                name: "Works");

            migrationBuilder.DropTable(
                name: "Ships");

            migrationBuilder.DropTable(
                name: "Tags");
        }
    }
}
