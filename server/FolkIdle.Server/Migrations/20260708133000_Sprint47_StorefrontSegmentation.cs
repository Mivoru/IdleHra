using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint47_StorefrontSegmentation : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerSegmentationProfiles",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    CohortTag = table.Column<int>(type: "integer", nullable: false),
                    LifetimeValueCents = table.Column<int>(type: "integer", nullable: false),
                    ChurnRiskScore = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerSegmentationProfiles", x => x.PlayerId);
                });

            migrationBuilder.CreateTable(
                name: "SegmentedStorefrontListings",
                columns: table => new
                {
                    ListingId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TargetCohort = table.Column<int>(type: "integer", nullable: false),
                    ProductIdentifier = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DiamondPackageYield = table.Column<int>(type: "integer", nullable: false),
                    PriceInCents = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SegmentedStorefrontListings", x => x.ListingId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SegmentedStorefrontListings_TargetCohort",
                table: "SegmentedStorefrontListings",
                column: "TargetCohort");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PlayerSegmentationProfiles");
            migrationBuilder.DropTable(name: "SegmentedStorefrontListings");
        }
    }
}
