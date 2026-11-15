using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class DailyMissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerMissionCounters",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    DayKey = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<short>(type: "smallint", nullable: false),
                    Detail = table.Column<short>(type: "smallint", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerMissionCounters", x => new { x.PlayerId, x.DayKey, x.Action, x.Detail });
                });

            migrationBuilder.CreateTable(
                name: "PlayerMissionDays",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    DayKey = table.Column<int>(type: "integer", nullable: false),
                    Actions = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClaimedMask = table.Column<int>(type: "integer", nullable: false),
                    BonusClaimed = table.Column<bool>(type: "boolean", nullable: false),
                    RerollsUsed = table.Column<int>(type: "integer", nullable: false),
                    XpEarned = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerMissionDays", x => new { x.PlayerId, x.DayKey });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerMissionCounters_DayKey",
                table: "PlayerMissionCounters",
                column: "DayKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerMissionCounters");

            migrationBuilder.DropTable(
                name: "PlayerMissionDays");
        }
    }
}
