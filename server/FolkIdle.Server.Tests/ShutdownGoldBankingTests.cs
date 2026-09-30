using System;
using System.Linq;
using System.Threading.Tasks;
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
    /// Modul: THE SHUTDOWN FLUSH NEVER BANKED THE GOLD IT WAS OWED.
    ///
    /// ShutdownGracefully (SIGTERM, ProcessExit, Ctrl+C) and ExecuteDataDrainage
    /// checkpoint every live session through FlushBatch, not FlushState - and
    /// FlushBatch wrote the level, the attributes, the diamonds and even the
    /// income TALLY for the tick's gold, but never RedisPendingGoldDelta. With
    /// Redis down that delta is the only record of coins earned since the last
    /// checkpoint (and of coins a failed flush's ack handed back during the
    /// shutdown drain), so a deploy dropped them - while gold_income_daily said
    /// they had been earned.
    ///
    /// The fixture registers no RedisSessionCache: this is the Redis-down path.
    /// </summary>
    [Collection("Postgres collection")]
    public class ShutdownGoldBankingTests
    {
        private readonly PostgresTestFixture _fixture;

        public ShutdownGoldBankingTests(PostgresTestFixture fixture) => _fixture = fixture;

        private async Task<PlayerRecord> CreatePlayerAsync()
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var player = new PlayerRecord
            {
                PlayerGuid = Guid.NewGuid(),
                AuthenticatorToken = Guid.NewGuid(),
                Username = $"shut_{Guid.NewGuid():N}".Substring(0, 20),
                SelectedLineageId = 1,
                CurrentLevel = 10,
            };
            db.PlayerRecords.Add(player);
            await db.SaveChangesAsync();
            db.CommodityRecords.Add(new CommodityRecord { PlayerId = player.Id, ItemId = "gold", Quantity = 1_000 });
            db.CharacterRecords.Add(new CharacterRecord { Id = Guid.NewGuid(), PlayerId = player.Id, AgePhase = 1, SlotIndex = 0 });
            await db.SaveChangesAsync();
            return player;
        }

        private async Task<long> GoldAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            return await db.CommodityRecords.AsNoTracking().Where(c => c.PlayerId == playerId && c.ItemId == "gold").SumAsync(c => c.Quantity);
        }

        private static void Earn(ref TickStatePayload state, GoldIncomeSource source, long coins)
        {
            state.AddGold(coins);
            state.RedisPendingGoldDelta += coins;
            GoldLedger.TallyIncome(ref state, source, coins);
        }

        [Fact]
        public async Task TheShutdownFlushBanksTheOwedGold_Once_AcrossSigtermThenProcessExit()
        {
            var player = await CreatePlayerAsync();
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            var live = await manager.LoadPlayerState(player.Id);
            Earn(ref live, GoldIncomeSource.Combat, 250);

            // SIGTERM: ShutdownGracefully hands FlushBatch a COPY of every
            // live payload...
            Assert.True(await manager.FlushBatch(new[] { live }));
            Assert.Equal(1_250, await GoldAsync(player.Id));

            // ...and Environment.Exit then raises ProcessExit, which runs
            // ShutdownGracefully again over the same, unchanged payloads. The
            // epoch sieve must keep the second pass from banking again.
            Assert.True(await manager.FlushBatch(new[] { live }));
            Assert.Equal(1_250, await GoldAsync(player.Id));
        }

        [Fact]
        public async Task GoldAFailedFlushHandedBackDuringTheShutdownDrain_IsBankedByTheBatch()
        {
            var player = await CreatePlayerAsync();
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            manager.FlushOverrideForTests = _ => Task.FromResult(false);

            var holder = new[] { await manager.LoadPlayerState(player.Id) };
            Earn(ref holder[0], GoldIncomeSource.Combat, 90);
            manager.RequestFlush(ref holder[0], FlushReason.Periodic);
            Assert.Equal(0L, holder[0].RedisPendingGoldDelta); // it went with the job

            // What DrainCheckpointWriterForShutdown does: drain, then apply acks.
            Assert.True(manager.DrainWriter(TimeSpan.FromSeconds(10)));
            manager.ApplyPendingAcks(ref holder[0]);
            Assert.Equal(90L, holder[0].RedisPendingGoldDelta); // handed back

            Assert.True(await manager.FlushBatch(new[] { holder[0] }));
            Assert.True(await manager.FlushBatch(new[] { holder[0] }));
            Assert.Equal(1_090, await GoldAsync(player.Id));
        }

        /// <summary>
        /// Redis UP: TrackState's frame moves the delta into the Redis buffer
        /// and zeroes it, so the batch has nothing of it to bank, and the
        /// buffer is banked once by StopAndFlushAsync (which Program.cs runs
        /// right after ShutdownGracefully, twice on SIGTERM + ProcessExit).
        /// </summary>
        [Fact]
        public async Task RedisUp_TheBufferIsBankedByWriteBehind_AndTheBatchDoesNotPayItAgain()
        {
            var player = await CreatePlayerAsync();
            var multiplexer = _fixture.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            var services = new ServiceCollection();
            services.AddSingleton(_fixture.RetryingOptions);
            services.AddSingleton(new RedisSessionCache(multiplexer));
            var provider = services.BuildServiceProvider();
            Assert.True(provider.GetRequiredService<RedisSessionCache>().IsConnected);

            var manager = new StateCheckpointManager(provider);
            var live = await manager.LoadPlayerState(player.Id);
            Earn(ref live, GoldIncomeSource.Combat, 40);
            live.IsDirty = true;
            manager.TrackState(ref live); // not a boundary: the frame takes the gold
            Assert.Equal(0L, live.RedisPendingGoldDelta);
            Earn(ref live, GoldIncomeSource.Combat, 5); // earned on the last tick, never framed

            Assert.True(await manager.FlushBatch(new[] { live }));
            Assert.True(await manager.FlushBatch(new[] { live }));
            Assert.Equal(1_005, await GoldAsync(player.Id));

            var writeBehind = new RedisWriteBehindEngine(_fixture.ServiceProvider, multiplexer);
            await writeBehind.StopAndFlushAsync();
            await writeBehind.StopAndFlushAsync();
            Assert.Equal(1_045, await GoldAsync(player.Id));
        }
    }
}
