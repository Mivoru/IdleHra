using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddCosmetics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EquippedAvatarId",
                table: "PlayerRecords",
                type: "character varying(48)",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EquippedFrameId",
                table: "PlayerRecords",
                type: "character varying(48)",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LevelChestsGranted",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "cosmetic_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<byte>(type: "smallint", nullable: false),
                    DefinitionId = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Rarity = table.Column<byte>(type: "smallint", nullable: false),
                    Source = table.Column<byte>(type: "smallint", nullable: false),
                    AcquiredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsListed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cosmetic_items", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cosmetic_items_PlayerId_Kind_Rarity",
                table: "cosmetic_items",
                columns: new[] { "PlayerId", "Kind", "Rarity" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cosmetic_items");

            migrationBuilder.DropColumn(
                name: "EquippedAvatarId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "EquippedFrameId",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "LevelChestsGranted",
                table: "PlayerRecords");
        }
    }
}
