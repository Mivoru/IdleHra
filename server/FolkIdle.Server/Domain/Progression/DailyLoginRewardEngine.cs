using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// The seven-day login week: claimed by hand from a window, once per UTC day.
    /// </summary>
    /// <remarks>
    /// Modul: THE LOGIN WEEK, REBUILT (owner, 2026-11-15). Two measured faults
    /// made the old one worthless:
    ///
    /// 1. It paid a FLAT 500-10,000 gold (x3 at most). Measured on production
    ///    over the seven days to 2026-11-15, the three real players earned
    ///    about 10M, 10M and 84M gold an hour, so the best day of the week was
    ///    0.3% of one hour's income. Gold is now minutes of the player's own
    ///    earned income (GoldRewardScale), the answer the world boss and the
    ///    gold dungeon already use, so it is worth the same at level 10 and
    ///    level 116.
    /// 2. It was granted inside /api/v1/auth/login, and players almost never
    ///    sign in with a password any more - a rotating refresh token renews
    ///    the session. One active player had not been credited for ten days.
    ///    The reward is now CLAIMED from a window the client opens at the
    ///    first session of the day, so it no longer depends on how the session
    ///    began.
    ///
    /// A missed day PAUSES the week instead of resetting it (owner): the next
    /// claim continues from the day after the last one claimed. Nobody loses a
    /// six-day streak to one busy day.
    ///
    /// State on PlayerRecord: LoginRewardClaimDay is the UTC day number of the
    /// last claim; LoginStreakDays is the week day last claimed (0 = none
    /// yet, 7 = a week just finished, so the next claim is day 1 again).
    /// LastLoginTimestamp is no longer the reward's clock - it is a plain
    /// "signed in" stamp, which guild succession reads.
    /// </remarks>
    public static class DailyLoginRewardEngine
    {
        public const int DaysInWeek = 7;

        /// <summary>One day of the week. Zero means "not on this day".</summary>
        public sealed record LoginDayReward(
            int Day,
            int GoldMinutes,
            int Diamonds,
            LootChestTier? LootChest,
            int WardrobeChestRarity,
            int TicketsPerGate);

        /// <summary>
        /// The week. Something every day, a chest every other day, the best on
        /// day seven. Gold is minutes of income: ten hours a week in all.
        /// Diamonds: 120 a week (the old week paid 100).
        /// </summary>
        public static readonly IReadOnlyList<LoginDayReward> Week = new[]
        {
            new LoginDayReward(1, GoldMinutes: 60, Diamonds: 0, LootChest: null, WardrobeChestRarity: 0, TicketsPerGate: 0),
            new LoginDayReward(2, GoldMinutes: 30, Diamonds: 0, LootChest: null, WardrobeChestRarity: 0, TicketsPerGate: 1),
            new LoginDayReward(3, GoldMinutes: 120, Diamonds: 20, LootChest: null, WardrobeChestRarity: 0, TicketsPerGate: 0),
            new LoginDayReward(4, GoldMinutes: 30, Diamonds: 0, LootChest: LootChestTier.Gold, WardrobeChestRarity: 0, TicketsPerGate: 0),
            new LoginDayReward(5, GoldMinutes: 120, Diamonds: 0, LootChest: null, WardrobeChestRarity: 0, TicketsPerGate: 0),
            new LoginDayReward(6, GoldMinutes: 30, Diamonds: 0, LootChest: null, WardrobeChestRarity: CosmeticRegistry.Epic, TicketsPerGate: 0),
            new LoginDayReward(7, GoldMinutes: 210, Diamonds: 100, LootChest: LootChestTier.Ancient, WardrobeChestRarity: 0, TicketsPerGate: 0),
        };

        internal static long ToDateKey(long epochSeconds) => epochSeconds / 86400L;

        internal static long ToDateKey(DateTime utcNow) => ToDateKey(new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds());

        /// <summary>The week day the next claim pays: the one after the last claimed, wrapping after seven.</summary>
        public static int NextDay(int lastClaimedDay) => lastClaimedDay >= 1 && lastClaimedDay < DaysInWeek ? lastClaimedDay + 1 : 1;

        public static bool CanClaim(long lastClaimDateKey, long todayDateKey) => lastClaimDateKey < todayDateKey;

        public static LoginDayReward RewardFor(int day) => Week[Math.Clamp(day, 1, DaysInWeek) - 1];

        /// <summary>The gold a day pays at this hourly income; 0 on a day with no gold.</summary>
        public static long GoldFor(LoginDayReward day, long hourlyIncome)
            => day.GoldMinutes > 0 ? GoldRewardScale.ForMinutes(hourlyIncome, day.GoldMinutes) : 0L;

        public enum ClaimOutcome { Ok, AlreadyClaimed, PlayerNotFound }

        public sealed record ClaimResult(
            ClaimOutcome Outcome,
            int Day = 0,
            long Gold = 0,
            int Diamonds = 0,
            int DiamondBalance = 0,
            long LootChestId = 0,
            string LootChest = "",
            long WardrobeChestId = 0,
            string WardrobeChest = "",
            int TicketsGranted = 0);

        /// <summary>
        /// Notes a password sign-in. No reward: that is claimed by hand now.
        /// Kept because guild succession reads LastLoginTimestamp as "this
        /// player is still around".
        /// </summary>
        public static async Task NoteSignInAsync(RetryingDbContextOptions retryingDbOptions, Guid accountId, long nowEpoch)
        {
            try
            {
                await using var context = new FolkIdleDbContext(retryingDbOptions.Options);
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"PlayerRecords\" SET \"LastLoginTimestamp\" = {nowEpoch} WHERE \"PlayerGuid\" = {accountId}");
            }
            catch (Exception ex)
            {
                // A stamp that failed never blocks a sign-in.
                Console.WriteLine($"Sign-in stamp failed - AccountId {accountId}: {ex.Message}");
            }
        }

        public static Task NoteSignInAsync(RetryingDbContextOptions retryingDbOptions, Guid accountId)
            => NoteSignInAsync(retryingDbOptions, accountId, GameClock.UtcNowOffset.ToUnixTimeSeconds());

        /// <summary>
        /// Pays today's day of the week, once per UTC day, in one transaction.
        /// The PlayerRecords row lock makes a double tap, a retried request or
        /// two devices find the day already claimed the second time.
        /// </summary>
        /// <remarks>
        /// Gold and diamonds move in the DATABASE here. The caller must tell the
        /// live session (ReloadState for gold, BillingSync for diamonds) - the
        /// checkpoint writes both from the payload, and would otherwise put the
        /// old balances back. The gold dungeon does the same.
        /// </remarks>
        public static async Task<ClaimResult> ClaimAsync(FolkIdleDbContext db, long playerId, DateTime utcNow)
        {
            long today = ToDateKey(utcNow);

            // Read before the lock: both are slow-moving and neither is written here.
            long hourlyIncome = await GoldRewardScale.HourlyIncomeAsync(db, playerId, utcNow);
            int region = await ChestRegionAsync(db, playerId);

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var player = await db.PlayerRecords
                .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                .SingleOrDefaultAsync();
            if (player == null) return new ClaimResult(ClaimOutcome.PlayerNotFound);
            if (!CanClaim(player.LoginRewardClaimDay, today))
            {
                await tx.SafeRollbackAsync();
                return new ClaimResult(ClaimOutcome.AlreadyClaimed, Day: player.LoginStreakDays, DiamondBalance: player.PremiumDiamonds);
            }

            int day = NextDay(player.LoginStreakDays);
            var reward = RewardFor(day);
            player.LoginRewardClaimDay = today;
            player.LoginStreakDays = day;
            if (day == DaysInWeek) player.LoginCyclesCompleted++;
            if (reward.Diamonds > 0) player.PremiumDiamonds += reward.Diamonds;

            int tickets = 0;
            if (reward.TicketsPerGate > 0)
            {
                var gates = await DungeonEngine.LoadAsync(db, playerId, utcNow);
                foreach (var row in gates.Values)
                {
                    for (int i = 0; i < reward.TicketsPerGate; i++)
                    {
                        if (DungeonEngine.AddBonusTicket(row)) tickets++;
                    }
                }
            }

            await db.SaveChangesAsync();

            long gold = GoldFor(reward, hourlyIncome);
            if (gold > 0)
            {
                await CommodityLedger.AddAsync(db, playerId, "gold", gold);
                await GoldLedger.RecordIncomeAsync(db, playerId, GoldIncomeSource.LoginReward, gold);
            }

            long lootChestId = 0;
            if (reward.LootChest is LootChestTier tier)
            {
                lootChestId = await LootChestEngine.GrantAsync(db, playerId, tier, region, LootChestSource.LoginReward, utcNow);
            }

            long wardrobeChestId = 0;
            if (reward.WardrobeChestRarity > 0)
            {
                wardrobeChestId = (await CosmeticEngine.InsertChestsAsync(
                    db, playerId, new[] { reward.WardrobeChestRarity }, CosmeticSource.LoginReward, utcNow)).Single();
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            return new ClaimResult(
                ClaimOutcome.Ok,
                Day: day,
                Gold: gold,
                Diamonds: reward.Diamonds,
                DiamondBalance: player.PremiumDiamonds,
                LootChestId: lootChestId,
                LootChest: reward.LootChest?.ToString() ?? string.Empty,
                WardrobeChestId: wardrobeChestId,
                WardrobeChest: reward.WardrobeChestRarity > 0 ? CosmeticRegistry.RarityNames[reward.WardrobeChestRarity] : string.Empty,
                TicketsGranted: tickets);
        }

        /// <summary>
        /// The region a loot chest rolls for: the highest the player has
        /// opened, from the codex, the same consecutive rule as the map.
        /// </summary>
        internal static async Task<int> ChestRegionAsync(FolkIdleDbContext db, long playerId)
        {
            var killed = await db.MonsterCodexEntries.AsNoTracking()
                .Where(c => c.PlayerId == playerId && c.KillCount > 0)
                .Select(c => c.MonsterId)
                .ToListAsync();
            return RegionUnlockGate.HighestUnlockedRegion(killed.ToHashSet());
        }
    }
}
