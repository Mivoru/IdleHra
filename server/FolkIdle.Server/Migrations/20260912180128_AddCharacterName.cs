using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// Adds characters."Name". Every existing row lands with an empty string
    /// and is named immediately afterwards by CharacterNameBackfill, which runs
    /// on the same `--migrate` entrypoint - see that class for why the backfill
    /// is C# rather than SQL here (it would otherwise mean FNV-1a in plpgsql
    /// plus both name tables repeated as SQL literals, which must then agree
    /// with FolkNameRegistry for ever).
    /// </summary>
    public partial class AddCharacterName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "characters",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Name",
                table: "characters");
        }
    }
}
