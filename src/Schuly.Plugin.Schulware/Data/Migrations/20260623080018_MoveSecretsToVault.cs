using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Schuly.Plugin.Schulware.Data.Migrations
{
    /// <inheritdoc />
    public partial class MoveSecretsToVault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContextStateJson",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MobileAccessToken",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MobileRefreshToken",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "WebSessionId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "WebSessionTransId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "WebSessionUserId",
                table: "Accounts");

            migrationBuilder.AddColumn<bool>(
                name: "AutoRefresh",
                table: "Accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoRefresh",
                table: "Accounts");

            migrationBuilder.AddColumn<string>(
                name: "ContextStateJson",
                table: "Accounts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MobileAccessToken",
                table: "Accounts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MobileRefreshToken",
                table: "Accounts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                table: "Accounts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebSessionId",
                table: "Accounts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebSessionTransId",
                table: "Accounts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebSessionUserId",
                table: "Accounts",
                type: "text",
                nullable: true);
        }
    }
}
