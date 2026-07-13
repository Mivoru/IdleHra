using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommodityRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ItemId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommodityRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GuildRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TotalGoldContributed = table.Column<long>(type: "bigint", nullable: false),
                    CurrentTier = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MarketEquipmentInstances",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BaseItemId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    QualityTier = table.Column<int>(type: "integer", nullable: false),
                    AffixPayload = table.Column<string>(type: "jsonb", nullable: false),
                    IsAffixLocked = table.Column<bool>(type: "boolean", nullable: false),
                    IsLockedInEscrow = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketEquipmentInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlayerRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CurrentLevel = table.Column<int>(type: "integer", nullable: false),
                    CurrentXp = table.Column<long>(type: "bigint", nullable: false),
                    SelectedLineageId = table.Column<int>(type: "integer", nullable: false),
                    PlayerGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthenticatorToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GuildMaterialSinkLedgers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    CommodityId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TotalAmountContributed = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildMaterialSinkLedgers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GuildMaterialSinkLedgers_GuildRecords_GuildId",
                        column: x => x.GuildId,
                        principalTable: "GuildRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MarketOrderRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SellerId = table.Column<long>(type: "bigint", nullable: false),
                    CommodityId = table.Column<long>(type: "bigint", nullable: true),
                    EquipmentInstanceId = table.Column<long>(type: "bigint", nullable: true),
                    Price = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OrderType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    BaseItemId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    QualityTier = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MarketOrderRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MarketOrderRecords_CommodityRecords_CommodityId",
                        column: x => x.CommodityId,
                        principalTable: "CommodityRecords",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_MarketOrderRecords_MarketEquipmentInstances_EquipmentInstan~",
                        column: x => x.EquipmentInstanceId,
                        principalTable: "MarketEquipmentInstances",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_GuildMaterialSinkLedgers_GuildId",
                table: "GuildMaterialSinkLedgers",
                column: "GuildId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketOrderRecords_CommodityId",
                table: "MarketOrderRecords",
                column: "CommodityId");

            migrationBuilder.CreateIndex(
                name: "IX_MarketOrderRecords_EquipmentInstanceId",
                table: "MarketOrderRecords",
                column: "EquipmentInstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuildMaterialSinkLedgers");

            migrationBuilder.DropTable(
                name: "MarketOrderRecords");

            migrationBuilder.DropTable(
                name: "PlayerRecords");

            migrationBuilder.DropTable(
                name: "GuildRecords");

            migrationBuilder.DropTable(
                name: "CommodityRecords");

            migrationBuilder.DropTable(
                name: "MarketEquipmentInstances");
        }
    }
}
