using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddLifetimeStatisticsCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TotalDeaths",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TotalItemsCrafted",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TotalPlayTimeSeconds",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TotalDeaths",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "TotalItemsCrafted",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "TotalPlayTimeSeconds",
                table: "PlayerRecords");
        }
    }
}
