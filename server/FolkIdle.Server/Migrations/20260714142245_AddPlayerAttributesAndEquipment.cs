using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerAttributesAndEquipment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BaseConstitution",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BaseDexterity",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BaseLuck",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BaseStrength",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "EquippedArmorId",
                table: "PlayerRecords",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EquippedWeaponId",
                table: "PlayerRecords",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaseConstitution",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BaseDexterity",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BaseLuck",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BaseStrength",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "EquippedArmorId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "EquippedWeaponId",
                table: "PlayerRecords");
        }
    }
}
