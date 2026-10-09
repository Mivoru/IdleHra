using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddSeasonalEventCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EventCurrency",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EventCurrencyDayKey",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EventCurrencyEarnedToday",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EventCurrencyEventId",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EventCurrency",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "EventCurrencyDayKey",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "EventCurrencyEarnedToday",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "EventCurrencyEventId",
                table: "PlayerRecords");
        }
    }
}
