using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// Gives the founders the four points every character is supposed to start
    /// with.
    ///
    /// AddBreedingAptitudes added the four columns with `defaultValue: 0`,
    /// which is what EF generates and what nobody looked at twice. But
    /// BreedingAptitudes.StartingValue is FOUR, and every character created
    /// since has had four. So every character that existed before that
    /// migration - every founder on every account older than 2026-08-08 - has
    /// carried 0/0/0/0 ever since.
    ///
    /// That is not cosmetic and it does not wash out. Aptitudes are the ONE
    /// axis a season does not reset, a child inherits each value from one
    /// parent, and mutation drifts about +0.15 a generation - so a line founded
    /// on zeroes starts four points behind on every axis and stays there for as
    /// long as the account exists. It is also invisible: a zero renders exactly
    /// like a four.
    ///
    /// Scoped to GENERATION ZERO with all four still at zero, which is
    /// precisely the population the backfill missed. A bred child cannot reach
    /// all four zeroes from parents at four - mutation moves one point at a
    /// time against a floor - so this cannot silently promote somebody who
    /// earned their numbers.
    /// </summary>
    public partial class BackfillFounderAptitudes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE character_lineage_registry
                SET ""AptitudeStrength"" = 4,
                    ""AptitudeSkill"" = 4,
                    ""AptitudeEndurance"" = 4,
                    ""AptitudeFortune"" = 4
                WHERE ""GenerationIndex"" = 0
                  AND ""AptitudeStrength"" = 0
                  AND ""AptitudeSkill"" = 0
                  AND ""AptitudeEndurance"" = 0
                  AND ""AptitudeFortune"" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty. Down() would have to put the zeroes back, and
            // it cannot tell a founder this migration repaired from one created
            // afterwards with a legitimate four - so the honest inverse is to
            // leave correct data alone.
        }
    }
}
