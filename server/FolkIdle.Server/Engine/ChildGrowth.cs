using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Children grow up on their own: a bred child is an adult one childhood
    /// (AgePhaseCurve.ChildEndTicks) after it was born, working or not.
    /// </summary>
    /// <remarks>
    /// Modul: SETTLED WHEN LOOKED AT, like VillageArrivalEngine.SettleAsync.
    /// Nothing checks a ten-hertz loop for an event an hour away; what matters
    /// is that the roster is right whenever something reads it - the login
    /// load, both breeding commands before their gate, the Hall and the
    /// breeding roster. One UPDATE, forward-only: it only ever raises a
    /// child's ticks to the end of childhood, never lowers anybody's.
    ///
    /// Before this a child grew only while it worked in one of the three
    /// slots (the adult aging rule, 2026-10-21), so every generation cost a
    /// working slot for an hour. A fielded child still ages by its work as
    /// before; whichever comes first makes it an adult.
    ///
    /// Modul: AND THE LIVE PAYLOAD AGREES. The settle fixes the row, but a
    /// child fielded before its hour was up kept "child" on the session
    /// payload (only a working slot aged) until a relog or a reload - its
    /// stats carried the child penalty while the Hall already said adult.
    /// A fielded child now ages on every tick and starts the session at
    /// its age since birth (AgeTicksOnLoad), so the two agree.
    /// </remarks>
    public static class ChildGrowth
    {
        /// <summary>Seconds from birth to adulthood, derived from the age curve.</summary>
        public static long ChildhoodSeconds => AgePhaseCurve.ChildEndTicks / (AgePhaseCurve.TicksPerHour / 3600L);

        /// <summary>Whether a child born at this second is grown by <paramref name="nowEpoch"/>.</summary>
        public static bool IsGrown(long bornAtEpoch, long nowEpoch) => bornAtEpoch + ChildhoodSeconds <= nowEpoch;

        /// <summary>
        /// The age a slot starts the session with: a child is at least as old
        /// as the time since its birth (in ticks, capped at the end of
        /// childhood), so the live tick - which ages a fielded child every
        /// tick, working or not - finishes the hour on the same second the
        /// SQL settle would. Adults and children of unknown birth are unchanged.
        /// </summary>
        public static long AgeTicksOnLoad(long storedAgeTicks, int storedPhase, long bornAtEpoch, long nowEpoch)
        {
            if (storedPhase != AgePhaseCurve.Child || bornAtEpoch <= 0 || nowEpoch <= bornAtEpoch) return storedAgeTicks;
            long sinceBirth = Math.Min(AgePhaseCurve.ChildEndTicks, (nowEpoch - bornAtEpoch) * (AgePhaseCurve.TicksPerHour / 3600L));
            return Math.Max(storedAgeTicks, sinceBirth);
        }

        /// <summary>Whether the live tick ages this slot: a working slot always, a child always (it grows on its own).</summary>
        public static bool AgesThisTick(int agePhase, bool slotIsWorking) => slotIsWorking || agePhase == AgePhaseCurve.Child;

        /// <summary>Promotes this player's grown children. Returns how many.</summary>
        public static Task<int> SettleAsync(FolkIdleDbContext db, long playerId, long nowEpoch)
        {
            long grownBefore = nowEpoch - ChildhoodSeconds;
            long childEnd = AgePhaseCurve.ChildEndTicks;
            int adult = AgePhaseCurve.Adult;
            int child = AgePhaseCurve.Child;
            return db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE ""characters"" SET ""AgeTicks"" = GREATEST(""AgeTicks"", {childEnd}), ""AgePhase"" = {adult}
                WHERE ""PlayerId"" = {playerId} AND ""AgePhase"" = {child} AND ""BornAtEpoch"" <= {grownBefore}");
        }

        /// <summary>The same, never throwing: a failed settle must not block a login or a screen.</summary>
        public static async Task TrySettleAsync(FolkIdleDbContext db, long playerId, long nowEpoch)
        {
            try
            {
                await SettleAsync(db, playerId, nowEpoch);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ChildGrowth: settle failed for player {playerId}: {ex.Message}");
            }
        }
    }
}
