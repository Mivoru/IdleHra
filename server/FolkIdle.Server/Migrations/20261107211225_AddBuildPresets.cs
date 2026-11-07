using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddBuildPresets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "player_build_presets",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Strength = table.Column<int>(type: "integer", nullable: false),
                    Dexterity = table.Column<int>(type: "integer", nullable: false),
                    Constitution = table.Column<int>(type: "integer", nullable: false),
                    Luck = table.Column<int>(type: "integer", nullable: false),
                    WarRanks = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_build_presets", x => new { x.PlayerId, x.Slot });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_build_presets");
        }
    }
}
