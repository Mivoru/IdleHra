using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Economy;
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
    /// The Deep's 7-day gold high-water mark (task 37 spec §3.4): written by the
    /// durable checkpoint and never by the Redis frame, one row a day, eight at
    /// most, and read by the stake.
    /// </summary>
    [Collection("Postgres collection")]
    public class GoldHighWaterTests
    {
        private readonly PostgresTestFixture _fixture;

        public GoldHighWaterTests(PostgresTestFixture fixture) => _fixture = fixture;

        private static readonly DateOnly Day0 = new DateOnly(2026, 9, 24);

        private async Task ResetAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM player_gold_daily_high WHERE \"PlayerId\" = {0}", playerId);
        }

        private async Task<PlayerGoldDailyHigh[]> RowsAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.PlayerGoldDailyHighs.AsNoTracking().Where(h => h.PlayerId == playerId).OrderBy(h => h.DayUtc).ToArrayAsync();
        }

        [Fact]
        public async Task TwoFlushesTheSameDayKeepTheHigher()
        {
            const long playerId = 981000001L;
            await ResetAsync(playerId);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            await GoldHighWater.RecordAsync(db, playerId, 10_000_000, Day0);
            await GoldHighWater.RecordAsync(db, playerId, 4_000_000, Day0);

            var rows = await RowsAsync(playerId);
            Assert.Single(rows);
            Assert.Equal(10_000_000, rows[0].MaxGold);
        }

        [Fact]
        public async Task OldRowsArePrunedAndAPlayerNeverHoldsMoreThanEight()
        {
            const long playerId = 981000002L;
            await ResetAsync(playerId);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            for (int day = 0; day < 20; day++)
            {
                await GoldHighWater.RecordAsync(db, playerId, 1_000 + day, Day0.AddDays(day));
                Assert.True((await RowsAsync(playerId)).Length <= 8, $"more than eight rows after day {day}");
            }

            var rows = await RowsAsync(playerId);
            Assert.Equal(8, rows.Length);
            Assert.Equal(Day0.AddDays(12), rows[0].DayUtc);
        }

        [Fact]
        public async Task TheSevenDayMaxSpansTodayAndTheSixDaysBefore()
        {
            const long playerId = 981000003L;
            await ResetAsync(playerId);
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            await GoldHighWater.RecordAsync(db, playerId, 50, Day0);                 // 6 days before day 6
            await GoldHighWater.RecordAsync(db, playerId, 900, Day0.AddDays(-1));    // 7 days before: outside
            await GoldHighWater.RecordAsync(db, playerId, 10, Day0.AddDays(6));      // "today"

            Assert.Equal(50, await GoldHighWater.SevenDayMaxAsync(db, playerId, Day0.AddDays(6)));
            Assert.Equal(900, await GoldHighWater.SevenDayMaxAsync(db, playerId, Day0.AddDays(5)));
            Assert.Equal(0, await GoldHighWater.SevenDayMaxAsync(db, 981999999L, Day0));
        }

        private async Task SeedPlayerAsync(long playerId, long epoch, long gold)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var existing = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
            if (existing != null) db.PlayerRecords.Remove(existing);
            var goldRow = await db.CommodityRecords.SingleOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
            if (goldRow != null) db.CommodityRecords.Remove(goldRow);
            await db.SaveChangesAsync();

            db.PlayerRecords.Add(new PlayerRecord
            {
                Id = playerId,
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                LogicEpochCounter = epoch,
                BaseStrength = 50,
                BaseDexterity = 50,
                BaseConstitution = 50,
                BaseLuck = 25
            });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = "gold", Quantity = gold });
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Modul: A REDIS FRAME IS NOT A CHECKPOINT. With Redis connected and the
        /// tick short of the boundary, TrackState stores a frame and returns;
        /// nothing may reach the high-water table, or a cached balance that never
        /// became durable could price a stake.
        /// </summary>
        [Fact]
        public async Task ARedisFrameOnlyTickWritesNoRow()
        {
            const long playerId = 981000004L;
            await ResetAsync(playerId);
            await SeedPlayerAsync(playerId, epoch: 3, gold: 1_000);

            var multiplexer = _fixture.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            var services = new ServiceCollection();
            services.AddSingleton(_fixture.RetryingOptions);
            services.AddSingleton(new RedisSessionCache(multiplexer));
            var provider = services.BuildServiceProvider();
            Assert.True(provider.GetRequiredService<RedisSessionCache>().IsConnected);

            var manager = new StateCheckpointManager(provider);
            var state = new TickStatePayload
            {
                PlayerId = playerId,
                LogicEpochCounter = 3,
                TicksSinceLastFlush = 5,
                IsDirty = true,
                InventorySpaceRemaining = 20,
                STR = 50, DEX = 50, CON = 50, LCK = 25
            };
            state.SetGold(77_000_000);

            manager.TrackState(ref state);

            Assert.Empty(await RowsAsync(playerId));
        }

        [Fact]
        public async Task ACheckpointRecordsTheLiveBalance()
        {
            const long playerId = 981000005L;
            await ResetAsync(playerId);
            await SeedPlayerAsync(playerId, epoch: 3, gold: 1_000);

            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            var state = new TickStatePayload
            {
                PlayerId = playerId,
                LogicEpochCounter = 3,
                InventorySpaceRemaining = 20,
                STR = 50, DEX = 50, CON = 50, LCK = 25
            };
            state.SetGold(12_345_678);

            Assert.True(await manager.FlushState(state));

            var rows = await RowsAsync(playerId);
            Assert.Single(rows);
            Assert.Equal(12_345_678, rows[0].MaxGold);
            Assert.Equal(GoldHighWater.Today(DateTime.UtcNow), rows[0].DayUtc);
        }

        /// <summary>
        /// A flush the split-brain sieve refuses rolls back before it commits
        /// anything, and the mark is inside that transaction.
        /// </summary>
        [Fact]
        public async Task AFlushThatRollsBackWritesNoRow()
        {
            const long playerId = 981000006L;
            await ResetAsync(playerId);
            await SeedPlayerAsync(playerId, epoch: 50, gold: 1_000);

            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            var state = new TickStatePayload
            {
                PlayerId = playerId,
                LogicEpochCounter = 10, // behind the database: refused
                InventorySpaceRemaining = 20
            };
            state.SetGold(99_000_000);

            Assert.False(await manager.FlushState(state));
            Assert.Empty(await RowsAsync(playerId));
        }

        [Fact]
        public async Task LoginHydrationRecordsTheLoadedGold()
        {
            const long playerId = 981000007L;
            await ResetAsync(playerId);
            await SeedPlayerAsync(playerId, epoch: 3, gold: 31_000_000);

            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            await manager.LoadPlayerState(playerId);

            var rows = await RowsAsync(playerId);
            Assert.Single(rows);
            Assert.Equal(31_000_000, rows[0].MaxGold);
        }
    }
}
