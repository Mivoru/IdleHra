using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPotionShelf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ActiveGatheringPotionExpiresEpoch",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ActiveGatheringPotionId",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte>(
                name: "PotionAutoDrinkMask",
                table: "PlayerRecords",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<int>(
                name: "PotionShelf1Count",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PotionShelf1ItemId",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PotionShelf2Count",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PotionShelf2ItemId",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PotionShelf3Count",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PotionShelf3ItemId",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveGatheringPotionExpiresEpoch",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "ActiveGatheringPotionId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "PotionAutoDrinkMask",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "PotionShelf1Count",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "PotionShelf1ItemId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "PotionShelf2Count",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "PotionShelf2ItemId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "PotionShelf3Count",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "PotionShelf3ItemId",
                table: "PlayerRecords");
        }
    }
}
