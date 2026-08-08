using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddVillageNewcomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastVillagerArrivalEpoch",
                table: "PlayerRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "village_newcomers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    RaceId = table.Column<int>(type: "integer", nullable: false),
                    IsFemale = table.Column<bool>(type: "boolean", nullable: false),
                    AptitudeStrength = table.Column<int>(type: "integer", nullable: false),
                    AptitudeSkill = table.Column<int>(type: "integer", nullable: false),
                    AptitudeEndurance = table.Column<int>(type: "integer", nullable: false),
                    AptitudeFortune = table.Column<int>(type: "integer", nullable: false),
                    ArrivedAtEpoch = table.Column<long>(type: "bigint", nullable: false),
                    IsElder = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_village_newcomers", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "village_newcomers");

            migrationBuilder.DropColumn(
                name: "LastVillagerArrivalEpoch",
                table: "PlayerRecords");
        }
    }
}
