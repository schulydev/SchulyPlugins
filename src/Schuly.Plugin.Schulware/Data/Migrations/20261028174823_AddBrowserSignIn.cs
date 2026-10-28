using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Schuly.Plugin.Schulware.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBrowserSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceId",
                table: "Accounts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "Accounts");
        }
    }
}
