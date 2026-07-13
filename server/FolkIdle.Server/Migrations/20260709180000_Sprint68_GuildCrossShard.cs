using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint68_GuildCrossShard : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GuildDefenseRosters",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    RegionShardId = table.Column<int>(type: "integer", nullable: false),
                    DefensiveStatsJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildDefenseRosters", x => x.GuildId);
                });

            migrationBuilder.CreateTable(
                name: "GuildMatchmakingSnapshots",
                columns: table => new
                {
                    MatchUuid = table.Column<System.Guid>(type: "uuid", nullable: false),
                    AttackerGuildId = table.Column<long>(type: "bigint", nullable: false),
                    DefenderGuildId = table.Column<long>(type: "bigint", nullable: false),
                    GlobalNodeMaxHp = table.Column<long>(type: "bigint", nullable: false),
                    GlobalNodeRemainingHp = table.Column<long>(type: "bigint", nullable: false),
                    TournamentGroupIndex = table.Column<int>(type: "integer", nullable: false),
                    IsComplete = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveMatchMmr = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildMatchmakingSnapshots", x => x.MatchUuid);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GuildDefenseRosters_RegionShardId_GuildId",
                table: "GuildDefenseRosters",
                columns: new[] { "RegionShardId", "GuildId" });

            migrationBuilder.CreateIndex(
                name: "IX_GuildMatchmakingSnapshots_AttackerGuildId_DefenderGuildId",
                table: "GuildMatchmakingSnapshots",
                columns: new[] { "AttackerGuildId", "DefenderGuildId" });

            migrationBuilder.CreateIndex(
                name: "IX_GuildMatchmakingSnapshots_TournamentGroupIndex_ActiveMatchMmr",
                table: "GuildMatchmakingSnapshots",
                columns: new[] { "TournamentGroupIndex", "ActiveMatchMmr" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GuildMatchmakingSnapshots");
            migrationBuilder.DropTable(name: "GuildDefenseRosters");
        }
    }
}
