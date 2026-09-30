using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AutomationRules",
                table: "characters",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutomationRules",
                table: "characters");
        }
    }
}
