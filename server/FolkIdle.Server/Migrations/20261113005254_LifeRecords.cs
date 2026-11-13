using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class LifeRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BestLifeSecondsTo100",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "BestLifeSecondsTo50",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "LifeStartedAtEpoch",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BestLifeSecondsTo100",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BestLifeSecondsTo50",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "LifeStartedAtEpoch",
                table: "PlayerRecords");
        }
    }
}
