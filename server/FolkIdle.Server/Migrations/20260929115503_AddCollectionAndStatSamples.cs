using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionAndStatSamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "player_collection",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    BaseItemId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BestTier = table.Column<int>(type: "integer", nullable: false),
                    FirstOwnedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_collection", x => new { x.PlayerId, x.BaseItemId });
                });

            migrationBuilder.CreateTable(
                name: "player_stat_samples",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kills = table.Column<long>(type: "bigint", nullable: false),
                    Xp = table.Column<long>(type: "bigint", nullable: false),
                    Gold = table.Column<long>(type: "bigint", nullable: false),
                    Harvests = table.Column<long>(type: "bigint", nullable: false),
                    Crafted = table.Column<long>(type: "bigint", nullable: false),
                    Fighting = table.Column<short>(type: "smallint", nullable: false),
                    Gathering = table.Column<short>(type: "smallint", nullable: false),
                    Crafting = table.Column<short>(type: "smallint", nullable: false),
                    Idle = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_stat_samples", x => new { x.PlayerId, x.AtUtc });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_collection");

            migrationBuilder.DropTable(
                name: "player_stat_samples");
        }
    }
}
