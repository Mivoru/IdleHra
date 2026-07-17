using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddConsumableExpiryAndFoodBuff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ActiveDefensivePotionExpiresEpoch",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ActiveFoodExpiresEpoch",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ActiveFoodId",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ActiveOffensivePotionExpiresEpoch",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActiveDefensivePotionExpiresEpoch",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "ActiveFoodExpiresEpoch",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "ActiveFoodId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "ActiveOffensivePotionExpiresEpoch",
                table: "PlayerRecords");
        }
    }
}
