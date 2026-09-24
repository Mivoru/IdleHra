using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE WEEK'S DIAMONDS VANISHED FOR ANYONE WHO WAS ONLINE, 2026-09-24.
    ///
    /// LeaderboardPayoutEngine credits PlayerRecords."PremiumDiamonds" and
    /// writes the week key in one transaction - correct for an offline player.
    /// For an ONLINE one the live payload owns PremiumCurrency, and every
    /// checkpoint writes it back with plain assignment
    /// (player.PremiumDiamonds = state.PremiumCurrency). The payload still held
    /// the pre-payout balance, so the next flush put it back, and the week key
    /// meant the payout was never retried. AchievementEngine had the identical
    /// bug and was fixed on 2026-08-02 by handing the authoritative balance to
    /// the tick thread through BillingSyncQueue; this pins the same fix here.
    ///
    /// It never fired in production only because the board has not yet
    /// cleared MinimumRankedPopulation. See
    /// docs/superpowers/specs/2026-09-24-guild-wars-design.md §8.1.
    /// </summary>
    [Collection("Postgres collection")]
    public class LeaderboardPayoutLiveSessionTests
    {
        private const string BoardKey = "leaderboard:mastery";
        private const string LockKey = "lock:leaderboard:payout";

        private readonly PostgresTestFixture _fixture;

        public LeaderboardPayoutLiveSessionTests(PostgresTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task AnOnlinePlayersPayoutSurvivesTheNextCheckpoint()
        {
            const long playerId = 950_030_001L;
            const int startingDiamonds = 100;
            var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
            int rankedPopulation = LeaderboardTierRegistry.MinimumRankedPopulation;
            int expectedPayout = LeaderboardTierRegistry.WeeklyDiamondsFor(1, rankedPopulation);
            Assert.True(expectedPayout > 0, "rank 1 at the population floor must pay something, or this test proves nothing");

            await using (var db = await _fixture.DbContextFactory.CreateDbContextAsync())
            {
                db.PlayerRecords.Add(new PlayerRecord
                {
                    Id = playerId,
                    PlayerGuid = Guid.NewGuid(),
                    AuthenticatorToken = Guid.NewGuid(),
                    CurrentLevel = 20,
                    PremiumDiamonds = startingDiamonds,
                });
                await db.SaveChangesAsync();
            }

            var checkpoints = new StateCheckpointManager(_fixture.ServiceProvider);

            // The player is online: a live payload exists, loaded before the
            // payout, so its PremiumCurrency is the old balance.
            var live = await checkpoints.LoadPlayerState(playerId);
            Assert.Equal(startingDiamonds, live.PremiumCurrency);

            var redis = _fixture.ServiceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
            await redis.KeyDeleteAsync(new RedisKey[] { BoardKey, LockKey });
            try
            {
                // Rank 1 is ours; the rest are ids with no row, which the payout
                // skips, but which make the population clear the floor.
                var entries = new List<SortedSetEntry> { new(playerId.ToString(), 1_000_000) };
                for (int i = 1; i < rankedPopulation; i++)
                {
                    entries.Add(new SortedSetEntry((990_000_000L + i).ToString(), 1_000 - i));
                }
                await redis.SortedSetAddAsync(BoardKey, entries.ToArray());

                var registry = new PlayerSessionRegistry();
                var engine = new LeaderboardPayoutEngine(
                    _fixture.ServiceProvider,
                    _fixture.ServiceProvider.GetRequiredService<IConnectionMultiplexer>(),
                    registry);

                int paid = await engine.PayCurrentWeekAsync(now);
                Assert.Equal(1, paid);

                // One tick: drain whatever the engine handed the tick thread,
                // exactly as the engine loop does, then checkpoint.
                var activePlayers = new Dictionary<long, TickStatePayload> { [playerId] = live };
                BillingTickCoordinator.DrainNotifications(registry, activePlayers);
                live = activePlayers[playerId];

                Assert.True(await checkpoints.FlushState(live));

                await using var verify = await _fixture.DbContextFactory.CreateDbContextAsync();
                var row = await verify.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == playerId);
                Assert.Equal(startingDiamonds + expectedPayout, row.PremiumDiamonds);
                Assert.Equal(startingDiamonds + expectedPayout, live.PremiumCurrency);
            }
            finally
            {
                await redis.KeyDeleteAsync(new RedisKey[] { BoardKey, LockKey });
            }
        }
    }
}
