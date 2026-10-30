using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FolkIdle.Server.Migrations
{
    /// <summary>
    /// Modul: THE ASCENSION RESET (owner decisions A1 + A2, 2026-10-30). NOT
    /// ADDITIVE - it rewrites live rows, so a database backup is taken before
    /// the deploy that carries it (ops/oracle/backup-db.sh).
    ///
    /// The ladder was rebuilt (Enrage from step 4, Ward from step 7, a region
    /// mastery bonus at step 10), so the old step counters no longer describe
    /// the new fights. The conversion the owner approved:
    ///   - every Ascension title and frame already earned STAYS (player_titles
    ///     and cosmetic_items are not touched);
    ///   - each step a player had cleared becomes one loot chest of that boss's
    ///     region (Source 3 = AscensionStep), at the new ladder's odds for that
    ///     step's band - so early players are paid once for what they did and
    ///     nobody re-climbs the same steps for a double reward;
    ///   - every affected player gets one message saying what happened;
    ///   - the step counters go to 0.
    ///
    /// Down cannot restore the counters (the chests may already be opened);
    /// the backup is the way back.
    /// </summary>
    public partial class AscensionV2Reset : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One chest per cleared step, tier by the step's band:
            //   1-3  Bronze 50 / Silver 30 / Gold 15 / Ancient 5
            //   4-6  Bronze 45 / Silver 35 / Gold 15 / Ancient 5
            //   7-9  Bronze 40 / Silver 30 / Gold 25 / Ancient 5
            //   10   Gold 75 / Ancient 25
            migrationBuilder.Sql(Domain.Combat.AscensionResetSql.PayChests);
            migrationBuilder.Sql(Domain.Combat.AscensionResetSql.Notify);
            migrationBuilder.Sql(Domain.Combat.AscensionResetSql.ZeroSteps);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible: the counters are restored from the pre-deploy backup.
        }
    }
}
