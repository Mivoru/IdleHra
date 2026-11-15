using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class WorldBossForge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ForgeCharge",
                table: "WorldBossSnapshots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ForgeFills",
                table: "WorldBossSnapshots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ForgeLastFillEpoch",
                table: "WorldBossSnapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "ForgeFillFed",
                table: "player_world_boss_attempts",
                type: "integer",
                nullable: false,
                // Modul: -1, NOT EF's 0. 0 is the first fill's index, so a 0
                // default would enrol every existing striker as a feeder of the
                // first fill without a single right call (WorldBossForge).
                defaultValue: -1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForgeCharge",
                table: "WorldBossSnapshots");

            migrationBuilder.DropColumn(
                name: "ForgeFills",
                table: "WorldBossSnapshots");

            migrationBuilder.DropColumn(
                name: "ForgeLastFillEpoch",
                table: "WorldBossSnapshots");

            migrationBuilder.DropColumn(
                name: "ForgeFillFed",
                table: "player_world_boss_attempts");
        }
    }
}
