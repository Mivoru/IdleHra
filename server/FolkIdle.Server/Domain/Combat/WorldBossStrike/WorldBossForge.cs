using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>
    /// Svarog's forge: the duel's shared moment (owner, 2026-11-15: option (a) of
    /// docs/design/WORLD_BOSS_REWORK_2026_11_14.md section 7). Every right call
    /// in a real duel, by anyone, adds one ember; when the forge is full the
    /// hammer falls - the World Boss screen shows it to everyone - and each
    /// player who fed that fill gets a small mail.
    /// </summary>
    /// <remarks>
    /// Modul: COSMETIC BY DESIGN. The hammer deals no damage and moves no health:
    /// the boss's health is sized from Monday's and Tuesday's strikes
    /// (WorldBossHpRules, 5.5-7.0x), and a damage burst would have changed that
    /// arithmetic. The owner chose the version that cannot unbalance the week.
    ///
    /// Modul: THE THRESHOLD IS SIZED FOR THE PLAYERS THERE ARE. Production has
    /// three strikers (player_world_boss_attempts, 2026-11-15); a reader of the
    /// duel makes about two right calls a strike, so about six a day - 15 fills
    /// the forge every two or three days, three times a week at most, and pays
    /// each feeder about 15 diamonds a week on top of the milestones' 60.
    /// Re-measure from ForgeFills when the population changes.
    /// </remarks>
    public static class WorldBossForge
    {
        public const int EmbersToFill = 15;
        public const int DiamondsPerFill = 5;
        public const string SenderName = "Svarog";

        /// <summary>
        /// Feeds the forge with this strike's right calls, inside the strike's
        /// transaction (the snapshot row is locked by the caller). True when
        /// this strike filled it.
        /// </summary>
        public static async Task<bool> FeedAsync(FolkIdleDbContext db, WorldBossSnapshot snapshot, PlayerWorldBossAttempt attempt,
            int rightCalls, DateTime utcNow)
        {
            if (rightCalls <= 0) return false;

            attempt.ForgeFillFed = snapshot.ForgeFills;
            snapshot.ForgeCharge += rightCalls;
            if (snapshot.ForgeCharge < EmbersToFill) return false;

            int fill = snapshot.ForgeFills;
            // One fill per strike; any surplus stays, short of a second fill.
            snapshot.ForgeCharge = Math.Min(snapshot.ForgeCharge - EmbersToFill, EmbersToFill - 1);
            snapshot.ForgeFills++;
            long now = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();
            snapshot.ForgeLastFillEpoch = now;

            // Everyone whose last ember went into this fill. The striker's own
            // row is tracked and not yet saved, so the query cannot see its new
            // value - it is added by hand.
            var feeders = await db.PlayerWorldBossAttempts.AsNoTracking()
                .Where(a => a.BossInstanceId == snapshot.BossInstanceId && a.ForgeFillFed == fill && a.PlayerId != attempt.PlayerId)
                .Select(a => a.PlayerId)
                .ToListAsync();
            feeders.Add(attempt.PlayerId);

            foreach (long playerId in feeders.Distinct())
            {
                db.MailboxInstances.Add(new MailboxInstance
                {
                    PlayerId = playerId,
                    BaseItemId = string.Empty,
                    Quantity = 0,
                    DiamondAttachment = DiamondsPerFill,
                    SenderName = SenderName,
                    MessageText = "You fed my forge, and my hammer fell on Chernobog. Take this for your part.",
                    ReceivedTimestamp = now,
                });
            }
            return true;
        }

        /// <summary>A new encounter starts with a cold forge.</summary>
        public static void Reset(WorldBossSnapshot snapshot)
        {
            snapshot.ForgeCharge = 0;
            snapshot.ForgeFills = 0;
            snapshot.ForgeLastFillEpoch = 0;
        }
    }
}
