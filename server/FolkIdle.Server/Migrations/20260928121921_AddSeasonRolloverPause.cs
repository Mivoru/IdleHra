using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddSeasonRolloverPause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRolloverPaused",
                table: "SeasonalEraRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The owner paused the rollover on 2026-09-28, with the active
            // season due to end on 2026-11-02 and wipe their account. Done
            // here so the deploy itself carries the decision, instead of
            // depending on somebody pressing Pause before the date.
            migrationBuilder.Sql("UPDATE \"SeasonalEraRecords\" SET \"IsRolloverPaused\" = TRUE WHERE \"IsActive\" = TRUE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsRolloverPaused",
                table: "SeasonalEraRecords");
        }
    }
}
