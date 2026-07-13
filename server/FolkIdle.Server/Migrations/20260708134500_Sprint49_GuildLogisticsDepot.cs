using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint49_GuildLogisticsDepot : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GuildContributionLedgers",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    MaterialId = table.Column<int>(type: "integer", nullable: false),
                    LifetimeContributed = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildContributionLedgers", x => new { x.PlayerId, x.GuildId, x.MaterialId });
                });

            migrationBuilder.CreateTable(
                name: "GuildLogisticsDepots",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    MaterialId = table.Column<int>(type: "integer", nullable: false),
                    CurrentStock = table.Column<long>(type: "bigint", nullable: false),
                    TargetRequirement = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildLogisticsDepots", x => new { x.GuildId, x.MaterialId });
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "GuildContributionLedgers");
            migrationBuilder.DropTable(name: "GuildLogisticsDepots");
        }
    }
}
