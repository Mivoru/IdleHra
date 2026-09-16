using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// Adds TraitMask to lineages and newcomers. Additive: every existing row
    /// gets 0, which means "no traits" - the intended state for characters
    /// that predate traits.
    public partial class AddBreedingTraits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TraitMask",
                table: "village_newcomers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TraitMask",
                table: "character_lineage_registry",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TraitMask",
                table: "village_newcomers");

            migrationBuilder.DropColumn(
                name: "TraitMask",
                table: "character_lineage_registry");
        }
    }
}
