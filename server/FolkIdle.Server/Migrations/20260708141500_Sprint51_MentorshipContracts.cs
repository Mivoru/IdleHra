using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint51_MentorshipContracts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MentorshipContracts",
                columns: table => new
                {
                    ContractId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MentorPlayerId = table.Column<long>(type: "bigint", nullable: false),
                    MenteePlayerId = table.Column<long>(type: "bigint", nullable: false),
                    ExpBonusMultiplier = table.Column<double>(type: "double precision", nullable: false),
                    TimestampEstablished = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MentorshipContracts", x => x.ContractId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MentorshipContracts_MenteePlayerId",
                table: "MentorshipContracts",
                column: "MenteePlayerId",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MentorshipContracts");
        }
    }
}
