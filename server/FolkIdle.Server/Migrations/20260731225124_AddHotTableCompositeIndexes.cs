using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddHotTableCompositeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MarketOrderRecords_BaseItemId_QualityTier_Status",
                table: "MarketOrderRecords",
                columns: new[] { "BaseItemId", "QualityTier", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_characters_PlayerId",
                table: "characters",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_EquipmentInstances_PlayerId",
                table: "EquipmentInstances",
                column: "PlayerId");

            migrationBuilder.CreateIndex(
                name: "IX_CommodityRecords_PlayerId_ItemId",
                table: "CommodityRecords",
                columns: new[] { "PlayerId", "ItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MarketOrderRecords_BaseItemId_QualityTier_Status",
                table: "MarketOrderRecords");

            migrationBuilder.DropIndex(
                name: "IX_characters_PlayerId",
                table: "characters");

            migrationBuilder.DropIndex(
                name: "IX_EquipmentInstances_PlayerId",
                table: "EquipmentInstances");

            migrationBuilder.DropIndex(
                name: "IX_CommodityRecords_PlayerId_ItemId",
                table: "CommodityRecords");
        }
    }
}
