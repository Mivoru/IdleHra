using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint66_ChroniclePass : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerChroniclePasses",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    PassLevel = table.Column<int>(type: "integer", nullable: false),
                    AccumulatedXp = table.Column<int>(type: "integer", nullable: false),
                    ClaimedMilestonesBitmask = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerChroniclePasses", x => x.PlayerId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerChroniclePasses_PassLevel",
                table: "PlayerChroniclePasses",
                column: "PassLevel");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PlayerChroniclePasses");
        }
    }
}
