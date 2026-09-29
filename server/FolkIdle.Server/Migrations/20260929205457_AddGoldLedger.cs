using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddGoldLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LifetimeGoldSpent",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "gold_spend_daily",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Category = table.Column<short>(type: "smallint", nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gold_spend_daily", x => new { x.PlayerId, x.Day, x.Category });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gold_spend_daily");

            migrationBuilder.DropColumn(
                name: "LifetimeGoldSpent",
                table: "PlayerRecords");
        }
    }
}
