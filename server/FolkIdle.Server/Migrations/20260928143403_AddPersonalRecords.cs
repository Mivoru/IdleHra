using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BestDropAtUtc",
                table: "PlayerRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BestDropBaseId",
                table: "PlayerRecords",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BestDropTier",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BestHit",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BossBestKillTenthsR1",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BossBestKillTenthsR2",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BossBestKillTenthsR3",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BossBestKillTenthsR4",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BossBestKillTenthsR5",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);


            // Modul: task 51. Seed the best-drop record from the drop record
            // (task 26), so a long-played account does not greet its next Common
            // with "New record". Legendary+ only and 180 days deep - that table's
            // own reach - and DROPS only: kills, boss guarantees, offline and the
            // retry outbox (DropSource 1, 2, 3, 9, 10). A fused or crafted piece
            // is not a drop. Ties keep the earliest.
            migrationBuilder.Sql(@"
UPDATE ""PlayerRecords"" p
SET ""BestDropTier"" = b.""FinalTier"", ""BestDropBaseId"" = b.""BaseItemId"", ""BestDropAtUtc"" = b.""CreatedAtUtc""
FROM (
    SELECT DISTINCT ON (""PlayerId"") ""PlayerId"", ""FinalTier"", ""BaseItemId"", ""CreatedAtUtc""
    FROM notable_item_events
    WHERE ""Source"" IN (1, 2, 3, 9, 10)
    ORDER BY ""PlayerId"", ""FinalTier"" DESC, ""CreatedAtUtc"" ASC
) b
WHERE p.""Id"" = b.""PlayerId"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BestDropAtUtc",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BestDropBaseId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BestDropTier",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BestHit",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BossBestKillTenthsR1",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BossBestKillTenthsR2",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BossBestKillTenthsR3",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BossBestKillTenthsR4",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "BossBestKillTenthsR5",
                table: "PlayerRecords");
        }
    }
}
