using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 43 (plan item 2a): checkpoints run on CheckpointWriter, not on the
    /// tick. These pin the three things that could go wrong in moving them -
    /// the epoch arithmetic (a false split-brain mails gold and disconnects),
    /// the gold (paid twice or lost), and the command continuation (run after
    /// a failed flush, or silently never answered).
    ///
    /// The fixture's provider registers no RedisSessionCache, so every test
    /// here runs the Redis-down path - the one where the flush itself banks
    /// the gold, which is the path that can pay twice.
    /// </summary>
    [Collection("Postgres collection")]
    public class CheckpointWriterTests
    {
        private readonly PostgresTestFixture _fixture;

        public CheckpointWriterTests(PostgresTestFixture fixture) => _fixture = fixture;

        private async Task SeedAsync(long playerId, long epoch, long gold)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM split_brain_incidents WHERE \"PlayerId\" = {0}", playerId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"MailboxInstances\" WHERE \"PlayerId\" = {0}", playerId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0}", playerId);
            var existing = await db.PlayerRecords.SingleOrDefaultAsync(p => p.Id == playerId);
            if (existing != null) db.PlayerRecords.Remove(existing);
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

        private async Task<(long Epoch, long Gold, int Incidents, int Mail)> ReadAsync(long playerId)
        {
            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            long epoch = await db.PlayerRecords.AsNoTracking().Where(p => p.Id == playerId).Select(p => p.LogicEpochCounter).SingleAsync();
            long gold = await db.CommodityRecords.AsNoTracking().Where(c => c.PlayerId == playerId && c.ItemId == "gold").SumAsync(c => c.Quantity);
            int incidents = await db.SplitBrainIncidents.AsNoTracking().CountAsync(i => i.PlayerId == playerId);
            int mail = await db.MailboxInstances.AsNoTracking().CountAsync(m => m.PlayerId == playerId && m.BaseItemId == "GOLD_COMPENSATION");
            return (epoch, gold, incidents, mail);
        }

        private static TickStatePayload Live(long playerId, long epoch, long gold)
        {
            var state = new TickStatePayload
            {
                PlayerId = playerId,
                LogicEpochCounter = epoch,
                InventorySpaceRemaining = 20,
                STR = 50, DEX = 50, CON = 50, LCK = 25
            };
            state.SetGold(gold);
            return state;
        }

        // Simulates the tick earning coins where no row was written (combat):
        // CurrentGold AND the owed delta, per CLAUDE.md "Two gold paths".
        private static void Earn(ref TickStatePayload state, long coins)
        {
            state.AddGold(coins);
            state.RedisPendingGoldDelta += coins;
        }

        private async Task SettleAsync(StateCheckpointManager manager, TickStatePayload[] holder)
        {
            await manager.WhenWriterIdleAsync();
            manager.ApplyPendingAcks(ref holder[0]);
        }

        /// <summary>(a) Two flushes in flight at E and E+1 both commit; the payload ends at E+2 and nothing is split-brained.</summary>
        [Fact]
        public async Task TwoFlushesInFlightBothCommitAndThePayloadEndsTwoAhead()
        {
            const long playerId = 983000001L;
            await SeedAsync(playerId, epoch: 5, gold: 1_000);
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = new List<long>();
            manager.FlushOverrideForTests = async snapshot =>
            {
                lock (seen) seen.Add(snapshot.LogicEpochCounter);
                await gate.Task;
                return await manager.FlushState(snapshot);
            };

            try
            {
                var holder = new[] { Live(playerId, 5, 1_000) };
                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                manager.RequestFlush(ref holder[0], FlushReason.Command);

                Assert.Equal(2, holder[0].FlushesInFlight);
                Assert.Equal(5, holder[0].LogicEpochCounter);

                gate.SetResult();
                await SettleAsync(manager, holder);

                Assert.Equal(new long[] { 5, 6 }, seen);
                Assert.Equal(0, holder[0].FlushesInFlight);
                Assert.Equal(7, holder[0].LogicEpochCounter);

                var db = await ReadAsync(playerId);
                Assert.Equal(7, db.Epoch);
                Assert.Equal(0, db.Incidents);
                Assert.Equal(0, db.Mail);

                // And the next one, requested from the settled payload, is not refused either.
                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                await SettleAsync(manager, holder);
                Assert.Equal(8, holder[0].LogicEpochCounter);
                Assert.Equal(8, (await ReadAsync(playerId)).Epoch);
            }
            finally
            {
                gate.TrySetResult();
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>(b) The first of two flushes fails, the second commits: no split-brain, and the first one's gold comes back exactly once.</summary>
        [Fact]
        public async Task AFailedFirstFlushHandsItsGoldBackExactlyOnce()
        {
            const long playerId = 983000002L;
            await SeedAsync(playerId, epoch: 5, gold: 1_000);
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            int calls = 0;
            manager.FlushOverrideForTests = snapshot =>
                Interlocked.Increment(ref calls) == 1 ? Task.FromResult(false) : manager.FlushState(snapshot);

            try
            {
                var holder = new[] { Live(playerId, 5, 1_000) };
                Earn(ref holder[0], 300);
                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                Earn(ref holder[0], 200);
                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                Assert.Equal(0L, holder[0].RedisPendingGoldDelta);

                await SettleAsync(manager, holder);

                Assert.Equal(300L, holder[0].RedisPendingGoldDelta);
                Assert.True(holder[0].IsDirty);
                Assert.Equal(0, holder[0].FlushesInFlight);
                Assert.Equal(7, holder[0].LogicEpochCounter);

                var db = await ReadAsync(playerId);
                Assert.Equal(1_200, db.Gold);
                Assert.Equal(7, db.Epoch);
                Assert.Equal(0, db.Incidents);
                Assert.Equal(0, db.Mail);

                // The retry banks the returned 300 - once.
                manager.RequestFlush(ref holder[0], FlushReason.Periodic);
                await SettleAsync(manager, holder);
                Assert.Equal(0L, holder[0].RedisPendingGoldDelta);
                Assert.Equal(1_500, (await ReadAsync(playerId)).Gold);
                Assert.Equal(1_500, holder[0].CurrentGold);
            }
            finally
            {
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>(d) A command whose flush fails never runs its continuation, and the player is un-suspended and told.</summary>
        [Fact]
        public async Task AFailedCommandFlushNeverRunsTheContinuationAndAnswersWithAResultCode()
        {
            const long playerId = 983000004L;
            await SeedAsync(playerId, epoch: 5, gold: 1_000);
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            var registry = new PlayerSessionRegistry();
            manager.BindAckQueue(registry.FlushAckQueue);
            manager.FlushOverrideForTests = _ => Task.FromResult(false);

            try
            {
                var live = Live(playerId, 5, 1_000);
                Earn(ref live, 70);
                live.IsSuspended = true;
                bool ran = false;
                manager.RequestFlush(ref live, FlushReason.Command, then: () => { ran = true; return Task.CompletedTask; });

                var players = new Dictionary<long, TickStatePayload> { [playerId] = live };
                await manager.WhenWriterIdleAsync();
                CheckpointAckTickCoordinator.Drain(registry, players, manager);

                Assert.False(ran);
                var after = players[playerId];
                Assert.False(after.IsSuspended);
                Assert.Equal(70L, after.RedisPendingGoldDelta);
                Assert.Equal(0, after.FlushesInFlight);
                Assert.Contains(registry.CommandResultQueue, r =>
                    r.PlayerId == playerId && r.ResultCode == (byte)FolkIdle.Server.Network.CommandResultCode.CheckpointFailed);
                Assert.Equal(1_000, (await ReadAsync(playerId)).Gold);
            }
            finally
            {
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>The other half of (d): on commit the continuation runs, after the row is written.</summary>
        [Fact]
        public async Task ACommittedCommandFlushRunsTheContinuationAfterTheCommit()
        {
            const long playerId = 983000005L;
            await SeedAsync(playerId, epoch: 5, gold: 1_000);
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);

            try
            {
                var holder = new[] { Live(playerId, 5, 1_000) };
                Earn(ref holder[0], 40);
                long epochSeenByContinuation = -1, goldSeenByContinuation = -1;
                manager.RequestFlush(ref holder[0], FlushReason.Command, then: async () =>
                {
                    var db = await ReadAsync(playerId);
                    epochSeenByContinuation = db.Epoch;
                    goldSeenByContinuation = db.Gold;
                });

                await SettleAsync(manager, holder);

                Assert.Equal(6, epochSeenByContinuation);
                Assert.Equal(1_040, goldSeenByContinuation);
                Assert.Equal(6, holder[0].LogicEpochCounter);
            }
            finally
            {
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>(e) Shutdown drains what is queued: every flush commits before DrainWriter returns.</summary>
        [Fact]
        public async Task ShutdownDrainsTheQueue()
        {
            const long firstId = 983000100L;
            const int players = 12;
            for (int i = 0; i < players; i++)
            {
                await SeedAsync(firstId + i, epoch: 5, gold: 1_000);
            }

            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            manager.FlushOverrideForTests = async snapshot =>
            {
                await Task.Delay(25);
                return await manager.FlushState(snapshot);
            };

            for (int i = 0; i < players; i++)
            {
                var live = Live(firstId + i, 5, 1_000);
                Earn(ref live, 10 + i);
                manager.RequestFlush(ref live, FlushReason.Logout);
            }

            Assert.True(manager.DrainWriter(TimeSpan.FromSeconds(30)));
            Assert.Equal(0, manager.Writer.QueueDepth);

            for (int i = 0; i < players; i++)
            {
                var db = await ReadAsync(firstId + i);
                Assert.Equal(6, db.Epoch);
                Assert.Equal(1_000 + 10 + i, db.Gold);
            }

            // After shutdown a request cannot run; it must not throw on the tick.
            var late = Live(firstId, 6, 1_000);
            manager.RequestFlush(ref late, FlushReason.Periodic);
            Assert.True(manager.Writer.DeadLetters >= 1);
        }

        /// <summary>A logout is retried by the writer, and when every attempt fails its gold is still banked.</summary>
        [Fact]
        public async Task ALogoutThatNeverCommitsStillBanksItsGold()
        {
            const long playerId = 983000006L;
            await SeedAsync(playerId, epoch: 5, gold: 1_000);
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            int calls = 0;
            manager.FlushOverrideForTests = _ => { Interlocked.Increment(ref calls); return Task.FromResult(false); };

            try
            {
                var live = Live(playerId, 5, 1_000);
                Earn(ref live, 250);
                manager.RequestFlush(ref live, FlushReason.Logout);
                await manager.WhenWriterIdleAsync();

                Assert.Equal(CheckpointWriter.LogoutAttempts, calls);
                Assert.Equal(1_250, (await ReadAsync(playerId)).Gold);
                Assert.Equal(1, manager.Writer.DeadLetters);

                // The ack carries nothing back: the rescue owns those coins.
                Assert.True(manager.AckQueue.TryDequeue(out var ack));
                Assert.False(ack.Committed);
                Assert.Equal(0L, ack.GoldDelta);
            }
            finally
            {
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>A failed flush whose player has left by the time the ack lands is rescued, not dropped.</summary>
        [Fact]
        public async Task AFailedFlushForAPlayerWhoLeftIsRescued()
        {
            const long playerId = 983000007L;
            await SeedAsync(playerId, epoch: 5, gold: 1_000);
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            var registry = new PlayerSessionRegistry();
            manager.BindAckQueue(registry.FlushAckQueue);
            manager.FlushOverrideForTests = _ => Task.FromResult(false);

            try
            {
                var live = Live(playerId, 5, 1_000);
                Earn(ref live, 90);
                manager.RequestFlush(ref live, FlushReason.Periodic);
                await manager.WhenWriterIdleAsync();

                CheckpointAckTickCoordinator.Drain(registry, new Dictionary<long, TickStatePayload>(), manager);
                await manager.WhenWriterIdleAsync();

                Assert.Equal(1_090, (await ReadAsync(playerId)).Gold);
            }
            finally
            {
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>A login waits for the player's queued flushes, so it never reads the row a logout is still writing.</summary>
        [Fact]
        public async Task WaitForPendingFlushesCompletesOnlyAfterTheQueuedFlush()
        {
            const long playerId = 983000008L;
            await SeedAsync(playerId, epoch: 5, gold: 1_000);
            var manager = new StateCheckpointManager(_fixture.ServiceProvider);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            manager.FlushOverrideForTests = async snapshot =>
            {
                await gate.Task;
                return await manager.FlushState(snapshot);
            };

            try
            {
                var live = Live(playerId, 5, 1_000);
                manager.RequestFlush(ref live, FlushReason.Logout);
                var wait = manager.WaitForPendingFlushesAsync(playerId);

                await Task.Delay(100);
                Assert.False(wait.IsCompleted);

                gate.SetResult();
                await wait;
                Assert.Equal(6, (await ReadAsync(playerId)).Epoch);
            }
            finally
            {
                gate.TrySetResult();
                manager.DrainWriter(TimeSpan.FromSeconds(10));
            }
        }
    }
}
