using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint50_GuildCombatSimulation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GuildWarActiveMatches",
                columns: table => new
                {
                    MatchId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AttackingGuildId = table.Column<long>(type: "bigint", nullable: false),
                    DefendingGuildId = table.Column<long>(type: "bigint", nullable: false),
                    InitialSeed = table.Column<int>(type: "integer", nullable: false),
                    CurrentStateBitmask = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildWarActiveMatches", x => x.MatchId);
                });

            migrationBuilder.CreateTable(
                name: "GuildWarCombatHistory",
                columns: table => new
                {
                    LogId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MatchId = table.Column<long>(type: "bigint", nullable: false),
                    ExecutionTick = table.Column<long>(type: "bigint", nullable: false),
                    DamageDelta = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildWarCombatHistory", x => x.LogId);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GuildWarCombatHistory");
            migrationBuilder.DropTable(name: "GuildWarActiveMatches");
        }
    }
}
