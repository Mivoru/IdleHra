using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailPasswordAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "PlayerRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "PlayerRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "PlayerRecords",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRecords_Email",
                table: "PlayerRecords",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRecords_Username",
                table: "PlayerRecords",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlayerRecords_Email",
                table: "PlayerRecords");

            migrationBuilder.DropIndex(
                name: "IX_PlayerRecords_Username",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "Username",
                table: "PlayerRecords");
        }
    }
}
