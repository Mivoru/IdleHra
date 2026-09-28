using System;
using System.Threading.Tasks;

using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// Modul: TASK 51 - PERSONAL RECORDS. Four lifetime bests, each written by
    /// the one system that already sees the event:
    ///
    ///   - the highest single hit and the fastest kill of each region boss are
    ///     TICK facts, so they live on the payload, ride the checkpoint to
    ///     PlayerRecords and are hydrated at login (CLAUDE.md: a field on
    ///     StateUpdatePacket must be loaded at login, or a relogin reads zero);
    ///   - the best drop is a LOOT WORKER fact, written to PlayerRecords by that
    ///     worker alone, after its own commit (see RecordBestDropAsync);
    ///   - the deepest Delve floor already existed (DelveDeepestFloor).
    ///
    /// Gold earned in one hour is not here: it needs hourly buckets, and task
    /// 56 builds those for its rates anyway.
    ///
    /// A record only ever improves. Every merge below is max (or min for a
    /// time), so an older snapshot arriving late can never undo a newer one.
    /// </summary>
    public static class PersonalRecords
    {
        public const int BossRegions = 5;

        /// <summary>Called where the tick publishes a player hit to the combat feed.</summary>
        public static void ObserveHit(ref TickStatePayload payload, int wholeHitPoints)
        {
            if (wholeHitPoints <= payload.BestHit) return;
            payload.BestHit = wholeHitPoints;
            payload.IsDirty = true;
        }

        /// <summary>Called on every region-boss kill, first clear or farm.</summary>
        public static void ObserveBossKill(ref TickStatePayload payload, int region, int fightTenths)
        {
            if (region < 1 || region > BossRegions || fightTenths <= 0) return;
            int best = GetBossBest(in payload, region);
            if (best != 0 && fightTenths >= best) return;
            SetBossBest(ref payload, region, fightTenths);
            payload.IsDirty = true;
        }

        public static int GetBossBest(in TickStatePayload payload, int region) => region switch
        {
            1 => payload.BossBestKillTenthsR1,
            2 => payload.BossBestKillTenthsR2,
            3 => payload.BossBestKillTenthsR3,
            4 => payload.BossBestKillTenthsR4,
            5 => payload.BossBestKillTenthsR5,
            _ => 0,
        };

        private static void SetBossBest(ref TickStatePayload payload, int region, int tenths)
        {
            switch (region)
            {
                case 1: payload.BossBestKillTenthsR1 = tenths; break;
                case 2: payload.BossBestKillTenthsR2 = tenths; break;
                case 3: payload.BossBestKillTenthsR3 = tenths; break;
                case 4: payload.BossBestKillTenthsR4 = tenths; break;
                case 5: payload.BossBestKillTenthsR5 = tenths; break;
            }
        }

        /// <summary>The better of two times, where 0 means "never killed".</summary>
        public static int FasterOf(int a, int b)
        {
            if (a <= 0) return Math.Max(0, b);
            if (b <= 0) return a;
            return Math.Min(a, b);
        }

        /// <summary>Checkpoint: fold the payload's records into the locked row.</summary>
        public static void MergeInto(PlayerRecord row, in TickStatePayload state)
        {
            row.BestHit = Math.Max(row.BestHit, state.BestHit);
            row.BossBestKillTenthsR1 = FasterOf(row.BossBestKillTenthsR1, state.BossBestKillTenthsR1);
            row.BossBestKillTenthsR2 = FasterOf(row.BossBestKillTenthsR2, state.BossBestKillTenthsR2);
            row.BossBestKillTenthsR3 = FasterOf(row.BossBestKillTenthsR3, state.BossBestKillTenthsR3);
            row.BossBestKillTenthsR4 = FasterOf(row.BossBestKillTenthsR4, state.BossBestKillTenthsR4);
            row.BossBestKillTenthsR5 = FasterOf(row.BossBestKillTenthsR5, state.BossBestKillTenthsR5);
        }

        /// <summary>
        /// The best drop, as one conditional UPDATE: it touches the row only when
        /// the drop beats what is stored, and a tie keeps the earlier record.
        ///
        /// Modul: run AFTER the loot transaction commits, never inside it. That
        /// transaction is Serializable and the checkpoint takes this same row
        /// FOR UPDATE, so reading the row there would add a serialization
        /// conflict to the hottest path in the game - for a column that changes
        /// a dozen times in an account's life. The checkpoint never assigns these
        /// columns, so the two writers cannot overwrite each other.
        /// </summary>
        public static Task<int> RecordBestDropAsync(FolkIdleDbContext db, long playerId, int tier, string baseItemId, DateTime atUtc)
        {
            return db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"PlayerRecords\" SET \"BestDropTier\" = {tier}, \"BestDropBaseId\" = {baseItemId}, \"BestDropAtUtc\" = {atUtc} WHERE \"Id\" = {playerId} AND \"BestDropTier\" < {tier}");
        }
    }
}
