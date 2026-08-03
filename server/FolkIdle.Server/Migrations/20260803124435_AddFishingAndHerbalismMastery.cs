using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddFishingAndHerbalismMastery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FishingMasteryLevel",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FishingMasteryXp",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HerbalismMasteryLevel",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HerbalismMasteryXp",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FishingMasteryLevel",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "FishingMasteryXp",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "HerbalismMasteryLevel",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "HerbalismMasteryXp",
                table: "PlayerRecords");
        }
    }
}
