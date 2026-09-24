using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatureUnlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feature_unlocks",
                columns: table => new
                {
                    FeatureKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UnlockedAtEpochMs = table.Column<long>(type: "bigint", nullable: false),
                    QualifyingPlayersAtUnlock = table.Column<int>(type: "integer", nullable: false),
                    QualifyingGuildsAtUnlock = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_unlocks", x => x.FeatureKey);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feature_unlocks");
        }
    }
}
