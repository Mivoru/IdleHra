using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddLarderAndAutoEatThreshold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutoEatThresholdPct",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LarderSlot1Count",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LarderSlot1ItemId",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LarderSlot2Count",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LarderSlot2ItemId",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LarderSlot3Count",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LarderSlot3ItemId",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoEatThresholdPct",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "LarderSlot1Count",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "LarderSlot1ItemId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "LarderSlot2Count",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "LarderSlot2ItemId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "LarderSlot3Count",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "LarderSlot3ItemId",
                table: "PlayerRecords");
        }
    }
}
