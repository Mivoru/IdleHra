using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddToolEquipmentSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EquippedAxeId",
                table: "characters",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EquippedPickaxeId",
                table: "characters",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EquippedRodId",
                table: "characters",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EquippedAxeId",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "EquippedPickaxeId",
                table: "characters");

            migrationBuilder.DropColumn(
                name: "EquippedRodId",
                table: "characters");
        }
    }
}
