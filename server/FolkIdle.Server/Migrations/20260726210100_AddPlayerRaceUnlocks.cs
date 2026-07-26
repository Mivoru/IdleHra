using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerRaceUnlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "player_race_unlocks",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    RaceId = table.Column<int>(type: "integer", nullable: false),
                    UnlockedAtEpoch = table.Column<long>(type: "bigint", nullable: false),
                    UnlockedByMonsterId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_race_unlocks", x => new { x.PlayerId, x.RaceId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_player_race_unlocks_PlayerId",
                table: "player_race_unlocks",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_race_unlocks");
        }
    }
}
