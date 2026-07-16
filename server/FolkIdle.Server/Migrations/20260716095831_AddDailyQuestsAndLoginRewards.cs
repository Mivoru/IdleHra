using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyQuestsAndLoginRewards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastLoginTimestamp",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "LoginStreakDays",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DailyQuestRecords",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    QuestSlot = table.Column<int>(type: "integer", nullable: false),
                    QuestType = table.Column<int>(type: "integer", nullable: false),
                    TargetAmount = table.Column<int>(type: "integer", nullable: false),
                    CurrentProgress = table.Column<int>(type: "integer", nullable: false),
                    RewardClaimed = table.Column<bool>(type: "boolean", nullable: false),
                    DateKeyUtc = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyQuestRecords", x => new { x.PlayerId, x.QuestSlot });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyQuestRecords");

            migrationBuilder.DropColumn(
                name: "LastLoginTimestamp",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "LoginStreakDays",
                table: "PlayerRecords");
        }
    }
}
