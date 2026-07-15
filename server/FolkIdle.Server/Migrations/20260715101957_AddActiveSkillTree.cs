using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveSkillTree : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AvailableSkillPoints",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PlayerSkillUnlocks",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    SkillId = table.Column<int>(type: "integer", nullable: false),
                    UnlockedAtEpoch = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerSkillUnlocks", x => new { x.PlayerId, x.SkillId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerSkillUnlocks_PlayerId",
                table: "PlayerSkillUnlocks",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerSkillUnlocks");

            migrationBuilder.DropColumn(
                name: "AvailableSkillPoints",
                table: "PlayerRecords");
        }
    }
}
