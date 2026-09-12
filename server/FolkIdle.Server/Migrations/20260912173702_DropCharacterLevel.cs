using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// Drops characters."Level" - a column nothing in the server ever wrote
    /// except DevFixtureSeeder, while BreedingEngine gated both pairings on it
    /// reaching 50. Measured on the live database before this ran: 80 rows,
    /// max value 1, none at 50. Breeding was therefore impossible for every
    /// player since launch.
    ///
    /// DESTRUCTIVE, and deliberately so - but it destroys nothing. Every value
    /// in the column is the constant the insert path wrote; no gameplay ever
    /// changed one. Down restores the column at 0 rather than trying to
    /// reconstruct per-row values, because there were never any to reconstruct.
    /// </summary>
    public partial class DropCharacterLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Level",
                table: "characters");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Level",
                table: "characters",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
