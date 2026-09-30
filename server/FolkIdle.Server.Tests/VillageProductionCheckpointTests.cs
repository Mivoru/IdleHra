using System;
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
    /// Modul: LIVE VILLAGE PRODUCTION HAD NO DURABLE PATH WITHOUT REDIS.
    ///
    /// The tick's Lumberjack/Quarry/Mine output lands on PendingWoodDelta /
    /// PendingStoneDelta / PendingIronDelta, and the only thing that ever wrote
    /// those to CommodityRecords was TryStoreFrame -> a Redis buffer ->
    /// RedisWriteBehindEngine. Both return early with Redis down, so a session's
    /// production was shown (CachedWoodStock) and then discarded at logout.
    /// It is gold's old defect, and it gets gold's answer: the checkpoint banks
    /// what Redis did not take, as an increment, carried by the job and handed
    /// back by a failed ack.
    ///
    /// The fixture registers no RedisSessionCache, so every test here but the
    /// last is the Redis-down path.
    /// </summary>
    [Collection("Postgres collection")]
    public class VillageProductionCheckpointTests
    {
        private readonly PostgresTestFixture _fixture;

        public VillageProductionCheckpointTests(PostgresTestFixture fixture) => _fixture = fixture;

        private const string Wood = VillageManagementEngine.WoodCommodityId;
        private const string Stone = VillageManagementEngine.StoneCommodityId;
        private const string Iron = VillageManagementEngine.IronOreCommodityId;

        private async Task<PlayerRecord> CreatePlayerAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"vprod_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                CurrentLevel = 10,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "gold", Quantity = 1_000 });
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = Wood, Quantity = 100 });
            db.CharacterRecords.Add(new CharacterRecord { Id = Guid.NewGuid(), PlayerId = player.Id, AgePhase = 1, SlotIndex = 0 });
            await db.SaveChangesAsync();
            return player;
        }

        private async Task<long> StockAsync(long playerId, string itemId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.CommodityRecords.AsNoTracking().Where(c => c.PlayerId == playerId && c.ItemId == itemId).SumAsync(c => c.Quantity);
        }

        private async Task<long> GatheredAsync(long playerId, string itemId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.MaterialFlowDaily.AsNoTracking()
                .Where(m => m.PlayerId == playerId && m.ItemId == itemId && m.Direction == (short)MaterialFlowDirection.Gathered)
                .SumAsync(m => m.Amount);
        }

        // What SimulationEngine's production step does per whole unit.
        private static void Produce(ref TickStatePayload state, long wood, long stone, long iron)
        {
            state.CachedWoodStock += wood; state.PendingWoodDelta += wood;
            state.CachedStoneStock += stone; state.PendingStoneDelta += stone;
            state.CachedIronOreStock += iron; state.PendingIronDelta += iron;
            state.IsDirty = true;
        }

        [Fact]
        public async Task ThePeriodicCheckpointBanksLiveProduction_Once_AndRecordsItAsGathered()
        {
            var player = await CreatePlayerAsync();
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            try
            {
                var holder = new[] { await manager.LoadPlayerState(player.Id) };
                Produce(ref holder[0], 7, 3, 2);

                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                Assert.Equal(0L, holder[0].PendingWoodDelta); // it went with the job
                await manager.WhenWriterIdleAsync();
                manager.ApplyPendingAcks(ref holder[0]);

                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                await manager.WhenWriterIdleAsync();
                manager.ApplyPendingAcks(ref holder[0]);

                Assert.Equal(107, await StockAsync(player.Id, Wood));
                Assert.Equal(3, await StockAsync(player.Id, Stone));
                Assert.Equal(2, await StockAsync(player.Id, Iron));
                Assert.Equal(7, await GatheredAsync(player.Id, Wood));
                Assert.Equal(3, await GatheredAsync(player.Id, Stone));
                Assert.Equal(2, await GatheredAsync(player.Id, Iron));

                // And a relogin sees it.
                var fresh = await new StateCheckpointManager(_fixture.ServiceProvider).LoadPlayerState(player.Id);
                Assert.Equal(107, fresh.CachedWoodStock);
            }
            finally
            {
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }

        [Fact]
        public async Task AFailedFlushHandsTheProductionBack_AndTheRetryBanksItOnce()
        {
            var player = await CreatePlayerAsync();
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            bool fail = true;
            manager.FlushOverrideForTests = snapshot => fail ? Task.FromResult(false) : manager.FlushState(snapshot);
            try
            {
                var holder = new[] { await manager.LoadPlayerState(player.Id) };
                Produce(ref holder[0], 5, 1, 1);

                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                await manager.WhenWriterIdleAsync();
                manager.ApplyPendingAcks(ref holder[0]);

                Assert.Equal(5L, holder[0].PendingWoodDelta);
                Assert.Equal(1L, holder[0].PendingStoneDelta);
                Assert.Equal(1L, holder[0].PendingIronDelta);
                Assert.Equal(100, await StockAsync(player.Id, Wood));

                fail = false;
                Produce(ref holder[0], 2, 0, 0); // produced meanwhile
                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                await manager.WhenWriterIdleAsync();
                manager.ApplyPendingAcks(ref holder[0]);

                Assert.Equal(0L, holder[0].PendingWoodDelta);
                Assert.Equal(107, await StockAsync(player.Id, Wood));
                Assert.Equal(1, await StockAsync(player.Id, Stone));
                Assert.Equal(7, await GatheredAsync(player.Id, Wood));
            }
            finally
            {
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }

        [Fact]
        public async Task TheLoginCheckpointBanksAndZeroesIt()
        {
            var player = await CreatePlayerAsync();
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            try
            {
                var state = await manager.LoadPlayerState(player.Id);
                Produce(ref state, 4, 0, 0);
                Assert.True(manager.FlushStateAndAdvance(ref state));
                Assert.Equal(0L, state.PendingWoodDelta);
                Assert.True(manager.FlushStateAndAdvance(ref state));
                Assert.Equal(104, await StockAsync(player.Id, Wood));
            }
            finally
            {
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }

        [Fact]
        public async Task TheShutdownBatchBanksIt_Once_AcrossSigtermThenProcessExit()
        {
            var player = await CreatePlayerAsync();
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            var live = await manager.LoadPlayerState(player.Id);
            Produce(ref live, 9, 0, 0);

            Assert.True(await manager.FlushBatch(new[] { live }));
            Assert.True(await manager.FlushBatch(new[] { live }));

            Assert.Equal(109, await StockAsync(player.Id, Wood));
            Assert.Equal(9, await GatheredAsync(player.Id, Wood));
        }

        [Fact]
        public void AReloadCarriesUnbankedProduction_OntoTheStockItShows()
        {
            var live = new TickStatePayload { PlayerId = 88L, CachedWoodStock = 130, PendingWoodDelta = 30, PendingStoneDelta = 2, PendingIronDelta = 1 };
            // What LoadPlayerState read: the row, which does not have the 30.
            var reloaded = new TickStatePayload { PlayerId = 88L, CachedWoodStock = 100, CachedStoneStock = 0, CachedIronOreStock = 0 };

            StateReloadMerge.CarryLiveOnlyFields(in live, ref reloaded);

            Assert.Equal(30L, reloaded.PendingWoodDelta);
            Assert.Equal(130L, reloaded.CachedWoodStock);
            Assert.Equal(2L, reloaded.PendingStoneDelta);
            Assert.Equal(2L, reloaded.CachedStoneStock);
            Assert.Equal(1L, reloaded.PendingIronDelta);
            Assert.Equal(1L, reloaded.CachedIronOreStock);
        }

        /// <summary>
        /// Redis UP: RequestFlush's TryStoreFrame moves the deltas into the
        /// buffers and zeroes them BEFORE the snapshot is taken, so the
        /// checkpoint banks nothing and write-behind banks it all, once.
        /// </summary>
        [Fact]
        public async Task RedisUp_TheCheckpointLeavesItToWriteBehind_SoItIsBankedOnce()
        {
            var player = await CreatePlayerAsync();
            var multiplexer = _fixture.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            var services = new ServiceCollection();
            services.AddSingleton(_fixture.RetryingOptions);
            services.AddSingleton(new RedisSessionCache(multiplexer));
            var provider = services.BuildServiceProvider();
            Assert.True(provider.GetRequiredService<RedisSessionCache>().IsConnected);

            var manager = new StateCheckpointManager(provider);
            try
            {
                var holder = new[] { await manager.LoadPlayerState(player.Id) };
                Produce(ref holder[0], 6, 0, 0);
                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                await manager.WhenWriterIdleAsync();
                manager.ApplyPendingAcks(ref holder[0]);
                Assert.True(await manager.FlushBatch(new[] { holder[0] }));

                Assert.Equal(100, await StockAsync(player.Id, Wood));

                var writeBehind = new RedisWriteBehindEngine(_fixture.ServiceProvider, multiplexer);
                await writeBehind.FlushNowAsync(default);
                await writeBehind.FlushNowAsync(default);

                Assert.Equal(106, await StockAsync(player.Id, Wood));
                Assert.Equal(6, await GatheredAsync(player.Id, Wood));
            }
            finally
            {
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }
    }
}
