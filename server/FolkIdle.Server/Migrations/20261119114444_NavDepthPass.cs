using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class NavDepthPass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GuardiansMet",
                table: "nav_progress",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PrevWeekCircle",
                table: "nav_progress",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PrevWeekDepth",
                table: "nav_progress",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PrevWeekId",
                table: "nav_progress",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WeekCircle",
                table: "nav_progress",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WeekDepth",
                table: "nav_progress",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WeekId",
                table: "nav_progress",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WeekRewardSettledId",
                table: "nav_progress",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GuardiansMet",
                table: "nav_progress");

            migrationBuilder.DropColumn(
                name: "PrevWeekCircle",
                table: "nav_progress");

            migrationBuilder.DropColumn(
                name: "PrevWeekDepth",
                table: "nav_progress");

            migrationBuilder.DropColumn(
                name: "PrevWeekId",
                table: "nav_progress");

            migrationBuilder.DropColumn(
                name: "WeekCircle",
                table: "nav_progress");

            migrationBuilder.DropColumn(
                name: "WeekDepth",
                table: "nav_progress");

            migrationBuilder.DropColumn(
                name: "WeekId",
                table: "nav_progress");

            migrationBuilder.DropColumn(
                name: "WeekRewardSettledId",
                table: "nav_progress");
        }
    }
}
