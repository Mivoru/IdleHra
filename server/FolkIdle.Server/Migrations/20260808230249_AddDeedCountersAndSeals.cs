using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDeedCountersAndSeals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AffixRerollsPerformed",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "BestSeasonRank",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ForgeFusionsCompleted",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "SealsEarnedMask",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AffixRerollsPerformed",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BestSeasonRank",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "ForgeFusionsCompleted",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "SealsEarnedMask",
                table: "PlayerRecords");
        }
    }
}
