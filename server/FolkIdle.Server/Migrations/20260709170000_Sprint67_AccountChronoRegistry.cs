using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint67_AccountChronoRegistry : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_chrono_registry",
                columns: table => new
                {
                    AccountId = table.Column<System.Guid>(type: "uuid", nullable: false),
                    BankedChronoSeconds = table.Column<int>(type: "integer", nullable: false),
                    ActiveSpeedMultiplier = table.Column<double>(type: "double precision", nullable: false),
                    AccelerationTerminationEpoch = table.Column<long>(type: "bigint", nullable: false),
                    LastClockSyncEpoch = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_chrono_registry", x => x.AccountId);
                    table.CheckConstraint("CK_account_chrono_registry_BankedChronoSeconds", "\"BankedChronoSeconds\" >= 0 AND \"BankedChronoSeconds\" <= 604800");
                    table.CheckConstraint("CK_account_chrono_registry_ActiveSpeedMultiplier", "\"ActiveSpeedMultiplier\" IN (1.0, 2.0, 4.0)");
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "account_chrono_registry");
        }
    }
}
