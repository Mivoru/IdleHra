using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddWorldBossEventWindowAndDropPlayerMonsterCodex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_monster_codex");

            migrationBuilder.AddColumn<long>(
                name: "EventEndEpoch",
                table: "WorldBossSnapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte>(
                name: "EventState",
                table: "WorldBossSnapshots",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventEndEpoch",
                table: "WorldBossSnapshots");

            migrationBuilder.DropColumn(
                name: "EventState",
                table: "WorldBossSnapshots");

            migrationBuilder.CreateTable(
                name: "player_monster_codex",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    MonsterId = table.Column<int>(type: "integer", nullable: false),
                    KillCount = table.Column<long>(type: "bigint", nullable: false),
                    MaxRarityFound = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_monster_codex", x => new { x.PlayerId, x.MonsterId });
                });
        }
    }
}
