using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddWorldBossArmourPlates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "BrokenPlateMask",
                table: "WorldBossSnapshots",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte>(
                name: "WeakPlateIndex",
                table: "WorldBossSnapshots",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte>(
                name: "WeakPlateRevealed",
                table: "WorldBossSnapshots",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BrokenPlateMask",
                table: "WorldBossSnapshots");

            migrationBuilder.DropColumn(
                name: "WeakPlateIndex",
                table: "WorldBossSnapshots");

            migrationBuilder.DropColumn(
                name: "WeakPlateRevealed",
                table: "WorldBossSnapshots");
        }
    }
}
