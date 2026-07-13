using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint55_LiveOpsWorldBoss : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorldBossSnapshots",
                columns: table => new
                {
                    BossInstanceId = table.Column<long>(type: "bigint", nullable: false),
                    MaxHp = table.Column<long>(type: "bigint", nullable: false),
                    CurrentHp = table.Column<long>(type: "bigint", nullable: false),
                    TotalDamageContributed = table.Column<long>(type: "bigint", nullable: false),
                    LastActiveTimestamp = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorldBossSnapshots", x => x.BossInstanceId);
                });

            migrationBuilder.CreateTable(
                name: "LiveOpsEventRotations",
                columns: table => new
                {
                    EventId = table.Column<int>(type: "integer", nullable: false),
                    EventType = table.Column<byte>(type: "smallint", nullable: false),
                    ModifierBitmask = table.Column<long>(type: "bigint", nullable: false),
                    EndTimestamp = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveOpsEventRotations", x => x.EventId);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "LiveOpsEventRotations");
            migrationBuilder.DropTable(name: "WorldBossSnapshots");
        }
    }
}
