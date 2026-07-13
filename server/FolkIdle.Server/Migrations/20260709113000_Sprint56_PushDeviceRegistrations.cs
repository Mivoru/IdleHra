using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    public partial class Sprint56_PushDeviceRegistrations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayerDeviceRegistrations",
                columns: table => new
                {
                    PlayerId = table.Column<long>(type: "bigint", nullable: false),
                    DeviceTokenRaw = table.Column<byte[]>(type: "bytea", maxLength: 64, nullable: false),
                    PlatformFamily = table.Column<byte>(type: "smallint", nullable: false),
                    TimestampRegistered = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerDeviceRegistrations", x => new { x.PlayerId, x.DeviceTokenRaw });
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerDeviceRegistrations_PlayerId",
                table: "PlayerDeviceRegistrations",
                column: "PlayerId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PlayerDeviceRegistrations");
        }
    }
}
