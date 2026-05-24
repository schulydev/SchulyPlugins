using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Schuly.Plugin.Schulware.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SchoolUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SchulnetzBaseUrl = table.Column<string>(type: "text", nullable: false),
                    SchulwareApiBaseUrl = table.Column<string>(type: "text", nullable: false),
                    SchulnetzStudentId = table.Column<string>(type: "text", nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    MobileAccessToken = table.Column<string>(type: "text", nullable: true),
                    MobileRefreshToken = table.Column<string>(type: "text", nullable: true),
                    MobileTokenExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WebSessionId = table.Column<string>(type: "text", nullable: true),
                    WebSessionUserId = table.Column<string>(type: "text", nullable: true),
                    WebSessionTransId = table.Column<string>(type: "text", nullable: true),
                    ContextStateJson = table.Column<string>(type: "text", nullable: true),
                    UserAgent = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "text", nullable: true),
                    LastSyncError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncStates_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_ApplicationUserId_SchulnetzBaseUrl",
                table: "Accounts",
                columns: new[] { "ApplicationUserId", "SchulnetzBaseUrl" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncStates_AccountId",
                table: "SyncStates",
                column: "AccountId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncStates");

            migrationBuilder.DropTable(
                name: "Accounts");
        }
    }
}
