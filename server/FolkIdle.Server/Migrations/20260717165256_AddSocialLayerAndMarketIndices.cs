using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddSocialLayerAndMarketIndices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerRelationships",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    TargetPlayerId = table.Column<long>(type: "bigint", nullable: false),
                    RelationType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerRelationships", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MarketOrderRecords_BaseItemId",
                table: "MarketOrderRecords",
                column: "BaseItemId");

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentInstances_BaseItemId",
                table: "EquipmentInstances",
                column: "BaseItemId");

            migrationBuilder.CreateIndex(
                name: "IX_CommodityRecords_ItemId",
                table: "CommodityRecords",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRelationships_PlayerId_TargetPlayerId",
                table: "PlayerRelationships",
                columns: new[] { "PlayerId", "TargetPlayerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerRelationships");

            migrationBuilder.DropIndex(
                name: "IX_MarketOrderRecords_BaseItemId",
                table: "MarketOrderRecords");

            migrationBuilder.DropIndex(
                name: "IX_EquipmentInstances_BaseItemId",
                table: "EquipmentInstances");

            migrationBuilder.DropIndex(
                name: "IX_CommodityRecords_ItemId",
                table: "CommodityRecords");
        }
    }
}
