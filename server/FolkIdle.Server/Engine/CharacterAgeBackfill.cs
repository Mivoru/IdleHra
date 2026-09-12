using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Repairs characters whose age phase and age ticks contradict each other.
    ///
    /// THE CONTRADICTION. `AgePhase` is a cache; `AgeTicks` is the durable
    /// value, and `SimulationEngine.ProcessAgeSlot` recomputes the phase from
    /// the ticks on every single tick. Three places wrote an adult phase beside
    /// zero ticks, which reads as "this is a grown character" and derives as a
    /// CHILD the instant it is fielded:
    ///
    ///   - CharacterGrantEngine, for a new account's founder pair and for the
    ///     male/female pair a region boss grants.
    ///   - DevFixtureSeeder, for the whole fixture roster.
    ///   - SeasonalRotationEngine, for EVERY surviving ancestor at the rollover.
    ///
    /// All three write the adult threshold now. This is for the rows that were
    /// written before they did - a player's benched race-pair rewards, which
    /// breed perfectly well while benched and become children for an hour the
    /// moment they are fielded, with nothing on any screen saying why.
    ///
    /// NARROW ON PURPOSE. It only touches a row where the two fields disagree.
    /// A bred child is phase 0 with zero ticks - the two agree, it is genuinely
    /// a child, and it is left alone to grow up. Anything that has ever been
    /// fielded has ticks above zero and already derives correctly.
    ///
    /// Idempotent, so the second run does nothing: after the repair the ticks
    /// are no longer zero, and the predicate stops matching.
    /// </summary>
    public static class CharacterAgeBackfill
    {
        /// <summary>Where a repaired character lands: the first tick of adulthood.</summary>
        public static long PromotedTicks => AgePhaseCurve.ChildEndTicks;

        /// <summary>
        /// Whether this row's phase and ticks contradict each other in the one
        /// direction worth repairing - claims to be grown, counts as a newborn.
        /// </summary>
        public static bool NeedsPromotion(int agePhase, long ageTicks)
            => ageTicks == 0L && agePhase >= AgePhaseCurve.Adult;

        public static async Task<int> RunAsync(FolkIdleDbContext db)
        {
            // The predicate is duplicated as a LINQ expression rather than
            // calling NeedsPromotion, because EF has to translate it to SQL -
            // and the two are pinned together by OnlyAContradictionIsRepaired,
            // which drives the C# one through the same cases.
            var contradictory = await db.CharacterRecords
                .Where(c => c.AgeTicks == 0L && c.AgePhase >= AgePhaseCurve.Adult)
                .ToListAsync();

            if (contradictory.Count == 0) return 0;

            for (int i = 0; i < contradictory.Count; i++)
            {
                contradictory[i].AgeTicks = PromotedTicks;
            }

            await db.SaveChangesAsync();
            System.Console.WriteLine(
                $"Aged {contradictory.Count} characters up to adulthood - they claimed to be grown and counted as newborns.");
            return contradictory.Count;
        }
    }
}
