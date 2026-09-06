using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddUnspentAttributePoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UnspentAttributePoints",
                table: "PlayerRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Modul: AND THE BACKFILL, WHICH IS THE REPAIR HALF.
            //
            // Two things are being settled at once here.
            //
            // Attributes used to be dealt automatically on level-up. They are
            // spent by the player now, so every character that already levelled
            // is owed the pool for the levels it gained:
            // AttributePointsPerLevel * (CurrentLevel - 1).
            //
            // And it is a REPAIR, not just a conversion, because
            // OfflineSimulationEngine.ApplyCombatXp never granted attributes at
            // all - it raised the level and paid the skill point and stopped
            // there. In an idle game that is where most levels come from, so
            // most of this debt was never paid in the first place. The only
            // account past level 1 on the live server is level 86 and holds a
            // brand-new registration's 50 / 50 / 50 / 25.
            //
            // What a player already received is subtracted, so nobody is paid
            // twice: the sum of each attribute above its starting value. The
            // starting values are the registration defaults (50/50/50/25) and
            // GREATEST(..., 0) guards a hand-edited row that sits below them.
            //
            // Idempotent by construction - it writes an absolute value derived
            // from level and current attributes, so re-running it is a no-op
            // rather than a second grant.
            migrationBuilder.Sql(@"
                UPDATE ""PlayerRecords""
                SET ""UnspentAttributePoints"" = GREATEST(
                        7 * GREATEST(""CurrentLevel"" - 1, 0)
                        - GREATEST(""BaseStrength""     - 50, 0)
                        - GREATEST(""BaseDexterity""    - 50, 0)
                        - GREATEST(""BaseConstitution"" - 50, 0)
                        - GREATEST(""BaseLuck""         - 25, 0),
                        0)
                WHERE ""CurrentLevel"" > 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnspentAttributePoints",
                table: "PlayerRecords");
        }
    }
}
