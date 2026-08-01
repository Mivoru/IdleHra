using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddGatheringMasteryPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MiningMasteryLevel",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MiningMasteryXp",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WoodcuttingMasteryLevel",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WoodcuttingMasteryXp",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MiningMasteryLevel",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "MiningMasteryXp",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "WoodcuttingMasteryLevel",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "WoodcuttingMasteryXp",
                table: "PlayerRecords");
        }
    }
}
