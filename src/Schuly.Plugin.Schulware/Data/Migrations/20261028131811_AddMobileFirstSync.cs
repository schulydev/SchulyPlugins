using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Schuly.Plugin.Schulware.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileFirstSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastSyncDetails",
                table: "SyncStates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HistorySyncedAt",
                table: "Accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MobileApiAvailable",
                table: "Accounts",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSyncDetails",
                table: "SyncStates");

            migrationBuilder.DropColumn(
                name: "HistorySyncedAt",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MobileApiAvailable",
                table: "Accounts");
        }
    }
}
