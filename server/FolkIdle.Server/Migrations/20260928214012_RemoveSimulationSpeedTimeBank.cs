using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSimulationSpeedTimeBank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Modul: NOT ADDITIVE - it deletes data on purpose (owner,
            // 2026-09-28). Simulation speed was removed; the bank it spent had
            // not been filled by anything since the chrono deletion, and nine
            // live accounts still held a leftover balance (the largest about
            // 6.8 days) that bought 4x progress. Take a backup before deploying.
            migrationBuilder.DropColumn(
                name: "AccumulatedTimeBankSeconds",
                table: "PlayerRecords");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccumulatedTimeBankSeconds",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
