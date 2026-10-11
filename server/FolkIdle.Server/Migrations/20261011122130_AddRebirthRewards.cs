using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddRebirthRewards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_player_pets_CharacterId",
                table: "player_pets");

            migrationBuilder.AddColumn<int>(
                name: "PeakLevel",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<byte>(
                name: "Slot",
                table: "player_pets",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.CreateIndex(
                name: "IX_player_pets_CharacterId_Slot",
                table: "player_pets",
                columns: new[] { "CharacterId", "Slot" },
                unique: true,
                filter: "\"CharacterId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_player_pets_CharacterId_Slot",
                table: "player_pets");

            migrationBuilder.DropColumn(
                name: "PeakLevel",
                table: "PlayerRecords");

            migrationBuilder.DropColumn(
                name: "Slot",
                table: "player_pets");

            migrationBuilder.CreateIndex(
                name: "IX_player_pets_CharacterId",
                table: "player_pets",
                column: "CharacterId",
                unique: true,
                filter: "\"CharacterId\" IS NOT NULL");
        }
    }
}
