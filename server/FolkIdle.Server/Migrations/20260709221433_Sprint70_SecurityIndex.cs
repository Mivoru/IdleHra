using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class Sprint70_SecurityIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AccountSecurityQuotas_SecurityIndex",
                table: "AccountSecurityQuotas",
                columns: new[] { "AccountId", "IsPermanentlyBlacklisted" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccountSecurityQuotas_SecurityIndex",
                table: "AccountSecurityQuotas");
        }
    }
}
