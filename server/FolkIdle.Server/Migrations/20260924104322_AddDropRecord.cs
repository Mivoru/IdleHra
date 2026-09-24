using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDropRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "loot_tier_daily_counts",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Source = table.Column<short>(type: "smallint", nullable: false),
                    RegionTier = table.Column<short>(type: "smallint", nullable: false),
                    QualityTier = table.Column<short>(type: "smallint", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    SalvagedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loot_tier_daily_counts", x => new { x.PlayerId, x.Day, x.Source, x.RegionTier, x.QualityTier });
                });

            migrationBuilder.CreateTable(
                name: "notable_item_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    EquipmentInstanceId = table.Column<long>(type: "bigint", nullable: true),
                    BaseItemId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Source = table.Column<short>(type: "smallint", nullable: false),
                    RolledTier = table.Column<short>(type: "smallint", nullable: false),
                    FinalTier = table.Column<short>(type: "smallint", nullable: false),
                    LootLuckPct = table.Column<float>(type: "real", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notable_item_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notable_item_events_PlayerId_CreatedAtUtc",
                table: "notable_item_events",
                columns: new[] { "PlayerId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "loot_tier_daily_counts");

            migrationBuilder.DropTable(
                name: "notable_item_events");
        }
    }
}
