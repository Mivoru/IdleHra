using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddCosmeticMarket : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cosmetic_market_listings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SellerId = table.Column<long>(type: "bigint", nullable: false),
                    CosmeticItemId = table.Column<long>(type: "bigint", nullable: false),
                    DefinitionId = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Kind = table.Column<byte>(type: "smallint", nullable: false),
                    Rarity = table.Column<byte>(type: "smallint", nullable: false),
                    Price = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cosmetic_market_listings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cosmetic_market_listings_CosmeticItemId",
                table: "cosmetic_market_listings",
                column: "CosmeticItemId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cosmetic_market_listings");
        }
    }
}
