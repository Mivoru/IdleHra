using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>
    /// The 7-day gold high-water mark the Deep's stake is priced on (task 37
    /// spec §3.4). See PlayerGoldDailyHigh for why it is daily rows.
    ///
    /// Modul: WRITTEN BY THE DURABLE CHECKPOINT, NEVER BY THE REDIS FRAME. "A
    /// Redis frame is not a checkpoint": the frame is twelve fields and a cache,
    /// and a high-water mark written from it could record a balance that never
    /// became durable. StateCheckpointManager.FlushState calls RecordAsync inside
    /// its own Serializable transaction, so a flush that rolls back records
    /// nothing. Also sampled at login hydration (wealth that arrived offline)
    /// and inside DelveEngine's descent, from the locked gold row.
    ///
    /// Raw SQL because the whole point is one statement: GREATEST on conflict,
    /// so two flushes the same day keep the larger, and the prune in the same
    /// round trip. Snake_case table, PascalCase quoted columns.
    /// </summary>
    public static class GoldHighWater
    {
        /// <summary>Days the stake looks back: today and the six before it.</summary>
        public const int WindowDays = 7;

        /// <summary>Rows older than this many days are pruned; with today that is at most eight rows a player.</summary>
        public const int RetainDays = 7;

        public static DateOnly Today(DateTime utcNow) => DateOnly.FromDateTime(utcNow.ToUniversalTime());

        /// <summary>
        /// Records <paramref name="gold"/> as today's high if it is the highest
        /// seen today, and prunes this player's rows older than RetainDays.
        /// Joins whatever transaction <paramref name="db"/> already has open.
        /// </summary>
        public static async Task RecordAsync(FolkIdleDbContext db, long playerId, long gold, DateOnly todayUtc)
        {
            if (playerId <= 0) return;
            long value = Math.Max(0L, gold);
            DateOnly oldestKept = todayUtc.AddDays(-RetainDays);

            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO player_gold_daily_high (\"PlayerId\", \"DayUtc\", \"MaxGold\") VALUES ({0}, {1}, {2}) " +
                "ON CONFLICT (\"PlayerId\", \"DayUtc\") DO UPDATE SET \"MaxGold\" = GREATEST(player_gold_daily_high.\"MaxGold\", EXCLUDED.\"MaxGold\"); " +
                "DELETE FROM player_gold_daily_high WHERE \"PlayerId\" = {0} AND \"DayUtc\" < {3};",
                playerId, todayUtc, value, oldestKept);
        }

        /// <summary>The highest gold recorded across today and the six days before it, or 0.</summary>
        public static async Task<long> SevenDayMaxAsync(FolkIdleDbContext db, long playerId, DateOnly todayUtc)
        {
            DateOnly from = todayUtc.AddDays(-(WindowDays - 1));
            long? max = await db.PlayerGoldDailyHighs.AsNoTracking()
                .Where(h => h.PlayerId == playerId && h.DayUtc >= from && h.DayUtc <= todayUtc)
                .MaxAsync(h => (long?)h.MaxGold);
            return max ?? 0L;
        }
    }
}
