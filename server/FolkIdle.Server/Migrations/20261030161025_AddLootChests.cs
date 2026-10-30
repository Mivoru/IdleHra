using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddLootChests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LootChests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    Tier = table.Column<byte>(type: "smallint", nullable: false),
                    Region = table.Column<byte>(type: "smallint", nullable: false),
                    Source = table.Column<byte>(type: "smallint", nullable: false),
                    GrantedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LootChests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LootChestWeeks",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WeekKey = table.Column<int>(type: "integer", nullable: false),
                    DiamondsThisWeek = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LootChestWeeks", x => x.PlayerId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LootChests_PlayerId",
                table: "LootChests",
                column: "PlayerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LootChests");

            migrationBuilder.DropTable(
                name: "LootChestWeeks");
        }
    }
}
