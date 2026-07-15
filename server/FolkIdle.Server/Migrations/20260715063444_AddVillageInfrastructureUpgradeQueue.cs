using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddVillageInfrastructureUpgradeQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "UpgradeCompletesAtEpoch",
                table: "VillageInfrastructures",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "UpgradeTargetLevel",
                table: "VillageInfrastructures",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UpgradeCompletesAtEpoch",
                table: "VillageInfrastructures");

            migrationBuilder.DropColumn(
                name: "UpgradeTargetLevel",
                table: "VillageInfrastructures");
        }
    }
}
