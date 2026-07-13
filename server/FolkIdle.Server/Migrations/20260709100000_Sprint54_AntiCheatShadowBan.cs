using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint54_AntiCheatShadowBan : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsQuarantined",
                table: "PlayerRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsQuarantined",
                table: "MarketEquipmentInstances",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IsQuarantined", table: "PlayerRecords");
            migrationBuilder.DropColumn(name: "IsQuarantined", table: "MarketEquipmentInstances");
        }
    }
}
