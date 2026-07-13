using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint70_AccountSecurityQuotas : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountSecurityQuotas",
                columns: table => new
                {
                    AccountId = table.Column<System.Guid>(type: "uuid", nullable: false),
                    TotalFloodInfractionsCount = table.Column<int>(type: "integer", nullable: false),
                    LastInfractionEpoch = table.Column<long>(type: "bigint", nullable: false),
                    IsPermanentlyBlacklisted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountSecurityQuotas", x => x.AccountId);
                    table.CheckConstraint("CK_AccountSecurityQuotas_TotalFloodInfractionsCount", "\"TotalFloodInfractionsCount\" >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountSecurityQuotas_Blacklist_LastInfraction",
                table: "AccountSecurityQuotas",
                columns: new[] { "IsPermanentlyBlacklisted", "LastInfractionEpoch" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AccountSecurityQuotas");
        }
    }
}
