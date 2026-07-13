using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint45_DurabilityEliminationAndSeasonalLegacy : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"EquipmentInstances\" DROP COLUMN IF EXISTS \"CurrentDurability\";");
            migrationBuilder.Sql("ALTER TABLE \"EquipmentInstances\" DROP COLUMN IF EXISTS \"MaximumDurability\";");

            migrationBuilder.CreateTable(
                name: "SeasonalEraRecords",
                columns: table => new
                {
                    EraId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EndTimestamp = table.Column<long>(type: "bigint", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeasonalEraRecords", x => x.EraId);
                });

            migrationBuilder.CreateTable(
                name: "PlayerLegacyLedgers",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    EraId = table.Column<int>(type: "integer", nullable: false),
                    LegacyShardBalance = table.Column<int>(type: "integer", nullable: false),
                    CitizenMultiSlotsUnlocked = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerLegacyLedgers", x => new { x.PlayerId, x.EraId });
                    table.ForeignKey(
                        name: "FK_PlayerLegacyLedgers_SeasonalEraRecords_EraId",
                        column: x => x.EraId,
                        principalTable: "SeasonalEraRecords",
                        principalColumn: "EraId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerLegacyLedgers_EraId",
                table: "PlayerLegacyLedgers",
                column: "EraId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PlayerLegacyLedgers");
            migrationBuilder.DropTable(name: "SeasonalEraRecords");

            migrationBuilder.AddColumn<int>(
                name: "CurrentDurability",
                table: "EquipmentInstances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaximumDurability",
                table: "EquipmentInstances",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
