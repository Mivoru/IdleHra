using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Models;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Progression
{
    // Modul: 7-day escalating daily login reward. Keyed off
    // PlayerRecord.LastLoginTimestamp/LoginStreakDays rather than any
    // client-supplied claim, so the grant is server-authoritative and
    // cannot be replayed by re-sending a login request - a second login on
    // the same UTC day is a genuine no-op (Granted: false), not a repeat
    // reward. Called once per HTTP /api/v1/auth/login (see
    // NetworkBroadcastSystem.HandleAuthLogin), after authentication
    // resolves the account, so a failed grant never blocks a successful
    // login - callers treat a false Granted result as "nothing to do,"
    // never as a login failure.
    public static class DailyLoginRewardEngine
    {
        // Modul: Comprehensive Game System Audit, Part 6.3. Rotating
        // retention matrices - previously one static 7-day table repeated
        // forever. Now a small set of matrices is keyed by the UTC week
        // number (todayDateKey / 7, pure integer arithmetic on the exact
        // same server-authoritative date key the streak dedupe already
        // uses - no schema change, no client input, no new state), so
        // every weekly reset deterministically shifts the calendar to the
        // next sheet and every player worldwide sees the same active
        // matrix for a given week. Index 0 = day 1 ... index 6 = day 7.
        // All matrices carry the same total weekly value (25500 gold) so
        // rotation changes pacing/shape for engagement, never weekly
        // earning power - matrix A is the original escalating curve,
        // B is front-loaded, C is twin-peaks (mid-week and weekend spikes).
        private static readonly long[][] GoldRewardMatrices =
        {
            new long[] { 500L, 1000L, 1500L, 2500L, 4000L, 6000L, 10000L },
            new long[] { 4000L, 3000L, 2500L, 2500L, 3500L, 4000L, 6000L },
            new long[] { 1000L, 2000L, 6000L, 2000L, 2500L, 4000L, 8000L }
        };
        // Modul: UI audit follow-up. internal (not private) so
        // NetworkBroadcastSystem.HandleLoginBonusState can report the real
        // day-7 bonus in its schedule preview instead of duplicating this
        // number as a second magic constant.
        internal const int PremiumDiamondsOnDay7Completion = 100;

        // Exposed for tests: which matrix is active for a given UTC date
        // key (epoch-seconds / 86400). Deterministic, server-clock-derived,
        // cycles A-B-C weekly.
        internal static int ResolveActiveMatrixIndex(long todayDateKey)
        {
            long weekNumber = todayDateKey / 7L;
            return (int)(weekNumber % GoldRewardMatrices.Length);
        }

        internal static long GetGoldReward(long todayDateKey, int streakDay)
        {
            return GoldRewardMatrices[ResolveActiveMatrixIndex(todayDateKey)][streakDay - 1];
        }

        public readonly struct LoginRewardResult
        {
            public readonly bool Granted;
            public readonly int StreakDay;
            public readonly long GoldGranted;
            public readonly int PremiumDiamondsGranted;

            public LoginRewardResult(bool granted, int streakDay, long goldGranted, int premiumDiamondsGranted)
            {
                Granted = granted;
                StreakDay = streakDay;
                GoldGranted = goldGranted;
                PremiumDiamondsGranted = premiumDiamondsGranted;
            }

            public static readonly LoginRewardResult NotGranted = new LoginRewardResult(false, 0, 0L, 0);
        }

        public static async Task<LoginRewardResult> TryGrantLoginRewardAsync(RetryingDbContextOptions retryingDbOptions, Guid accountId)
        {
            await using var context = new FolkIdleDbContext(retryingDbOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    context.ChangeTracker.Clear();
                    using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                    var player = await context.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"PlayerGuid\" = {0} FOR UPDATE", accountId)
                        .SingleOrDefaultAsync();

                    if (player == null)
                    {
                        await transaction.RollbackAsync();
                        return LoginRewardResult.NotGranted;
                    }

                    long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    long todayDateKey = nowEpoch / 86400L;
                    long lastLoginDateKey = player.LastLoginTimestamp / 86400L;
                    bool hasEverLoggedIn = player.LastLoginTimestamp > 0;

                    if (hasEverLoggedIn && lastLoginDateKey == todayDateKey)
                    {
                        // Already credited for today's UTC day - a repeat
                        // login attempt, not a new reward.
                        await transaction.RollbackAsync();
                        return LoginRewardResult.NotGranted;
                    }

                    bool isConsecutiveDay = hasEverLoggedIn && lastLoginDateKey == todayDateKey - 1;
                    int newStreakDay = isConsecutiveDay
                        ? (player.LoginStreakDays >= 7 ? 1 : player.LoginStreakDays + 1)
                        : 1;

                    player.LastLoginTimestamp = nowEpoch;
                    player.LoginStreakDays = newStreakDay;

                    long goldReward = GetGoldReward(todayDateKey, newStreakDay);
                    int diamondReward = newStreakDay == 7 ? PremiumDiamondsOnDay7Completion : 0;

                    var goldRecord = await context.CommodityRecords
                        .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", player.Id)
                        .SingleOrDefaultAsync();

                    if (goldRecord == null)
                    {
                        goldRecord = new CommodityRecord { PlayerId = player.Id, ItemId = "gold", Quantity = 0L };
                        context.CommodityRecords.Add(goldRecord);
                    }
                    goldRecord.Quantity += goldReward;

                    if (diamondReward > 0)
                    {
                        player.PremiumDiamonds += diamondReward;
                    }

                    await context.SaveChangesAsync();
                    await transaction.CommitAsync();

                    return new LoginRewardResult(true, newStreakDay, goldReward, diamondReward);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Daily login reward grant failed - AccountId {accountId}: {ex.Message}");
                return LoginRewardResult.NotGranted;
            }
        }
    }
}
