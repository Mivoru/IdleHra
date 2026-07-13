using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerWorldBossAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "player_world_boss_attempts",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    BossInstanceId = table.Column<long>(type: "bigint", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    TotalInflictedDamage = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_world_boss_attempts", x => new { x.PlayerId, x.BossInstanceId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_world_boss_attempts");
        }
    }
}
