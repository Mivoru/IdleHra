using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkshopCommissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChosenAffixId",
                table: "PlayerCraftingSlots",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "FloorTier",
                table: "PlayerCraftingSlots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "StartedEpoch",
                table: "PlayerCraftingSlots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChosenAffixId",
                table: "PlayerCraftingSlots");

            migrationBuilder.DropColumn(
                name: "FloorTier",
                table: "PlayerCraftingSlots");

            migrationBuilder.DropColumn(
                name: "StartedEpoch",
                table: "PlayerCraftingSlots");
        }
    }
}
