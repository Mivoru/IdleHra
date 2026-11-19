using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddNavProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nav_progress",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    BestDepths = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    HighestCircle = table.Column<int>(type: "integer", nullable: false),
                    Souls = table.Column<long>(type: "bigint", nullable: false),
                    SoulsLifetime = table.Column<long>(type: "bigint", nullable: false),
                    Workshop = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Research = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ResearchActive = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResearchEndsEpoch = table.Column<long>(type: "bigint", nullable: false),
                    Charges = table.Column<int>(type: "integer", nullable: false),
                    ChargeAnchorEpoch = table.Column<long>(type: "bigint", nullable: false),
                    MilestoneMask = table.Column<long>(type: "bigint", nullable: false),
                    RunsTotal = table.Column<int>(type: "integer", nullable: false),
                    LastRunJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nav_progress", x => x.PlayerId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nav_progress");
        }
    }
}
