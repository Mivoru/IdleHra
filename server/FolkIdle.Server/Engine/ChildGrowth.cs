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
    /// </remarks>
    public static class ChildGrowth
    {
        /// <summary>Seconds from birth to adulthood, derived from the age curve.</summary>
        public static long ChildhoodSeconds => AgePhaseCurve.ChildEndTicks / (AgePhaseCurve.TicksPerHour / 3600L);

        /// <summary>Whether a child born at this second is grown by <paramref name="nowEpoch"/>.</summary>
        public static bool IsGrown(long bornAtEpoch, long nowEpoch) => bornAtEpoch + ChildhoodSeconds <= nowEpoch;

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
