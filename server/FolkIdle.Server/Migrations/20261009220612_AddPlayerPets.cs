using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerPets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "player_pets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    PetId = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    CharacterId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcquiredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_pets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_player_pets_CharacterId",
                table: "player_pets",
                column: "CharacterId",
                unique: true,
                filter: "\"CharacterId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_player_pets_PlayerId_PetId",
                table: "player_pets",
                columns: new[] { "PlayerId", "PetId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_pets");
        }
    }
}
