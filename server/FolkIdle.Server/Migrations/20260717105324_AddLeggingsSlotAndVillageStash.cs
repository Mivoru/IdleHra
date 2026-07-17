using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddLeggingsSlotAndVillageStash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EquippedLeggingsId",
                table: "PlayerRecords",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VillageStashInstances",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Quantity = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VillageStashInstances", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VillageStashInstances_PlayerId_ItemId",
                table: "VillageStashInstances",
                columns: new[] { "PlayerId", "ItemId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VillageStashInstances");

            migrationBuilder.DropColumn(
                name: "EquippedLeggingsId",
                table: "PlayerRecords");
        }
    }
}
