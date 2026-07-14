using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddWorldBossAttemptSessionStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SessionStartEpoch",
                table: "player_world_boss_attempts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SessionStartEpoch",
                table: "player_world_boss_attempts");
        }
    }
}
