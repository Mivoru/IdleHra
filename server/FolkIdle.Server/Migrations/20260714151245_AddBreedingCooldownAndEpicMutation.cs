using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddBreedingCooldownAndEpicMutation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BreedingCooldownEndEpoch",
                table: "characters",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "IsBreedingActive",
                table: "characters",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsEpicMutation",
                table: "character_lineage_registry",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BreedingCooldownEndEpoch",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "IsBreedingActive",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "IsEpicMutation",
                table: "character_lineage_registry");
        }
    }
}
