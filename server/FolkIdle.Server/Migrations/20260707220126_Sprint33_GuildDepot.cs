using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class Sprint33_GuildDepot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MiningMonolithLevel",
                table: "GuildRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MiningMonolithProgress",
                table: "GuildRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WoodcuttingMonolithLevel",
                table: "GuildRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "WoodcuttingMonolithProgress",
                table: "GuildRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "GuildDepotBalances",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    ItemDefinitionId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildDepotBalances", x => new { x.GuildId, x.ItemDefinitionId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuildDepotBalances");

            migrationBuilder.DropColumn(
                name: "MiningMonolithLevel",
                table: "GuildRecords");

            migrationBuilder.DropColumn(
                name: "MiningMonolithProgress",
                table: "GuildRecords");

            migrationBuilder.DropColumn(
                name: "WoodcuttingMonolithLevel",
                table: "GuildRecords");

            migrationBuilder.DropColumn(
                name: "WoodcuttingMonolithProgress",
                table: "GuildRecords");
        }
    }
}
