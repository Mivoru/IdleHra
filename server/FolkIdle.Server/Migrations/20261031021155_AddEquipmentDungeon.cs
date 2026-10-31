using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddEquipmentDungeon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DungeonBestStage",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DungeonFirstClearMask",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DungeonTicketDay",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DungeonTickets",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DungeonBestStage",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "DungeonFirstClearMask",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "DungeonTicketDay",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "DungeonTickets",
                table: "PlayerRecords");
        }
    }
}
