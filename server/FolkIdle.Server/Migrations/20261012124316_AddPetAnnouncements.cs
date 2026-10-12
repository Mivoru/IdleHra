using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPetAnnouncements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AnnouncedAtUtc",
                table: "player_pets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingDuplicates",
                table: "player_pets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Modul: every pet owned before the window existed counts as
            // already shown. Otherwise the first login after this deploy would
            // open one window per pet an account has ever had.
            migrationBuilder.Sql("UPDATE player_pets SET \"AnnouncedAtUtc\" = \"AcquiredAtUtc\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnnouncedAtUtc",
                table: "player_pets");

            migrationBuilder.DropColumn(
                name: "PendingDuplicates",
                table: "player_pets");
        }
    }
}
