using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddBreedingAptitudes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AptitudeEndurance",
                table: "character_lineage_registry",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AptitudeFortune",
                table: "character_lineage_registry",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AptitudeSkill",
                table: "character_lineage_registry",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AptitudeStrength",
                table: "character_lineage_registry",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AptitudeEndurance",
                table: "character_lineage_registry");

            migrationBuilder.DropColumn(
                name: "AptitudeFortune",
                table: "character_lineage_registry");

            migrationBuilder.DropColumn(
                name: "AptitudeSkill",
                table: "character_lineage_registry");

            migrationBuilder.DropColumn(
                name: "AptitudeStrength",
                table: "character_lineage_registry");
        }
    }
}
