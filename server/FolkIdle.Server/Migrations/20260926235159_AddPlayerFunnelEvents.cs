using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerFunnelEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "player_funnel_events",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    Step = table.Column<short>(type: "smallint", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_player_funnel_events", x => new { x.PlayerId, x.Step });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "player_funnel_events");
        }
    }
}
