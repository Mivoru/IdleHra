using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddTheDeep : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveTitleSlug",
                table: "PlayerRecords",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DelveDeepestFloor",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DelveDeepestThisWeek",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "DelveDeepestThisWeekAtUtc",
                table: "PlayerRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeep",
                table: "DelveRunRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LanternsBought",
                table: "DelveRunRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "StakeGold",
                table: "DelveRunRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "player_gold_daily_high",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    DayUtc = table.Column<DateOnly>(type: "date", nullable: false),
                    MaxGold = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_gold_daily_high", x => new { x.PlayerId, x.DayUtc });
                });

            migrationBuilder.CreateTable(
                name: "player_titles",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    TitleSlug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EarnedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_titles", x => new { x.PlayerId, x.TitleSlug });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_gold_daily_high");

            migrationBuilder.DropTable(
                name: "player_titles");

            migrationBuilder.DropColumn(
                name: "ActiveTitleSlug",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "DelveDeepestFloor",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "DelveDeepestThisWeek",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "DelveDeepestThisWeekAtUtc",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "IsDeep",
                table: "DelveRunRecords");

            migrationBuilder.DropColumn(
                name: "LanternsBought",
                table: "DelveRunRecords");

            migrationBuilder.DropColumn(
                name: "StakeGold",
                table: "DelveRunRecords");
        }
    }
}
