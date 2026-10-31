using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDungeonProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DungeonProgress",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Tickets = table.Column<int>(type: "integer", nullable: false),
                    TicketDay = table.Column<int>(type: "integer", nullable: false),
                    BestStage = table.Column<int>(type: "integer", nullable: false),
                    FirstClearMask = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DungeonProgress", x => new { x.PlayerId, x.Kind });
                });

            // Modul: ADDITIVE - the equipment dungeon's four PlayerRecords columns
            // are copied into Kind 0 rows (it was open to players for a few hours
            // in 1.0.1486, so best stages and first clears exist), and the columns
            // stay where they are, unread. Only players who ever touched the
            // dungeon get a row; everyone else's is created on first read.
            migrationBuilder.Sql(@"
                INSERT INTO ""DungeonProgress"" (""PlayerId"", ""Kind"", ""Tickets"", ""TicketDay"", ""BestStage"", ""FirstClearMask"")
                SELECT ""Id"", 0, ""DungeonTickets"", ""DungeonTicketDay"", ""DungeonBestStage"", ""DungeonFirstClearMask""
                FROM ""PlayerRecords""
                WHERE ""DungeonTicketDay"" > 0 OR ""DungeonBestStage"" > 0 OR ""DungeonFirstClearMask"" <> 0
                ON CONFLICT DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DungeonProgress");
        }
    }
}
