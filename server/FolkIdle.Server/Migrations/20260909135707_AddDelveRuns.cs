using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDelveRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DelveDiamondsThisWeek",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DelveWeekKey",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DelveRunRecords",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    EntryFeePaid = table.Column<long>(type: "bigint", nullable: false),
                    CurrentFloor = table.Column<int>(type: "integer", nullable: false),
                    FloorsCleared = table.Column<int>(type: "integer", nullable: false),
                    ChargesRemaining = table.Column<int>(type: "integer", nullable: false),
                    PackedDoorDemands = table.Column<int>(type: "integer", nullable: false),
                    RevealedDoorMask = table.Column<int>(type: "integer", nullable: false),
                    StartedAtEpoch = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DelveRunRecords", x => x.PlayerId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DelveRunRecords");

            migrationBuilder.DropColumn(
                name: "DelveDiamondsThisWeek",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "DelveWeekKey",
                table: "PlayerRecords");
        }
    }
}
